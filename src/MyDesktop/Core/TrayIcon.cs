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
	const uint IconId = 1;

	readonly HwndSource _window;
	readonly int _taskbarCreatedMessage;
	readonly int _activateMessage;
	readonly string _tooltip;
	IntPtr _icon;

	public event Action? LeftClick;
	public event Action<POINT>? RightClick;
	public event Action? TaskbarCreated;
	public event Action? ActivateRequested;
	public event Action? DisplayChanged;
	public event Action? ThemeChanged;

	public IntPtr Handle => _window.Handle;

	public TrayIcon(string tooltip, string activateMessageName)
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
		_activateMessage = RegisterWindowMessage(activateMessageName);
		// 以管理员身份运行时，放行来自普通权限进程（Explorer、第二个实例）的广播
		ChangeWindowMessageFilterEx(Handle, _taskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);
		ChangeWindowMessageFilterEx(Handle, _activateMessage, MSGFLT_ALLOW, IntPtr.Zero);
		_icon = LoadAppIcon();
		Add();
	}

	public void ShowBalloon(string title, string text)
	{
		var data = CreateData(NIF_INFO);
		data.szInfoTitle = title;
		data.szInfo = text;
		data.dwInfoFlags = NIIF_INFO;
		Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	public void Dispose()
	{
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
		else if (msg == _activateMessage)
		{
			ActivateRequested?.Invoke();
			handled = true;
		}
		else if (msg == WM_DISPLAYCHANGE)
		{
			DisplayChanged?.Invoke();
		}
		else if (msg == WM_SETTINGCHANGE)
		{
			if ((int)wParam == SPI_SETWORKAREA)
			{
				DisplayChanged?.Invoke();
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
