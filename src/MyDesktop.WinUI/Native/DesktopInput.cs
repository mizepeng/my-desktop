using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

internal static class DesktopInput
{
	public static bool Begin(nint hwnd)
	{
		SetActivationAllowed(hwnd, true);
		// 后台桌面只设置本线程的活动窗口，不能请求切走用户正在使用的前台窗口。
		if (IsInputDesktop())
		{
			return SetForegroundWindow(hwnd) && OwnsForeground(hwnd);
		}
		else
		{
			SetActiveWindow(hwnd);
			return GetActiveWindow() == hwnd;
		}
	}

	public static void End(nint hwnd, nint desktop)
	{
		SetActivationAllowed(hwnd, false);
		// 用户已经切到其他程序时不再改变前台；仍在本窗口中才把输入交回桌面。
		if (IsInputDesktop() && OwnsForeground(hwnd) && desktop != 0)
		{
			if (!SetForegroundWindow(desktop))
			{
				// 桌面正在销毁或前台已经再次切换时，保留已恢复的非激活状态，交由下一次用户输入决定焦点。
				Log.Warn("编辑结束时未能把输入交回桌面，宿主已恢复非激活状态。");
			}
		}
	}

	public static bool OwnsForeground(nint hwnd)
	{
		var foreground = GetForegroundWindow();
		return foreground == hwnd || foreground != 0 && GetAncestor(foreground, 3) == hwnd;
	}

	public static bool IsInputDesktop()
	{
		var input = OpenInputDesktop(0, false, 1);
		if (input == 0)
		{
			int error = Marshal.GetLastWin32Error();
			// 安全桌面拒绝访问时，当前线程也不能向该桌面请求前台输入。
			return error == 5 ? false : throw new Win32Exception(error);
		}
		try
		{
			return NameOf(input) == NameOf(GetThreadDesktop(GetCurrentThreadId()));
		}
		finally
		{
			CloseDesktop(input);
		}
	}

	static string NameOf(nint desktop)
	{
		var name = new StringBuilder(256);
		if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _))
		{
			throw new Win32Exception();
		}
		return name.ToString();
	}

	static void SetActivationAllowed(nint hwnd, bool allowed)
	{
		long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
		SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(allowed ? style & ~WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE));
	}

	[DllImport("user32.dll")]
	static extern nint SetActiveWindow(nint hwnd);
	[DllImport("user32.dll")]
	static extern nint GetActiveWindow();
	[DllImport("kernel32.dll")]
	static extern uint GetCurrentThreadId();
	[DllImport("user32.dll")]
	static extern nint GetThreadDesktop(uint thread);
	[DllImport("user32.dll", SetLastError = true)]
	static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
	[DllImport("user32.dll")]
	static extern bool CloseDesktop(nint desktop);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, int length, out int needed);
}
