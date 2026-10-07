using System.Runtime.InteropServices;
using System.Windows.Media;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 画在屏幕上的选框：框选图标、按住右键画框新建分区、拆出标签时的落点预览。置顶、鼠标穿透、不抢焦点。
/// 像素由 CPU 直接画好交给 UpdateLayeredWindow，不经过 WPF：WPF 的透明窗口每次重画都要等显卡把画面拷回内存，
/// 显卡被游戏占满时拖着选框就会一卡一卡；这里每次也只画选框这么大的一块，不用重画整个图标层或分区。
/// </summary>
internal sealed class DrawFrameWindow : IDisposable
{
	static readonly Color Fill = Color.FromArgb(0x33, 0x3B, 0x82, 0xF6);

	readonly IntPtr _hwnd;
	readonly Color _border;
	readonly double _borderDip;
	readonly double _radiusDip;
	// 画像素的位图：比选框大时直接复用，只用左上角那一块
	IntPtr _dc;
	IntPtr _bitmap;
	IntPtr _bits;
	int _bitmapWidth;
	int _bitmapHeight;
	bool _shown;
	// 画失败只记一次日志，免得拖动时每动一下都记
	static bool _failureLogged;

	DrawFrameWindow(Color border, double borderDip, double radiusDip)
	{
		_border = border;
		_borderDip = borderDip;
		_radiusDip = radiusDip;
		// 不用 WPF 的 HwndSource：它不用逐像素透明时会强行去掉 WS_EX_LAYERED，UpdateLayeredWindow 就用不了。
		// 借用系统自带的 Static 窗口类建一个原生窗口，内容全由 UpdateLayeredWindow 提供，不需要自己的窗口过程
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST), "Static", null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建选框窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		_dc = CreateCompatibleDC(IntPtr.Zero);
	}

	/// <summary>
	/// 画框新建分区、拆出标签的落点预览：圆角、较粗的边框，和分区的样子接近。
	/// </summary>
	public static DrawFrameWindow CreateFrame() => new(Color.FromArgb(0xE6, 0x60, 0xA5, 0xFA), 2, 6);

	/// <summary>
	/// 框选图标的选框：直角、细边框，和系统桌面一样。
	/// </summary>
	public static DrawFrameWindow CreateMarquee() => new(Color.FromArgb(0xCC, 0x60, 0xA5, 0xFA), 1, 0);

	/// <summary>
	/// 在屏幕物理像素矩形处显示选框；边框粗细和圆角按所在显示器的缩放比例换算。
	/// </summary>
	public void ShowAt(RECT rect)
	{
		int width = rect.Width;
		int height = rect.Height;
		if (width <= 0 || height <= 0)
		{
			Hide();
			return;
		}
		double scale = GetDpiForRect(rect) / 96.0;
		int border = Math.Max(1, (int)Math.Round(_borderDip * scale));
		int radius = Math.Min((int)Math.Round(_radiusDip * scale), Math.Min(width, height) / 2);
		EnsureBitmap(width, height);
		Draw(width, height, border, radius);
		var destination = new POINT(rect.Left, rect.Top);
		var size = new SIZE(width, height);
		var source = new POINT(0, 0);
		var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
		if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref size, _dc, ref source, 0, ref blend, ULW_ALPHA) && !_failureLogged)
		{
			_failureLogged = true;
			Log.Warn($"画选框失败（UpdateLayeredWindow 错误码 {Marshal.GetLastWin32Error()}）");
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
	/// 位图不够大时换一张更大的（多留一些，拖动选框时不用每次都重新分配）。
	/// </summary>
	void EnsureBitmap(int width, int height)
	{
		if (width <= _bitmapWidth && height <= _bitmapHeight)
		{
			return;
		}
		int newWidth = Math.Max(width, _bitmapWidth) + 256;
		int newHeight = Math.Max(height, _bitmapHeight) + 256;
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = newWidth,
				// 高度为负：自上而下，第 y 行在 bits + y × 行宽
				biHeight = -newHeight,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
		if (bitmap == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建选框位图失败（{newWidth}×{newHeight}）");
		}
		SelectObject(_dc, bitmap);
		if (_bitmap != IntPtr.Zero)
		{
			DeleteObject(_bitmap);
		}
		(_bitmap, _bits, _bitmapWidth, _bitmapHeight) = (bitmap, bits, newWidth, newHeight);
	}

	/// <summary>
	/// 逐行画出选框（预乘 alpha 的 BGRA）：中间那些行都一样，算一次后照抄；圆角所在的行按像素到圆弧的距离做抗锯齿。
	/// </summary>
	void Draw(int width, int height, int border, int radius)
	{
		int stride = _bitmapWidth * 4;
		var row = new int[width];
		var middle = new int[width];
		Array.Fill(middle, Premultiply(Fill, 1));
		Array.Fill(middle, Premultiply(_border, 1), 0, Math.Min(border, width));
		Array.Fill(middle, Premultiply(_border, 1), Math.Max(0, width - border), Math.Min(border, width));
		for (int y = 0; y < height; y++)
		{
			bool corner = y < radius || y >= height - radius;
			bool edge = y < border || y >= height - border;
			if (!corner && !edge)
			{
				Marshal.Copy(middle, 0, _bits + y * stride, width);
				continue;
			}
			for (int x = 0; x < width; x++)
			{
				row[x] = Pixel(x, y, width, height, border, radius);
			}
			Marshal.Copy(row, 0, _bits + y * stride, width);
		}
	}

	/// <summary>
	/// 一个像素的颜色：圆角范围内按像素中心到圆心的距离算出落在外轮廓、内轮廓里的比例，内部是填充色，两条轮廓之间是边框色。
	/// </summary>
	int Pixel(int x, int y, int width, int height, int border, int radius)
	{
		double inside;
		double outside;
		bool inCornerX = x < radius || x >= width - radius;
		bool inCornerY = y < radius || y >= height - radius;
		if (radius > 0 && inCornerX && inCornerY)
		{
			double cx = x < radius ? radius : width - radius;
			double cy = y < radius ? radius : height - radius;
			double distance = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
			outside = Math.Clamp(radius - distance + 0.5, 0, 1);
			inside = Math.Clamp(radius - border - distance + 0.5, 0, 1);
		}
		else
		{
			outside = 1;
			inside = x < border || x >= width - border || y < border || y >= height - border ? 0 : 1;
		}
		return Blend(Premultiply(Fill, inside), Premultiply(_border, outside - inside));
	}

	/// <summary>
	/// 颜色乘上覆盖比例后转成预乘 alpha 的 BGRA。
	/// </summary>
	static int Premultiply(Color color, double coverage)
	{
		double alpha = color.A / 255.0 * coverage;
		int a = (int)Math.Round(alpha * 255);
		int r = (int)Math.Round(color.R * alpha);
		int g = (int)Math.Round(color.G * alpha);
		int b = (int)Math.Round(color.B * alpha);
		return (a << 24) | (r << 16) | (g << 8) | b;
	}

	/// <summary>
	/// 两个不重叠的预乘颜色相加（填充和边框各占像素的一部分）。
	/// </summary>
	static int Blend(int first, int second)
	{
		int Channel(int shift) => Math.Min(255, ((first >> shift) & 0xFF) + ((second >> shift) & 0xFF));
		return (Channel(24) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
	}
}
