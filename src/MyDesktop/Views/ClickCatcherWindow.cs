using System.Runtime.InteropServices;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 右键框选框住了图标时跟在鼠标下面的透明小窗口：接住这次右键松开，桌面就收不到、不会弹出桌面右键菜单，改由本程序弹出图标的菜单。
/// 不用钩子吞掉松开：吞掉后系统记录的右键状态停在“按下”（实测 GetAsyncKeyState 一直报告按着），随后在分区里框选、点桌面图标都会乱。
/// 窗口看不见（每个像素的不透明度为 1/255，分层窗口完全透明的像素接不住鼠标），借系统 Static 类、带 SS_NOTIFY 才会接收鼠标，
/// 收到右键松开后 Static 的默认处理什么也不做；不抢焦点。
/// </summary>
internal sealed class ClickCatcherWindow : IDisposable
{
	const int SS_NOTIFY = 0x0100;

	/// <summary>
	/// 边长（DIP）：比一次界面刷新里鼠标能移动的距离大得多，松开时鼠标总在它上面。
	/// </summary>
	const double SizeDip = 160;

	readonly IntPtr _hwnd;
	IntPtr _dc;
	IntPtr _bitmap;
	int _size;
	bool _shown;

	public ClickCatcherWindow()
	{
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST), "Static", null,
				unchecked((uint)WS_POPUP) | SS_NOTIFY, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建接住点击的窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		_dc = CreateCompatibleDC(IntPtr.Zero);
	}

	public bool IsShown => _shown;

	/// <summary>
	/// 以鼠标位置（屏幕物理像素）为中心显示；边长按所在显示器的缩放比例换算，比例变了才重画位图，其余只挪位置。
	/// </summary>
	public void ShowAt(POINT center)
	{
		int size = (int)Math.Round(SizeDip * GetDpiForRect(new RECT(center.X, center.Y, center.X + 1, center.Y + 1)) / 96.0);
		var destination = new POINT(center.X - size / 2, center.Y - size / 2);
		if (size != _size)
		{
			Redraw(size, destination);
		}
		else
		{
			SetWindowPos(_hwnd, IntPtr.Zero, destination.X, destination.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
		}
		if (!_shown)
		{
			_shown = true;
			SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
			ShowWindowAsync(_hwnd, SW_SHOWNA);
		}
	}

	public void Hide()
	{
		if (_shown)
		{
			_shown = false;
			ShowWindowAsync(_hwnd, SW_HIDE);
		}
	}

	public void Dispose()
	{
		DestroyWindow(_hwnd);
		if (_bitmap != IntPtr.Zero)
		{
			DeleteObject(_bitmap);
			_bitmap = IntPtr.Zero;
		}
		if (_dc != IntPtr.Zero)
		{
			DeleteDC(_dc);
			_dc = IntPtr.Zero;
		}
	}

	/// <summary>
	/// 画一张每个像素都是 1/255 黑色（预乘 alpha）的位图交给 UpdateLayeredWindow。
	/// </summary>
	void Redraw(int size, POINT destination)
	{
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = size,
				biHeight = -size,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
		if (bitmap == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建接住点击的位图失败（{size}×{size}）");
		}
		var pixels = new int[size * size];
		Array.Fill(pixels, 1 << 24);
		Marshal.Copy(pixels, 0, bits, pixels.Length);
		SelectObject(_dc, bitmap);
		if (_bitmap != IntPtr.Zero)
		{
			DeleteObject(_bitmap);
		}
		_bitmap = bitmap;
		_size = size;
		var sizeStruct = new SIZE(size, size);
		var source = new POINT(0, 0);
		var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
		if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref sizeStruct, _dc, ref source, 0, ref blend, ULW_ALPHA))
		{
			Log.Warn($"画接住点击的窗口失败（UpdateLayeredWindow 错误码 {Marshal.GetLastWin32Error()}）");
		}
	}
}
