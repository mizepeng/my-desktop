using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 系统托盘图标（Shell_NotifyIcon），附带一个隐藏的顶层窗口，用于接收托盘回调和系统广播消息。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
	const int WM_TRAYICON = WM_APP + 1;

	/// <summary>
	/// 用户点击了通知（托盘图标的回调消息，系统把通知显示为 Windows 通知，在通知中心里点也会收到）。
	/// </summary>
	const int NIN_BALLOONUSERCLICK = 0x0405;

	const int SPI_SETWORKAREA = 0x002F;
	const int SPI_SETDESKWALLPAPER = 0x0014;
	const uint IconId = 1;

	/// <summary>
	/// 全局快捷键的编号：搜索、速览，以及速览期间临时注册的 Esc（结束速览）。
	/// </summary>
	public const int SearchHotkeyId = 1;

	public const int PeekHotkeyId = 2;

	public const int EscapeHotkeyId = 3;

	const uint VK_ESCAPE = 0x1B;

	/// <summary>
	/// 添加图标失败后隔多久重试、最多重试几次：登录后任务栏还没就绪时会失败，之后不一定再收到 TaskbarCreated。
	/// </summary>
	static readonly TimeSpan AddRetryInterval = TimeSpan.FromSeconds(3);
	const int AddRetryLimit = 20;

	readonly HwndSource _window;
	readonly int _taskbarCreatedMessage;
	readonly int _commandMessage;
	readonly int _stateMessage;
	readonly string _tooltip;
	IntPtr _icon;
	// 用户点击最近一条通知时要做的事，下一条通知会替换它
	Action? _balloonClicked;
	readonly DispatcherTimer _addRetryTimer;
	int _addRetries;

	public event Action? LeftClick;
	public event Action<POINT>? RightClick;
	public event Action? TaskbarCreated;
	public event Action<int>? CommandReceived;
	public event Action? DisplayChanged;
	public event Action? ThemeChanged;

	/// <summary>
	/// 桌面壁纸换了（毛玻璃背景要跟着重画）。
	/// </summary>
	public event Action? WallpaperChanged;

	/// <summary>
	/// 剪贴板内容变化（用来把被剪切的图标显示成半透明）。
	/// </summary>
	public event Action? ClipboardChanged;

	/// <summary>
	/// 按下了 SetHotkey 注册的全局快捷键，参数是快捷键编号（SearchHotkeyId 等）。
	/// </summary>
	public event Action<int>? HotkeyPressed;

	/// <summary>
	/// 其他进程用 WM_COPYDATA 发来数据（参数是 dwData 和 Unicode 文本），返回是否接收。在窗口过程里同步调用，发送方等着结果，不要在里面弹窗。
	/// </summary>
	public Func<int, string, bool>? DataReceived { get; set; }

	/// <summary>
	/// 只在 Win10 上触发：会话解锁、登录或睡眠唤醒（参数是原因），查 Win10 桌面背景变黑用（见 DesktopDiagnostics）。
	/// </summary>
	public event Action<string>? SessionResumed;

	// 是否注册了会话通知（只在 Win10 上注册），退出时要注销
	bool _sessionNotification;
	// 显示器开关的电源设置通知（只在 Win10 上注册，查黑屏用），退出时要注销
	IntPtr _displayStateNotification;

	public IntPtr Handle => _window.Handle;

	/// <summary>
	/// 其他进程用 stateMessageName 消息查询状态时的返回值。
	/// </summary>
	public Func<int>? StateProvider { get; set; }

	public TrayIcon(string tooltip, string commandMessageName, string stateMessageName)
	{
		_tooltip = tooltip;
		// 必须是顶层窗口（不能用 message-only 窗口），否则收不到 TaskbarCreated 等广播
		var parameters = new HwndSourceParameters("MyDesktopTrayHost")
		{
			Width = 0,
			Height = 0,
			WindowStyle = 0,
			ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
		};
		_window = new HwndSource(parameters);
		_window.AddHook(WndProc);
		_taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
		_commandMessage = RegisterWindowMessage(commandMessageName);
		_stateMessage = RegisterWindowMessage(stateMessageName);
		// 以管理员身份运行时，放行来自普通权限进程（Explorer、第二个实例、右键菜单服务）的消息
		ChangeWindowMessageFilterEx(Handle, _taskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);
		ChangeWindowMessageFilterEx(Handle, _commandMessage, MSGFLT_ALLOW, IntPtr.Zero);
		ChangeWindowMessageFilterEx(Handle, _stateMessage, MSGFLT_ALLOW, IntPtr.Zero);
		ChangeWindowMessageFilterEx(Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
		_icon = LoadAppIcon();
		_addRetryTimer = new DispatcherTimer { Interval = AddRetryInterval };
		_addRetryTimer.Tick += (_, _) => RetryAdd();
		Add();
		AddClipboardFormatListener(Handle);
		if (DesktopHost.IsWindows10)
		{
			_sessionNotification = WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);
			if (!_sessionNotification)
			{
				Log.Warn($"注册会话通知失败（错误码 {Marshal.GetLastWin32Error()}），解锁后不会检测桌面背景");
			}
			var displayState = GUID_CONSOLE_DISPLAY_STATE;
			_displayStateNotification = RegisterPowerSettingNotification(Handle, ref displayState, DEVICE_NOTIFY_WINDOW_HANDLE);
			if (_displayStateNotification == IntPtr.Zero)
			{
				Log.Warn($"注册显示器开关通知失败（错误码 {Marshal.GetLastWin32Error()}）");
			}
		}
	}

	/// <summary>
	/// 在通知区弹出通知；clicked 是用户点击这条通知时要做的事，不需要时传 null。
	/// </summary>
	public void ShowBalloon(string title, string text, Action? clicked = null)
	{
		_balloonClicked = clicked;
		var data = CreateData(NIF_INFO);
		data.szInfoTitle = title;
		data.szInfo = text;
		data.dwInfoFlags = NIIF_INFO;
		Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	/// <summary>
	/// 注册编号为 id 的全局快捷键（先注销这个编号原来的）；传 null 表示不用快捷键。被其他程序占用时返回 false。
	/// </summary>
	public bool SetHotkey(int id, Hotkey? hotkey)
	{
		UnregisterHotKey(Handle, id);
		return hotkey is not { } key || RegisterHotKey(Handle, id, key.NativeModifiers | MOD_NOREPEAT, key.VirtualKey);
	}

	/// <summary>
	/// 速览期间临时把单独的 Esc 注册成全局快捷键：焦点在哪个窗口（分区、搜索框）上按 Esc 都能结束速览。速览结束立即注销，不影响平时的 Esc。
	/// </summary>
	public bool SetEscapeHotkey(bool enabled)
	{
		UnregisterHotKey(Handle, EscapeHotkeyId);
		return !enabled || RegisterHotKey(Handle, EscapeHotkeyId, MOD_NOREPEAT, VK_ESCAPE);
	}

	public void Dispose()
	{
		_addRetryTimer.Stop();
		UnregisterHotKey(Handle, SearchHotkeyId);
		UnregisterHotKey(Handle, PeekHotkeyId);
		UnregisterHotKey(Handle, EscapeHotkeyId);
		RemoveClipboardFormatListener(Handle);
		if (_sessionNotification)
		{
			WTSUnRegisterSessionNotification(Handle);
		}
		if (_displayStateNotification != IntPtr.Zero)
		{
			UnregisterPowerSettingNotification(_displayStateNotification);
			_displayStateNotification = IntPtr.Zero;
		}
		var data = CreateData(0);
		Shell_NotifyIcon(NIM_DELETE, ref data);
		if (_icon != IntPtr.Zero)
		{
			DestroyIcon(_icon);
			_icon = IntPtr.Zero;
		}
		_window.Dispose();
	}

	/// <summary>
	/// 添加托盘图标（启动时、资源管理器重建任务栏后），失败时隔 AddRetryInterval 重试，最多 AddRetryLimit 次。
	/// </summary>
	void Add()
	{
		_addRetryTimer.Stop();
		_addRetries = 0;
		if (TryAdd())
		{
			return;
		}
		Log.Warn($"添加托盘图标失败，{AddRetryInterval.TotalSeconds:0} 秒后重试（任务栏可能还没就绪）");
		_addRetryTimer.Start();
	}

	void RetryAdd()
	{
		_addRetries++;
		if (TryAdd())
		{
			_addRetryTimer.Stop();
			Log.Info($"第 {_addRetries} 次重试添加托盘图标成功");
			return;
		}
		if (_addRetries >= AddRetryLimit)
		{
			_addRetryTimer.Stop();
			Log.Error($"添加托盘图标失败，已重试 {AddRetryLimit} 次，放弃；资源管理器重建任务栏时会再试");
		}
	}

	bool TryAdd()
	{
		var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
		if (Shell_NotifyIcon(NIM_ADD, ref data))
		{
			return true;
		}
		// 上一次添加可能其实成功了、只是返回失败（比如任务栏忙时超时），这时再添加会失败，改一下能成功就说明图标已经在了
		return Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	NOTIFYICONDATA CreateData(uint flags)
	{
		return new NOTIFYICONDATA
		{
			cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
			hWnd = Handle,
			uID = IconId,
			uFlags = flags,
			uCallbackMessage = WM_TRAYICON,
			hIcon = _icon,
			szTip = _tooltip,
			szInfo = string.Empty,
			szInfoTitle = string.Empty,
		};
	}

	IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		// 查 Win10 桌面背景变黑：显示器、设置、会话、电源等系统消息先记一条日志，不影响下面的处理
		if (DesktopHost.IsWindows10)
		{
			DesktopDiagnostics.OnTrayMessage(msg, wParam, lParam);
		}
		if (msg == WM_TRAYICON)
		{
			switch ((int)lParam)
			{
				case WM_LBUTTONUP:
					LeftClick?.Invoke();
					break;
				case WM_RBUTTONUP:
					RightClick?.Invoke(NativeMethods.GetCursorPos());
					break;
				case NIN_BALLOONUSERCLICK:
					var clicked = _balloonClicked;
					_balloonClicked = null;
					clicked?.Invoke();
					break;
			}
			handled = true;
		}
		else if (msg == _taskbarCreatedMessage)
		{
			// Explorer 重启后托盘区被重建，需要重新添加图标
			Add();
			TaskbarCreated?.Invoke();
		}
		else if (msg == _commandMessage)
		{
			CommandReceived?.Invoke((int)wParam);
			handled = true;
		}
		else if (msg == _stateMessage)
		{
			handled = true;
			return new IntPtr(StateProvider?.Invoke() ?? 0);
		}
		else if (msg == WM_COPYDATA)
		{
			handled = true;
			var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
			var text = data.lpData == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(data.lpData, data.cbData / 2);
			return DataReceived?.Invoke((int)data.dwData, text) == true ? new IntPtr(1) : IntPtr.Zero;
		}
		else if (msg == WM_DISPLAYCHANGE)
		{
			DisplayChanged?.Invoke();
		}
		else if (msg == WM_CLIPBOARDUPDATE)
		{
			ClipboardChanged?.Invoke();
		}
		else if (msg == WM_HOTKEY && (int)wParam is SearchHotkeyId or PeekHotkeyId or EscapeHotkeyId)
		{
			HotkeyPressed?.Invoke((int)wParam);
			handled = true;
		}
		else if (DesktopHost.IsWindows10 && msg == WM_WTSSESSION_CHANGE && (int)wParam is WTS_SESSION_UNLOCK or WTS_SESSION_LOGON)
		{
			SessionResumed?.Invoke((int)wParam == WTS_SESSION_UNLOCK ? "会话解锁" : "会话登录");
		}
		else if (DesktopHost.IsWindows10 && msg == WM_POWERBROADCAST && (int)wParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC)
		{
			SessionResumed?.Invoke("睡眠唤醒");
		}
		else if (msg == WM_SETTINGCHANGE)
		{
			if ((int)wParam == SPI_SETWORKAREA)
			{
				DisplayChanged?.Invoke();
			}
			else if ((int)wParam == SPI_SETDESKWALLPAPER)
			{
				WallpaperChanged?.Invoke();
			}
			else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
			{
				ThemeChanged?.Invoke();
			}
		}
		return IntPtr.Zero;
	}

	static IntPtr LoadAppIcon()
	{
		int size = GetSystemMetrics(SM_CXSMICON);
		var icons = new IntPtr[1];
		var exe = Environment.ProcessPath;
		if (exe != null && PrivateExtractIcons(exe, 0, size, size, icons, IntPtr.Zero, 1, 0) > 0)
		{
			return icons[0];
		}
		return IntPtr.Zero;
	}
}
