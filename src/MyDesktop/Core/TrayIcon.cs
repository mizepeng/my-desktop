using System.Runtime.InteropServices;
using System.Windows.Interop;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 系统托盘图标（Shell_NotifyIcon），附带一个隐藏的顶层窗口，用于接收托盘回调和系统广播消息。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
	const int WM_TRAYICON = WM_APP + 1;
	const int SPI_SETWORKAREA = 0x002F;
	const int SPI_SETDESKWALLPAPER = 0x0014;
	const uint IconId = 1;
	const int HotkeyId = 1;

	readonly HwndSource _window;
	readonly int _taskbarCreatedMessage;
	readonly int _commandMessage;
	readonly int _stateMessage;
	readonly string _tooltip;
	IntPtr _icon;

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
	/// 按下了 SetHotkey 注册的全局快捷键。
	/// </summary>
	public event Action? HotkeyPressed;

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
		_icon = LoadAppIcon();
		Add();
		AddClipboardFormatListener(Handle);
	}

	public void ShowBalloon(string title, string text)
	{
		var data = CreateData(NIF_INFO);
		data.szInfoTitle = title;
		data.szInfo = text;
		data.dwInfoFlags = NIIF_INFO;
		Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	/// <summary>
	/// 注册全局快捷键（只有一个，先注销原来的）；传 null 表示不用快捷键。被其他程序占用时返回 false。
	/// </summary>
	public bool SetHotkey(Hotkey? hotkey)
	{
		UnregisterHotKey(Handle, HotkeyId);
		return hotkey is not { } key || RegisterHotKey(Handle, HotkeyId, key.NativeModifiers | MOD_NOREPEAT, key.VirtualKey);
	}

	public void Dispose()
	{
		UnregisterHotKey(Handle, HotkeyId);
		RemoveClipboardFormatListener(Handle);
		var data = CreateData(0);
		Shell_NotifyIcon(NIM_DELETE, ref data);
		if (_icon != IntPtr.Zero)
		{
			DestroyIcon(_icon);
			_icon = IntPtr.Zero;
		}
		_window.Dispose();
	}

	void Add()
	{
		var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
		Shell_NotifyIcon(NIM_ADD, ref data);
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
		else if (msg == WM_DISPLAYCHANGE)
		{
			DisplayChanged?.Invoke();
		}
		else if (msg == WM_CLIPBOARDUPDATE)
		{
			ClipboardChanged?.Invoke();
		}
		else if (msg == WM_HOTKEY && (int)wParam == HotkeyId)
		{
			HotkeyPressed?.Invoke();
			handled = true;
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
