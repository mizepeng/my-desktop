using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 桌面在前台时被拦下、转给散放图标层处理的按键命令。
/// </summary>
internal enum DesktopKey
{
	Open,
	Delete,
	DeletePermanently,
	Rename,
	SelectAll,
	Copy,
	Cut,
	Paste,
	Undo,
	Redo,
	ContextMenu,
	Properties,
	Left,
	Up,
	Right,
	Down,
	Home,
	End,
}

/// <summary>
/// 把高频的矩形更新合并成「界面线程有空时只处理最新的一个」。鼠标每秒可产生上千次移动，逐个排队的话界面线程
/// 来不及重绘，积压的更新会让选框越来越落后于鼠标；同时用低于重绘的优先级，不挤占界面重绘。
/// </summary>
sealed class RectUpdateCoalescer(Dispatcher dispatcher, Action<RECT> handler)
{
	readonly object _gate = new();
	RECT _latest;
	bool _queued;

	public void Post(RECT rect)
	{
		lock (_gate)
		{
			_latest = rect;
			if (_queued)
			{
				return;
			}
			_queued = true;
		}
		dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
		{
			RECT latest;
			lock (_gate)
			{
				latest = _latest;
				_queued = false;
			}
			handler(latest);
		});
	}
}

/// <summary>
/// 低级鼠标钩子：检测「双击桌面空白处」和桌面空白处的框选（接管桌面图标时左右键都能框选散放图标，开着「框选后新建分区」时左键框选松开后弹出新建分区的选项）；
/// 接管桌面图标后还用低级键盘钩子拦下删除、改名等按键。
/// 资源管理器的图标列表被隐藏后收不到鼠标，系统自己的框选用不上，框选要由本程序来做。鼠标事件一律不扣：右键单击、长按都原样交给系统，
/// 桌面右键菜单和其他软件的右键手势照常工作。右键框选框住了图标时，由跟在鼠标下的透明小窗口接住松开（见 ClickCatcherWindow），
/// 改为弹出这些图标的右键菜单；不能在钩子里吞掉松开，否则系统记录的右键状态会停在按下。
/// 钩子运行在独立线程上，界面线程偶尔忙碌也不会拖慢全系统的鼠标响应。
/// </summary>
internal sealed class DesktopMouseWatcher : IDisposable
{
	readonly LowLevelMouseProc _callback;
	readonly LowLevelKeyboardProc _keyboardCallback;
	readonly Dispatcher _uiDispatcher;
	readonly RectUpdateCoalescer _bandUpdates;
	readonly RectUpdateCoalescer _rightBandUpdates;
	Thread? _thread;
	Dispatcher? _hookDispatcher;
	IntPtr _hook;
	IntPtr _keyboardHook;
	volatile bool _doubleClickEnabled;
	volatile bool _createEnabled;
	volatile bool _takeoverEnabled;
	volatile bool _menuOpen;

	// 以下状态只在钩子线程上读写
	uint _lastTime;
	POINT _lastPoint;
	bool _leftDown;
	bool _banding;
	bool _rightDown;
	bool _rightBanding;
	POINT _rightStart;
	POINT _leftStart;
	bool _leftSwallowed;

	// 以下状态只在界面线程上读写
	bool _checking;

	public DesktopMouseWatcher(Dispatcher uiDispatcher)
	{
		_uiDispatcher = uiDispatcher;
		// 委托必须由字段持有，否则被 GC 回收后钩子回调会崩溃
		_callback = HookCallback;
		_keyboardCallback = KeyboardCallback;
		_bandUpdates = new RectUpdateCoalescer(uiDispatcher, rect => BandUpdated?.Invoke(rect));
		_rightBandUpdates = new RectUpdateCoalescer(uiDispatcher, rect => RightBandUpdated?.Invoke(rect));
	}

	public event Action? DoubleClicked;

	/// <summary>
	/// 在桌面空白处按下鼠标（参数表示是否按着 Ctrl 或 Shift 追加选择），在界面线程上触发。
	/// </summary>
	public event Action<bool>? BlankPressed;

	/// <summary>
	/// 在桌面空白处拖动框选时选框变化（屏幕物理像素），在界面线程上触发。
	/// </summary>
	public event Action<RECT>? BandUpdated;

	/// <summary>
	/// 框选结束（参数为最后的选框，屏幕物理像素），在界面线程上触发；与选框更新同一优先级排队，不会跑到最后一次更新前面。
	/// </summary>
	public event Action<RECT>? BandFinished;

	/// <summary>
	/// 右键框选时选框变化（屏幕物理像素），在界面线程上触发。
	/// </summary>
	public event Action<RECT>? RightBandUpdated;

	/// <summary>
	/// 右键框选结束（参数为松开右键的位置），在界面线程上触发；松开不扣，与选框更新同一优先级排队。
	/// </summary>
	public event Action<POINT>? RightBandFinished;

	/// <summary>
	/// 本程序的窗口在前台、桌面又在最上层时，在桌面空白处按下了鼠标，要由本程序来激活桌面（左键的这次按下已被扣下，右键不扣），在界面线程上触发。
	/// 让系统随点击激活的话，桌面会被提到分区之上闪一下，见 DesktopHost.Lift。
	/// </summary>
	public event Action? DesktopActivationNeeded;

	/// <summary>
	/// 桌面在前台时被拦下的按键命令（参数二表示是否按着 Shift），在界面线程上触发。
	/// </summary>
	public event Action<DesktopKey, bool>? KeyIntercepted;

	/// <summary>
	/// 桌面在前台时输入的字母或数字，用于按首字母定位图标，在界面线程上触发。
	/// </summary>
	public event Action<char>? CharIntercepted;

	/// <summary>
	/// 本程序的菜单（如图标的右键菜单）开着、桌面又在最上层时，在桌面空白处按下了鼠标：这次点击不扣，要用它关掉菜单，
	/// 但系统随后会激活桌面、把它提到分区之上闪一下，先把分区临时置顶，在界面线程上触发。
	/// </summary>
	public event Action? FencesLiftNeeded;

	/// <summary>
	/// 菜单开着时在菜单外按下了鼠标或按了 Esc，要关掉菜单，在界面线程上触发（事件照常放行，Esc 除外）。
	/// </summary>
	public event Action? MenuDismissRequested;

	public bool DoubleClickEnabled
	{
		get => _doubleClickEnabled;
		set => _doubleClickEnabled = value;
	}

	/// <summary>
	/// 开着「框选后新建分区」：没接管桌面图标时也跟踪桌面空白处的左键拖动（只看不扣，选框由资源管理器自己画），松开后报告选框。
	/// </summary>
	public bool CreateEnabled
	{
		get => _createEnabled;
		set => _createEnabled = value;
	}

	/// <summary>
	/// 已接管桌面图标：启用空白处框选和键盘保护。资源管理器的图标视图此时虽被隐藏，
	/// 仍可能保留选中项和键盘焦点，桌面在前台时按 Delete、Ctrl+A 等会作用到看不见的图标上，必须拦下转给图标层。
	/// </summary>
	public bool TakeoverEnabled
	{
		get => _takeoverEnabled;
		set
		{
			_takeoverEnabled = value;
			_hookDispatcher?.BeginInvoke(UpdateKeyboardHook);
		}
	}

	/// <summary>
	/// 本程序在桌面上弹出了原生菜单（框选后新建分区）。框选刚结束时前台是桌面，本程序拿不到前台，
	/// 菜单点外面、按 Esc 都不会自己关掉，由钩子发现后通知关掉；这期间也装上键盘钩子看 Esc。
	/// </summary>
	public bool MenuOpen
	{
		get => _menuOpen;
		set
		{
			_menuOpen = value;
			_hookDispatcher?.BeginInvoke(UpdateKeyboardHook);
		}
	}

	public void Start()
	{
		if (_thread != null)
		{
			return;
		}
		using var ready = new ManualResetEventSlim();
		_thread = new Thread(() =>
		{
			_hookDispatcher = Dispatcher.CurrentDispatcher;
			_hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(null), 0);
			if (_hook == IntPtr.Zero)
			{
				Log.Warn($"安装鼠标钩子失败：{Marshal.GetLastWin32Error()}");
			}
			UpdateKeyboardHook();
			ready.Set();
			// 低级钩子要求安装线程持续泵送消息
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "DesktopMouseHook",
			// 鼠标、键盘事件都要等钩子返回才往下传，系统忙（比如在玩游戏）时也要尽快轮到钩子线程，免得输入延迟
			Priority = ThreadPriority.Highest,
		};
		_thread.Start();
		ready.Wait();
	}

	public void Stop()
	{
		var dispatcher = _hookDispatcher;
		if (dispatcher == null)
		{
			return;
		}
		dispatcher.Invoke(() =>
		{
			if (_hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_hook);
				_hook = IntPtr.Zero;
			}
			if (_keyboardHook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_keyboardHook);
				_keyboardHook = IntPtr.Zero;
			}
		});
		dispatcher.InvokeShutdown();
		_thread?.Join(1000);
		_thread = null;
		_hookDispatcher = null;
	}

	public void Dispose() => Stop();

	/// <summary>
	/// 键盘钩子只在接管桌面图标时安装，平时不给每次按键增加开销；在钩子线程上调用。
	/// </summary>
	void UpdateKeyboardHook()
	{
		bool wanted = _takeoverEnabled || _menuOpen;
		if (wanted && _keyboardHook == IntPtr.Zero)
		{
			_keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, GetModuleHandle(null), 0);
			if (_keyboardHook == IntPtr.Zero)
			{
				Log.Warn($"安装键盘钩子失败：{Marshal.GetLastWin32Error()}");
			}
		}
		else if (!wanted && _keyboardHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_keyboardHook);
			_keyboardHook = IntPtr.Zero;
		}
	}

	IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			if (code >= 0 && _menuOpen && (int)wParam == WM_KEYDOWN && Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode == VK_ESCAPE)
			{
				_uiDispatcher.BeginInvoke(() => MenuDismissRequested?.Invoke());
				return new IntPtr(1);
			}
			// 按着 Win 键的组合键（Win+E、Win+I、Win+D 等）属于系统，一律放行
			if (code >= 0 && _takeoverEnabled && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN && !IsPressed(VK_LWIN) && !IsPressed(VK_RWIN))
			{
				// 不排除注入的按键：屏幕键盘、触摸键盘和宏工具发出的 Delete 同样会作用到隐藏的图标上
				var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
				if (MapKey(info.vkCode) is DesktopKey key && IsDesktopFocused())
				{
					bool shift = IsPressed(VK_SHIFT);
					_uiDispatcher.BeginInvoke(() => KeyIntercepted?.Invoke(key, shift));
					return new IntPtr(1);
				}
				if (MapChar(info.vkCode) is char character && IsDesktopFocused())
				{
					_uiDispatcher.BeginInvoke(() => CharIntercepted?.Invoke(character));
					return new IntPtr(1);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("键盘钩子处理失败", ex);
		}
		return CallNextHookEx(_keyboardHook, code, wParam, lParam);
	}

	static DesktopKey? MapKey(uint vk)
	{
		bool ctrl = IsPressed(VK_CONTROL);
		bool shift = IsPressed(VK_SHIFT);
		bool alt = IsPressed(VK_MENU);
		return (int)vk switch
		{
			VK_DELETE => shift ? DesktopKey.DeletePermanently : DesktopKey.Delete,
			VK_RETURN when alt => DesktopKey.Properties,
			VK_RETURN => DesktopKey.Open,
			VK_F2 when !ctrl && !alt => DesktopKey.Rename,
			'A' when ctrl && !alt => DesktopKey.SelectAll,
			'C' when ctrl && !alt => DesktopKey.Copy,
			'X' when ctrl && !alt => DesktopKey.Cut,
			// 隐藏的图标列表收不到这几个快捷键（用户反馈按了没反应），拦下后转成桌面视图自己的命令
			'V' when ctrl && !alt => DesktopKey.Paste,
			'Z' when ctrl && !alt => DesktopKey.Undo,
			'Y' when ctrl && !alt => DesktopKey.Redo,
			// 资源管理器中 Ctrl+D 也是删除
			'D' when ctrl && !alt => DesktopKey.Delete,
			VK_APPS => DesktopKey.ContextMenu,
			VK_F10 when shift => DesktopKey.ContextMenu,
			VK_LEFT when !ctrl && !alt => DesktopKey.Left,
			VK_UP when !ctrl && !alt => DesktopKey.Up,
			VK_RIGHT when !ctrl && !alt => DesktopKey.Right,
			VK_DOWN when !ctrl && !alt => DesktopKey.Down,
			VK_HOME when !ctrl && !alt => DesktopKey.Home,
			VK_END when !ctrl && !alt => DesktopKey.End,
			_ => null,
		};
	}

	/// <summary>
	/// 不带 Ctrl、Alt 的字母和数字（含小键盘）。
	/// </summary>
	static char? MapChar(uint vk)
	{
		if (IsPressed(VK_CONTROL) || IsPressed(VK_MENU))
		{
			return null;
		}
		return (int)vk switch
		{
			>= 'A' and <= 'Z' or >= '0' and <= '9' => (char)vk,
			>= VK_NUMPAD0 and <= VK_NUMPAD9 => (char)('0' + vk - VK_NUMPAD0),
			_ => null,
		};
	}

	static bool IsPressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

	/// <summary>
	/// 桌面在前台，且没有打开菜单或编辑框（资源管理器的桌面右键菜单要能用回车选择）。
	/// </summary>
	static bool IsDesktopFocused()
	{
		var foreground = GetForegroundWindow();
		if (!DesktopHost.IsDesktopSurface(foreground))
		{
			return false;
		}
		var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
		if (!GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info))
		{
			return true;
		}
		return (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) == 0 && GetClassName(info.hwndFocus) != "Edit";
	}

	IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
	{
		// 钩子回调超时会被系统摘除，只做轻量判断，重活异步处理；
		// 回调由系统直接调用，异常必须就地捕获，否则会终止进程
		try
		{
			// 每个鼠标事件都要等钩子返回才交给前台程序（包括游戏），高回报率鼠标每秒移动上千次：
			// 没在拖动时的移动、滚轮等用不到的消息直接放行，连事件内容都不读
			int message = (int)wParam;
			bool relevant = message switch
			{
				WM_MOUSEMOVE => _leftDown || _rightDown,
				WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP => true,
				_ => false,
			};
			if (code >= 0 && relevant && HandleMouse(message, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)))
			{
				return new IntPtr(1);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("鼠标钩子处理失败", ex);
		}
		return CallNextHookEx(_hook, code, wParam, lParam);
	}

	/// <summary>
	/// 返回 true 表示吞掉该事件。
	/// </summary>
	bool HandleMouse(int message, MSLLHOOKSTRUCT info)
	{
		// 菜单开着时按在菜单外面：关掉菜单，这次点击照常往下处理
		if (_menuOpen && (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN) && GetClassName(GetAncestor(WindowFromPoint(info.pt), GA_ROOT)) != "#32768")
		{
			_uiDispatcher.BeginInvoke(() => MenuDismissRequested?.Invoke());
		}
		switch (message)
		{
			case WM_LBUTTONDOWN:
				if (_doubleClickEnabled)
				{
					TrackDoubleClick(info);
				}
				_leftSwallowed = false;
				// 不排除注入的输入：触摸、笔和自动化工具产生的点击同样要能框选
				if (_takeoverEnabled || _createEnabled)
				{
					TrackBlankPress(info.pt);
					// 扣下这次按下（连同抬起），改由本程序激活桌面；资源管理器收到点击本来也只是激活桌面、取消选择，取消选择已由 BlankPressed 完成
					if (_takeoverEnabled && _leftDown && WillRaiseDesktop())
					{
						_leftSwallowed = true;
						_uiDispatcher.BeginInvoke(() => DesktopActivationNeeded?.Invoke());
					}
					else if (_takeoverEnabled && _leftDown && IsOwnMenuOverRaisableDesktop())
					{
						_uiDispatcher.BeginInvoke(() => FencesLiftNeeded?.Invoke());
					}
				}
				return _leftSwallowed;
			case WM_LBUTTONUP:
				if (_leftDown)
				{
					_leftDown = false;
					if (_banding)
					{
						_banding = false;
						var band = Normalize(_leftStart, info.pt);
						_uiDispatcher.BeginInvoke(DispatcherPriority.Input, () => BandFinished?.Invoke(band));
					}
				}
				if (_leftSwallowed)
				{
					_leftSwallowed = false;
					return true;
				}
				return false;
			case WM_RBUTTONDOWN:
				// 右键按下不扣：和资源管理器一样，在桌面空白处按下右键先取消分区和桌面上的所有选择，随后拖动即框选
				_rightDown = false;
				_rightBanding = false;
				if (_takeoverEnabled && IsOverDesktop(info.pt))
				{
					_rightDown = true;
					_rightStart = info.pt;
					_uiDispatcher.BeginInvoke(() => BlankPressed?.Invoke(false));
					// 系统随这次点击激活桌面时会把它提到分区之上，先由本程序把分区临时置顶
					if (WillRaiseDesktop())
					{
						_uiDispatcher.BeginInvoke(() => DesktopActivationNeeded?.Invoke());
					}
					else if (IsOwnMenuOverRaisableDesktop())
					{
						_uiDispatcher.BeginInvoke(() => FencesLiftNeeded?.Invoke());
					}
				}
				return false;
			case WM_RBUTTONUP:
				if (!_rightDown)
				{
					return false;
				}
				_rightDown = false;
				if (!_rightBanding)
				{
					return false;
				}
				_rightBanding = false;
				var point = info.pt;
				_uiDispatcher.BeginInvoke(DispatcherPriority.Input, () => RightBandFinished?.Invoke(point));
				return false;
			case WM_MOUSEMOVE:
				if (_leftDown)
				{
					TrackBand(info.pt);
				}
				if (_rightDown)
				{
					TrackRightBand(info.pt);
				}
				return false;
			default:
				return false;
		}
	}

	void TrackDoubleClick(MSLLHOOKSTRUCT info)
	{
		uint elapsed = unchecked(info.time - _lastTime);
		bool near = Math.Abs(info.pt.X - _lastPoint.X) * 2 <= GetSystemMetrics(SM_CXDOUBLECLK)
				&& Math.Abs(info.pt.Y - _lastPoint.Y) * 2 <= GetSystemMetrics(SM_CYDOUBLECLK);
		if (_lastTime != 0 && elapsed <= GetDoubleClickTime() && near)
		{
			_lastTime = 0;
			var point = info.pt;
			_uiDispatcher.BeginInvoke(() => OnDoubleClick(point));
		}
		else
		{
			_lastTime = info.time;
			_lastPoint = info.pt;
		}
	}

	/// <summary>
	/// 在桌面空白处按下左键：取消图标层上的选择，随后拖动即框选（与资源管理器一致，按着 Ctrl/Shift 时追加）。
	/// 不吞掉事件，资源管理器照常收到点击。
	/// </summary>
	void TrackBlankPress(POINT point)
	{
		_leftDown = false;
		_banding = false;
		if (!IsOverDesktop(point))
		{
			return;
		}
		_leftDown = true;
		_leftStart = point;
		bool additive = IsPressed(VK_CONTROL) || IsPressed(VK_SHIFT);
		_uiDispatcher.BeginInvoke(() => BlankPressed?.Invoke(additive));
	}

	void TrackRightBand(POINT point)
	{
		if (!_rightBanding)
		{
			if (Math.Abs(point.X - _rightStart.X) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(point.Y - _rightStart.Y) < GetSystemMetrics(SM_CYDRAG))
			{
				return;
			}
			_rightBanding = true;
		}
		_rightBandUpdates.Post(Normalize(_rightStart, point));
	}

	void TrackBand(POINT point)
	{
		if (!_banding)
		{
			if (Math.Abs(point.X - _leftStart.X) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(point.Y - _leftStart.Y) < GetSystemMetrics(SM_CYDRAG))
			{
				return;
			}
			_banding = true;
		}
		_bandUpdates.Post(Normalize(_leftStart, point));
	}

	/// <summary>
	/// 判断屏幕上该点露出的是不是桌面。只读窗口属性、不发消息，可以安全地在钩子回调里调用。
	/// </summary>
	static bool IsOverDesktop(POINT point)
	{
		var desktop = DesktopHost.FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return false;
		}
		for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
		{
			if (hwnd == desktop)
			{
				return true;
			}
			// 散放图标层只有图标处挡住鼠标，其余透明处穿透到桌面
			if (DesktopHost.IsLayer(hwnd))
			{
				if (IsWindowVisible(hwnd) && DesktopHost.HitsLayerIcon(point))
				{
					return false;
				}
				continue;
			}
			if (!IsWindowVisible(hwnd) || (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0)
			{
				continue;
			}
			// 被系统隐藏（cloaked）的窗口虽然「可见」但不在屏幕上
			if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
			{
				continue;
			}
			if (NativeMethods.GetWindowRect(hwnd).Contains(point))
			{
				// 分层窗口的全透明处不接收点击、鼠标穿透到下面，以系统的命中测试为准，例如 NVIDIA 覆盖层铺满全屏的透明窗口。
				// WindowFromPoint 不向其他线程的窗口发消息（实测目标线程无响应时也立即返回），钩子线程自己没有窗口，可以在这里调用
				if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_LAYERED) != 0 && GetAncestor(WindowFromPoint(point), GA_ROOT) != hwnd)
				{
					continue;
				}
				return false;
			}
		}
		return false;
	}

	/// <summary>
	/// 这次按下是否会激活桌面并让它盖住分区：本程序的窗口在前台（菜单开着时除外，这次点击要用来关掉菜单），桌面又在最上层。
	/// 只读窗口属性、不发消息，可以在钩子回调里调用。
	/// </summary>
	static bool WillRaiseDesktop()
	{
		uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
		if (process != (uint)Environment.ProcessId)
		{
			return false;
		}
		var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
		if (GetGUIThreadInfo(thread, ref info) && (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0)
		{
			return false;
		}
		return DesktopHost.IsDesktopOnTop();
	}

	/// <summary>
	/// 本程序的菜单正开着（前台是本程序、处于菜单模式），桌面又在最上层：点桌面关菜单时系统会激活桌面盖住分区。
	/// 只读窗口属性、不发消息，可以在钩子回调里调用。
	/// </summary>
	static bool IsOwnMenuOverRaisableDesktop()
	{
		uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
		if (process != (uint)Environment.ProcessId)
		{
			return false;
		}
		var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
		return GetGUIThreadInfo(thread, ref info) && (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0 && DesktopHost.IsDesktopOnTop();
	}

	static RECT Normalize(POINT a, POINT b)
	{
		return new RECT(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
	}

	async void OnDoubleClick(POINT point)
	{
		if (_checking)
		{
			return;
		}
		_checking = true;
		try
		{
			// WindowFromPoint 会向目标窗口发消息，UI Automation 也是跨进程调用，都放到后台线程，Explorer 无响应时不拖住界面
			if (await Task.Run(() => IsBlankDesktopArea(point)))
			{
				DoubleClicked?.Invoke();
			}
		}
		catch (Exception ex)
		{
			Log.Warn("处理桌面双击失败", ex);
		}
		finally
		{
			_checking = false;
		}
	}

	static bool IsBlankDesktopArea(POINT point)
	{
		if (!DesktopHost.IsDesktopSurface(WindowFromPoint(point)))
		{
			return false;
		}
		// 图标可见时要排除「双击图标打开文件」的情况
		return !DesktopHost.AreIconsVisible() || !IsOverDesktopIcon(point);
	}

	static bool IsOverDesktopIcon(POINT point)
	{
		try
		{
			var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
			var walker = TreeWalker.RawViewWalker;
			for (int depth = 0; element != null && depth < 4; depth++)
			{
				var type = element.Current.ControlType;
				if (type == ControlType.ListItem)
				{
					return true;
				}
				if (type == ControlType.List || type == ControlType.Pane || type == ControlType.Window)
				{
					return false;
				}
				element = walker.GetParent(element);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("UI Automation 检测桌面图标失败", ex);
		}
		return false;
	}
}
