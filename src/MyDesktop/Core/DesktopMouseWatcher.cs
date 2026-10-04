using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 低级鼠标钩子：检测「双击桌面空白处」以及「在桌面上按住右键拖动画框」。
/// 钩子运行在独立线程上，界面线程偶尔忙碌也不会拖慢全系统的鼠标响应。
/// </summary>
internal sealed class DesktopMouseWatcher : IDisposable
{
	/// <summary>
	/// 重放右键单击时写入 dwExtraInfo 的标记，用于让自己注入的事件直接放行。
	/// </summary>
	static readonly IntPtr ReplayMarker = new(0x4D59444B);

	readonly LowLevelMouseProc _callback;
	readonly Dispatcher _uiDispatcher;
	Thread? _thread;
	Dispatcher? _hookDispatcher;
	IntPtr _hook;
	volatile bool _doubleClickEnabled;
	volatile bool _drawEnabled;

	// 以下状态只在钩子线程上读写
	uint _lastTime;
	POINT _lastPoint;
	bool _rightDown;
	bool _drawing;
	POINT _rightStart;

	// 以下状态只在界面线程上读写
	bool _checking;

	public DesktopMouseWatcher(Dispatcher uiDispatcher)
	{
		_uiDispatcher = uiDispatcher;
		// 委托必须由字段持有，否则被 GC 回收后钩子回调会崩溃
		_callback = HookCallback;
	}

	public event Action? DoubleClicked;

	/// <summary>
	/// 画框过程中矩形变化（屏幕物理像素），在界面线程上触发。
	/// </summary>
	public event Action<RECT>? DrawUpdated;

	/// <summary>
	/// 画框结束，在界面线程上触发。
	/// </summary>
	public event Action<RECT>? DrawFinished;

	public bool DoubleClickEnabled
	{
		get => _doubleClickEnabled;
		set => _doubleClickEnabled = value;
	}

	public bool DrawEnabled
	{
		get => _drawEnabled;
		set => _drawEnabled = value;
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
			ready.Set();
			// 低级钩子要求安装线程持续泵送消息
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "DesktopMouseHook",
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
		});
		dispatcher.InvokeShutdown();
		_thread?.Join(1000);
		_thread = null;
		_hookDispatcher = null;
	}

	public void Dispose() => Stop();

	IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
	{
		// 钩子回调超时会被系统摘除，只做轻量判断，重活异步处理；
		// 回调由系统直接调用，异常必须就地捕获，否则会终止进程
		try
		{
			if (code >= 0 && HandleMouse((int)wParam, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)))
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
		bool injected = (info.flags & LLMHF_INJECTED) != 0;
		switch (message)
		{
			case WM_LBUTTONDOWN:
				if (_doubleClickEnabled)
				{
					TrackDoubleClick(info);
				}
				return false;
			case WM_RBUTTONDOWN:
				// 先扣下右键按下：若随后拖动就画框，若只是单击则原样重放，桌面右键菜单照常弹出
				if (_drawEnabled && !injected && IsOverDesktop(info.pt))
				{
					_rightDown = true;
					_drawing = false;
					_rightStart = info.pt;
					return true;
				}
				return false;
			case WM_MOUSEMOVE:
				if (_rightDown)
				{
					TrackDraw(info.pt);
				}
				return false;
			case WM_RBUTTONUP:
				if (_rightDown && !injected)
				{
					_rightDown = false;
					FinishRightButton(info.pt);
					return true;
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

	void TrackDraw(POINT point)
	{
		if (!_drawing)
		{
			if (Math.Abs(point.X - _rightStart.X) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(point.Y - _rightStart.Y) < GetSystemMetrics(SM_CYDRAG))
			{
				return;
			}
			_drawing = true;
		}
		var rect = Normalize(_rightStart, point);
		_uiDispatcher.BeginInvoke(() => DrawUpdated?.Invoke(rect));
	}

	void FinishRightButton(POINT point)
	{
		var rect = Normalize(_rightStart, point);
		bool drew = _drawing;
		_drawing = false;
		// 拖得太小视为手滑，按普通右键处理
		if (drew && rect.Width >= 48 && rect.Height >= 32)
		{
			_uiDispatcher.BeginInvoke(() => DrawFinished?.Invoke(rect));
			return;
		}
		if (drew)
		{
			_uiDispatcher.BeginInvoke(() => DrawFinished?.Invoke(default));
		}
		// 不能在钩子回调里直接注入输入，放到钩子线程稍后执行
		_hookDispatcher?.BeginInvoke(ReplayRightClick);
	}

	static void ReplayRightClick()
	{
		var inputs = new INPUT[]
		{
			new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN, dwExtraInfo = ReplayMarker } },
			new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP, dwExtraInfo = ReplayMarker } },
		};
		if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
		{
			Log.Warn($"重放右键失败：{Marshal.GetLastWin32Error()}");
		}
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
				return false;
			}
		}
		return false;
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
