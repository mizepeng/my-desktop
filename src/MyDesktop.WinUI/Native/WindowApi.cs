using System.ComponentModel;
using System.Runtime.InteropServices;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

internal static class WindowApi
{
	public static void Configure(nint hwnd)
	{
		long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
		style &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_APPWINDOW | WS_EX_TOPMOST);
		SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
	}

	public static void SetRegion(nint hwnd, IEnumerable<RECT> rectangles, int radius)
	{
		nint region = CreateRectRgn(0, 0, 0, 0);
		if (region == 0)
		{
			throw new Win32Exception();
		}
		try
		{
			foreach (var rectangle in rectangles)
			{
				var part = CreateRoundRectRgn(rectangle.Left, rectangle.Top, rectangle.Right + 1, rectangle.Bottom + 1, radius * 2, radius * 2);
				if (part == 0)
				{
					throw new Win32Exception();
				}
				try
				{
					if (CombineRgn(region, region, part, 2) == 0)
					{
						throw new Win32Exception();
					}
				}
				finally
				{
					DeleteObject(part);
				}
			}
			if (SetWindowRgn(hwnd, region, true) == 0)
			{
				throw new Win32Exception();
			}
			// 成功后由系统接管区域句柄。
			region = 0;
		}
		finally
		{
			if (region != 0)
			{
				DeleteObject(region);
			}
		}
	}

	public static void ShowError(string message) => MessageBox(0, message, "MyDesktop", 0x10);

	internal delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
	[DllImport("comctl32.dll", SetLastError = true)]
	internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
	[DllImport("comctl32.dll")]
	internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
	[DllImport("comctl32.dll")]
	internal static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
	[DllImport("user32.dll")]
	internal static extern bool ShowWindow(nint hwnd, int command);
	[DllImport("gdi32.dll", SetLastError = true)]
	static extern nint CreateRectRgn(int left, int top, int right, int bottom);
	[DllImport("gdi32.dll", SetLastError = true)]
	static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
	[DllImport("gdi32.dll", SetLastError = true)]
	static extern int CombineRgn(nint destination, nint first, nint second, int mode);
	[DllImport("user32.dll", SetLastError = true)]
	static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	static extern int MessageBox(nint owner, string text, string caption, uint flags);
}
