using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 分区挪动窗口左上角的那一下，顶替它显示的截图（见 FenceWindow.MoveBehindStandIn）：鼠标穿透、不抢焦点、不置顶，
/// 层级紧贴在分区之上；像素用 UpdateLayeredWindow 一次给全，不用等 WPF 重画。
/// </summary>
internal sealed class StandInWindow : IDisposable
{
	readonly IntPtr _hwnd;

	public StandInWindow()
	{
		// 和 DrawFrameWindow 一样借用系统的 Static 窗口类：WPF 的窗口不用逐像素透明时会被去掉 WS_EX_LAYERED
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), "Static", null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建截图窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
	}

	/// <summary>
	/// 在 rect（屏幕物理像素）处显示 image（和 rect 一样大、预乘透明度的 32 位像素），层级放在 owner 正上方。
	/// </summary>
	public void Show(BitmapSource image, RECT rect, IntPtr owner)
	{
		int width = rect.Width;
		int height = rect.Height;
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				// 高度为负：自上而下，和 WPF 位图的行序一致
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var dc = CreateCompatibleDC(IntPtr.Zero);
		var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
		try
		{
			if (bitmap == IntPtr.Zero)
			{
				throw new InvalidOperationException($"创建截图位图失败（{width}×{height}）");
			}
			image.CopyPixels(new Int32Rect(0, 0, width, height), bits, width * height * 4, width * 4);
			var previous = SelectObject(dc, bitmap);
			var destination = new POINT(rect.Left, rect.Top);
			var size = new SIZE(width, height);
			var source = new POINT(0, 0);
			var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
			bool shown = UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref size, dc, ref source, 0, ref blend, ULW_ALPHA);
			SelectObject(dc, previous);
			if (!shown)
			{
				throw new InvalidOperationException($"显示截图失败（UpdateLayeredWindow 错误码 {Marshal.GetLastWin32Error()}）");
			}
		}
		finally
		{
			if (bitmap != IntPtr.Zero)
			{
				DeleteObject(bitmap);
			}
			DeleteDC(dc);
		}
		// 插到原本在 owner 上面的那个窗口后面，就是 owner 正上方；上面是置顶窗口时放到普通窗口的最上面
		var above = GetWindow(owner, GW_HWNDPREV);
		uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
		if (above == _hwnd)
		{
			flags |= SWP_NOZORDER;
		}
		else if (above != IntPtr.Zero && (GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0)
		{
			above = HWND_TOP;
		}
		SetWindowPos(_hwnd, above, 0, 0, 0, 0, flags);
	}

	public void Hide() => SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);

	public void Dispose() => DestroyWindow(_hwnd);
}
