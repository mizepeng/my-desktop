using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 用 GDI ClearType 把名称画成预乘透明度的位图（桌面图标层的 TextShadow 使用）。
/// WPF 文字横向不做 hinting，透明窗口里又只有灰度抗锯齿，竖笔画常落在半个像素上，成了 2 像素宽的灰柱；
/// GDI 横竖都按字体的 hinting 对齐整像素，和资源管理器桌面的文字一样清楚（2026-11 实测对比，150% 缩放：浅色、深色壁纸上和原生都几乎逐像素一致）。
/// 透明窗口只有一个透明度，ClearType 三个子像素的覆盖度各不相同，透明度取最大的那个：浅色底上彩边会略偏暗，好在笔画周围有阴影垫着，实测看不出。
/// 试过把字直接画在下面的壁纸上再反算透明度，和原生的差别并没有更小，还要多读一张原尺寸壁纸（4K 约 31 MB），没有采用。
/// 阴影照资源管理器桌面的样子在位图上合成（ExplorerShadow），不再用 WPF 画法的描边。
/// </summary>
internal static class GdiText
{
	/// <summary>
	/// 要画的名称。宽度、行数、行高都是物理像素；Shadow 为 null 时不画阴影。
	/// </summary>
	internal sealed record Request(string Text, string Face, int FontPixels, int Weight, bool Italic, uint Align, bool Wrap, bool Ellipsis,
			int Width, int Lines, Color Foreground, ShadowSpec? Shadow, double Dpi);

	/// <summary>
	/// 阴影：文字覆盖度高斯模糊（Sigma 为标准差，物理像素）后乘 Strength（超过 1 的部分截掉），向右下各挪 Offset 个像素。
	/// </summary>
	internal sealed record ShadowSpec(double Sigma, double Strength, int Offset);

	/// <summary>
	/// 画好的位图；文字区域的左上角在位图里的 (Padding, Padding)。
	/// </summary>
	internal sealed record Result(BitmapSource Bitmap, int Padding);

	/// <summary>
	/// 和资源管理器桌面一样的阴影。2026-11 在 150% 缩放、浅色壁纸上，用纯壁纸和原生图标两张截图反推出原生阴影的透明度，
	/// 再拟合：标准差约 1.5 像素、向右下各 1 像素、强度约 1.4，没有描边，四个名称的结果一致；拟合误差比原来的描边加模糊阴影小一半。
	/// 只在 150% 下量过，其他缩放比例按 DIP 等比换算（标准差 1 DIP，偏移 2/3 DIP、至少 1 像素）。
	/// </summary>
	public static ShadowSpec ExplorerShadow(double scale) => new(scale, 1.4, Math.Max(1, (int)Math.Round(scale * 2 / 3)));

	/// <summary>
	/// 阴影向四周扩出的像素数。
	/// </summary>
	static int PaddingFor(ShadowSpec? shadow) => shadow == null ? 0 : (int)Math.Ceiling(shadow.Sigma * 3) + shadow.Offset;

	/// <summary>
	/// 画名称：白字画在黑底上取各子像素的覆盖度，再和阴影一起合成预乘透明度的位图。
	/// </summary>
	public static Result Render(Request request)
	{
		int pad = PaddingFor(request.Shadow);
		var font = CreateFont(-request.FontPixels, 0, 0, 0, request.Weight, request.Italic ? 1u : 0u, 0, 0, DEFAULT_CHARSET, 0, 0,
				CLEARTYPE_QUALITY, 0, request.Face);
		var dc = CreateCompatibleDC(IntPtr.Zero);
		var oldFont = SelectObject(dc, font);
		try
		{
			int lineHeight = LineHeight(dc);
			int width = request.Width + pad * 2;
			int height = request.Lines * lineHeight + pad * 2;
			uint format = request.Align | DT_NOPREFIX | (request.Wrap ? DT_WORDBREAK | DT_EDITCONTROL : DT_SINGLELINE) | (request.Ellipsis ? DT_END_ELLIPSIS : 0);
			var textRect = new RECT(pad, pad, pad + request.Width, pad + request.Lines * lineHeight);
			// 白字画在黑底上，各通道就是覆盖度（ClearType 时三个子像素各不相同）
			var coverage = Draw(dc, width, height, 0xFFFFFF, request.Text, textRect, format);
			var shadow = ShadowAlpha(coverage, width, height, request.Shadow);
			var pixels = Compose(coverage, shadow, request.Foreground);
			var bitmap = BitmapSource.Create(width, height, request.Dpi, request.Dpi, PixelFormats.Pbgra32, null, pixels, width * 4);
			bitmap.Freeze();
			return new Result(bitmap, pad);
		}
		finally
		{
			SelectObject(dc, oldFont);
			DeleteObject(font);
			DeleteDC(dc);
		}
	}

	static int LineHeight(IntPtr dc)
	{
		var rect = new RECT(0, 0, 1000, 1000);
		DrawText(dc, "Ag", 2, ref rect, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
		return Math.Max(1, rect.Bottom - rect.Top);
	}

	/// <summary>
	/// 在黑底的 32 位位图上画字，返回画好后的像素（BGRA，透明度字节无意义）。
	/// </summary>
	static int[] Draw(IntPtr dc, int width, int height, uint color, string text, RECT rect, uint format)
	{
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				// 高度为负：自上而下的行序
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
		if (bitmap == IntPtr.Zero)
		{
			throw new InvalidOperationException($"CreateDIBSection 失败（{width}×{height}）");
		}
		var old = SelectObject(dc, bitmap);
		try
		{
			SetBkMode(dc, TRANSPARENT);
			SetTextColor(dc, color);
			DrawText(dc, text, text.Length, ref rect, format);
			var pixels = new int[width * height];
			Marshal.Copy(bits, pixels, 0, pixels.Length);
			return pixels;
		}
		finally
		{
			SelectObject(dc, old);
			DeleteObject(bitmap);
		}
	}

	static double Max(int bgra) => Math.Max((bgra >> 16) & 255, Math.Max((bgra >> 8) & 255, bgra & 255)) / 255.0;

	/// <summary>
	/// 阴影层（黑色）的不透明度：覆盖度高斯模糊后向右下挪、乘强度。
	/// </summary>
	static double[] ShadowAlpha(int[] coverage, int width, int height, ShadowSpec? spec)
	{
		var alpha = new double[width * height];
		if (spec == null)
		{
			return alpha;
		}
		var cover = new double[width * height];
		for (int i = 0; i < cover.Length; i++)
		{
			cover[i] = Max(coverage[i]);
		}
		double sigma = Math.Max(0.5, spec.Sigma);
		int reach = (int)Math.Ceiling(sigma * 3);
		var kernel = new double[reach * 2 + 1];
		double sum = 0;
		for (int i = -reach; i <= reach; i++)
		{
			kernel[i + reach] = Math.Exp(-i * i / (2 * sigma * sigma));
			sum += kernel[i + reach];
		}
		var rows = new double[width * height];
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				double v = 0;
				for (int k = -reach; k <= reach; k++)
				{
					// 阴影右移：取左边 Offset 处的值
					int xx = x - spec.Offset + k;
					if (xx >= 0 && xx < width)
					{
						v += cover[y * width + xx] * kernel[k + reach];
					}
				}
				rows[y * width + x] = v / sum;
			}
		}
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				double v = 0;
				for (int k = -reach; k <= reach; k++)
				{
					// 阴影下移：取上方 Offset 处的模糊值
					int yy = y - spec.Offset + k;
					if (yy >= 0 && yy < height)
					{
						v += rows[yy * width + x] * kernel[k + reach];
					}
				}
				alpha[y * width + x] = Math.Min(1, v / sum * spec.Strength);
			}
		}
		return alpha;
	}

	/// <summary>
	/// 按黑底的覆盖度合成：文字（预乘）叠在黑色阴影层上，透明度取三个通道里最大的覆盖度。
	/// </summary>
	static byte[] Compose(int[] coverage, double[] shadow, Color foreground)
	{
		var pixels = new byte[coverage.Length * 4];
		for (int i = 0; i < coverage.Length; i++)
		{
			int c = coverage[i];
			double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
			double text = Math.Max(r, Math.Max(g, b));
			double alpha = text + shadow[i] * (1 - text);
			pixels[i * 4] = ToByte(b * foreground.B / 255.0);
			pixels[i * 4 + 1] = ToByte(g * foreground.G / 255.0);
			pixels[i * 4 + 2] = ToByte(r * foreground.R / 255.0);
			pixels[i * 4 + 3] = ToByte(alpha);
		}
		return pixels;
	}

	static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
