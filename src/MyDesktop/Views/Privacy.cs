using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MyDesktop.Models;

namespace MyDesktop.Views;

/// <summary>
/// 隐私模式此刻要打码哪些内容（已经算上了总开关和「桌面在前台时露出」）；RevealOnHover 表示鼠标指着的那一块露出，Level 是模糊程度（见 Privacy.MinLevel）。
/// </summary>
internal readonly record struct PrivacyState(bool Icons, bool Names, bool Titles, bool RevealOnHover, int Level)
{
	public bool Any => Icons || Names || Titles;
}

/// <summary>
/// 隐私模式的打码：图标图片、名称、分区标题各用一个往下继承的附加属性标记，设在窗口上，鼠标指着的格子、分区再把它改回 false 露出。
/// 不用 WPF 的 BlurEffect：效果要先画进中间位图再合成，每次重画都要过一遍，硬件加速时还让显卡驱动内存上涨（见 docs/dev/constraints.md）。
/// 改成在 CPU 上把文字画成很小的位图再模糊、图标画成主题色的色团，显示时平滑放大：
/// 图标每个 4 KB（按图像缓存，不再给图标打码时清掉），文字由 TextShadow 各自留一张（不再给这类文字打码时释放）；平时重画不多任何开销。
/// </summary>
internal static class Privacy
{
	/// <summary>
	/// 打码图标图片（PrivacyImage 响应）。
	/// </summary>
	public static readonly DependencyProperty IconsHiddenProperty = DependencyProperty.RegisterAttached("IconsHidden", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnHiddenChanged));

	/// <summary>
	/// 打码图标名称（TextShadow 响应，IsTitle 为 false 的）。
	/// </summary>
	public static readonly DependencyProperty NamesHiddenProperty = DependencyProperty.RegisterAttached("NamesHidden", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnHiddenChanged));

	/// <summary>
	/// 打码分区标题（TextShadow 响应 IsTitle 为 true 的，映射分区子文件夹的路径整块模糊）。
	/// </summary>
	public static readonly DependencyProperty TitlesHiddenProperty = DependencyProperty.RegisterAttached("TitlesHidden", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnHiddenChanged));

	/// <summary>
	/// 鼠标指着的格子露出（图标层、搜索框的项目样式里按它和 IsMouseOver 改回上面三项）。
	/// </summary>
	public static readonly DependencyProperty RevealOnHoverProperty = DependencyProperty.RegisterAttached("RevealOnHover", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

	/// <summary>
	/// 这个元素的提示里是图标名称：名称打码时不显示提示。
	/// </summary>
	public static readonly DependencyProperty NameToolTipProperty = DependencyProperty.RegisterAttached("NameToolTip", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, (d, _) => UpdateToolTip(d)));

	/// <summary>
	/// 这个元素的提示里是分区标题：标题打码时不显示提示。
	/// </summary>
	public static readonly DependencyProperty TitleToolTipProperty = DependencyProperty.RegisterAttached("TitleToolTip", typeof(bool), typeof(Privacy),
			new FrameworkPropertyMetadata(false, (d, _) => UpdateToolTip(d)));

	/// <summary>
	/// 模糊程度（MinLevel～MaxLevel），往下继承；变了 PrivacyImage、TextShadow 按新的程度重画。
	/// </summary>
	public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached("Level", typeof(int), typeof(Privacy),
			new FrameworkPropertyMetadata(AppSettings.DefaultPrivacyBlurLevel, FrameworkPropertyMetadataOptions.Inherits, OnLevelChanged));

	public const int MinLevel = 1;
	public const int MaxLevel = 10;

	// 模糊程度只管文字：每 DIP 画几像素，越小越模糊；再做四遍半径 1 的方框模糊（约等于标准差 1.6 像素的高斯模糊）。
	// 用真实文件名对比过几档（2026-11）：0.35 时英文单词的轮廓还认得出来，默认的第 8 档是 0.25
	static readonly double[] TextPixelsPerDipByLevel = [0.8, 0.65, 0.55, 0.45, 0.38, 0.33, 0.29, 0.25, 0.21, 0.17];
	const int BlurPasses = 4;
	// 模糊图只画在文字自己的范围里，不往外扩：画到范围外的部分在桌面图标层上会被截断（图标层窗口只比图标范围大一点，
	// 名称那一条重画时还会抹掉上面图标伸下来的部分，Max 实机截图），四周只留一点让模糊散开，边上靠渐隐（FeatherRatio）收成全透明
	const int Padding = 1;
	// 文字模糊后最浓处拉到的不透明度（不够浓时才拉，不会调淡）：细笔画模糊后很淡，短标题只剩一小团灰影
	const double TextPeak = 0.6;
	// 模糊后按到中心的椭圆距离渐隐：最外一圈像素上全透明（只按位图边缘算的话四条边正中还剩一点颜色，放大后被图像边界切断成亮边），
	// 往里这个比例的范围内渐渐变浓，名称拉浓后才不会像一条硬边的矩形（Max 反馈「边缘没有过渡」）
	const double FeatherRatio = 0.5;
	// 图标不模糊原图，只取主题色画成统一的圆形色团：形状、轮廓都看不出来（Max 2026-11 提议）。
	// 之前模糊原图时满铺的方形应用图标还是一块圆角方块、要靠渐隐削圆，削多了看着缩小、削少了成硬边圆点，换成统一的色团就没有这些问题。
	// 色团按 BlobPixels 见方画好再平滑放大；最浓处的不透明度和之前选定的效果一致（Max 选的 D：渐隐 0.6、最浓 0.85）
	const int BlobPixels = 32;
	const double BlobFeather = 0.6;
	const double BlobPeak = 0.85;
	// 取主题色时把图标画成这么大再统计；色相分几组，饱和度低于多少算无彩色
	const int ThemeSamplePixels = 16;
	const int HueBins = 12;
	const double GraySaturation = 0.15;

	static readonly ConditionalWeakTable<ImageSource, BitmapSource> BlurredIcons = new();
	static BlurEffect? _pathBlur;

	/// <summary>
	/// 当前状态；新开的窗口按它设置，变了发出 Changed。
	/// </summary>
	public static PrivacyState Current { get; private set; }

	public static event Action? Changed;

	public static void SetIconsHidden(DependencyObject element, bool value) => element.SetValue(IconsHiddenProperty, value);

	public static bool GetIconsHidden(DependencyObject element) => (bool)element.GetValue(IconsHiddenProperty);

	public static void SetNamesHidden(DependencyObject element, bool value) => element.SetValue(NamesHiddenProperty, value);

	public static bool GetNamesHidden(DependencyObject element) => (bool)element.GetValue(NamesHiddenProperty);

	public static void SetTitlesHidden(DependencyObject element, bool value) => element.SetValue(TitlesHiddenProperty, value);

	public static bool GetTitlesHidden(DependencyObject element) => (bool)element.GetValue(TitlesHiddenProperty);

	public static void SetRevealOnHover(DependencyObject element, bool value) => element.SetValue(RevealOnHoverProperty, value);

	public static bool GetRevealOnHover(DependencyObject element) => (bool)element.GetValue(RevealOnHoverProperty);

	public static void SetNameToolTip(DependencyObject element, bool value) => element.SetValue(NameToolTipProperty, value);

	public static bool GetNameToolTip(DependencyObject element) => (bool)element.GetValue(NameToolTipProperty);

	public static void SetTitleToolTip(DependencyObject element, bool value) => element.SetValue(TitleToolTipProperty, value);

	public static bool GetTitleToolTip(DependencyObject element) => (bool)element.GetValue(TitleToolTipProperty);

	public static void SetLevel(DependencyObject element, int value) => element.SetValue(LevelProperty, value);

	public static int GetLevel(DependencyObject element) => (int)element.GetValue(LevelProperty);

	/// <summary>
	/// 换成新的状态，变了才通知各窗口；全部不打码时清掉图标的模糊缓存。
	/// </summary>
	public static void Update(PrivacyState state)
	{
		if (state == Current)
		{
			return;
		}
		Current = state;
		if (!state.Icons)
		{
			BlurredIcons.Clear();
		}
		Changed?.Invoke();
	}

	/// <summary>
	/// 把状态设到窗口（或别的根元素）上；revealed 为 true 时整块露出（鼠标在分区上）。
	/// </summary>
	public static void Apply(DependencyObject root, PrivacyState state, bool revealed)
	{
		SetIconsHidden(root, state.Icons && !revealed);
		SetNamesHidden(root, state.Names && !revealed);
		SetTitlesHidden(root, state.Titles && !revealed);
		SetRevealOnHover(root, state.RevealOnHover);
		SetLevel(root, state.Level);
	}

	static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is PrivacyImage or TextShadow)
		{
			((UIElement)d).InvalidateVisual();
		}
	}

	static int Clamp(int level) => Math.Clamp(level, MinLevel, MaxLevel) - MinLevel;

	static void OnHiddenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		switch (d)
		{
			case PrivacyImage image:
				image.InvalidateVisual();
				break;
			case TextShadow shadow:
				shadow.UpdatePrivacy();
				break;
			case BreadcrumbPanel path when e.Property == TitlesHiddenProperty:
				// 路径是一排按钮，只在映射分区进入子文件夹后出现，整块加一个小的模糊效果
				path.Effect = (bool)e.NewValue ? _pathBlur ??= CreatePathBlur() : null;
				break;
		}
		UpdateToolTip(d);
	}

	static BlurEffect CreatePathBlur()
	{
		var effect = new BlurEffect { Radius = 12 };
		effect.Freeze();
		return effect;
	}

	static void UpdateToolTip(DependencyObject d)
	{
		bool name = GetNameToolTip(d);
		bool title = GetTitleToolTip(d);
		if (name || title)
		{
			ToolTipService.SetIsEnabled(d, !(name && GetNamesHidden(d) || title && GetTitlesHidden(d)));
		}
	}

	/// <summary>
	/// 图标打码后的样子：图标主题色的圆形色团，画在图标原来的范围里。按图像缓存。
	/// </summary>
	public static BitmapSource BlurIcon(ImageSource source)
	{
		if (BlurredIcons.TryGetValue(source, out var cached))
		{
			return cached;
		}
		var blob = CreateBlob(ThemeColor(source));
		BlurredIcons.AddOrUpdate(source, blob);
		return blob;
	}

	/// <summary>
	/// 图标的主题色：按色相分组，取鲜艳部分分量最大的那组颜色的平均。
	/// </summary>
	static Color ThemeColor(ImageSource source)
	{
		int size = ThemeSamplePixels;
		var visual = new DrawingVisual();
		using (var dc = visual.RenderOpen())
		{
			var group = new DrawingGroup();
			RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
			group.Children.Add(new ImageDrawing(source, new Rect(0, 0, size, size)));
			dc.DrawDrawing(group);
		}
		var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
		target.Render(visual);
		var pixels = new byte[size * size * 4];
		target.CopyPixels(pixels, size * 4, 0);
		// 按色相分成 HueBins 组，再加一组近乎无彩色的（白、灰、黑）；取分量最大的一组求平均。
		// 整张图一起平均的话，黄色加蓝色的文件夹会混成土绿色
		var bins = new double[HueBins + 1, 4];
		for (int i = 0; i < pixels.Length; i += 4)
		{
			int alpha = pixels[i + 3];
			if (alpha == 0)
			{
				continue;
			}
			// 预乘过透明度，先还原成原来的颜色
			double b = pixels[i] * 255.0 / alpha;
			double g = pixels[i + 1] * 255.0 / alpha;
			double r = pixels[i + 2] * 255.0 / alpha;
			double max = Math.Max(r, Math.Max(g, b));
			double min = Math.Min(r, Math.Min(g, b));
			double saturation = max > 0 ? (max - min) / max : 0;
			int bin = saturation < GraySaturation ? HueBins : (int)(Hue(r, g, b, max, min) / 360 * HueBins) % HueBins;
			// 鲜艳的部分说了算，白底、灰边、阴影只占一点分量
			double weight = alpha / 255.0 * (0.1 + saturation);
			bins[bin, 0] += r * weight;
			bins[bin, 1] += g * weight;
			bins[bin, 2] += b * weight;
			bins[bin, 3] += weight;
		}
		int best = 0;
		for (int bin = 1; bin <= HueBins; bin++)
		{
			if (bins[bin, 3] > bins[best, 3])
			{
				best = bin;
			}
		}
		double total = bins[best, 3];
		if (total == 0)
		{
			// 整张图全透明，没有颜色可取，画个灰色团占住位置
			return Colors.Gray;
		}
		return Color.FromRgb((byte)Math.Round(bins[best, 0] / total), (byte)Math.Round(bins[best, 1] / total), (byte)Math.Round(bins[best, 2] / total));
	}

	/// <summary>
	/// 色相（0～360 度）。
	/// </summary>
	static double Hue(double r, double g, double b, double max, double min)
	{
		double delta = max - min;
		double hue = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
		return (hue * 60 + 360) % 360;
	}

	/// <summary>
	/// 圆形色团：中心最浓（BlobPeak），按到中心的距离渐隐，最外一圈像素上全透明。
	/// </summary>
	static BitmapSource CreateBlob(Color color)
	{
		int size = BlobPixels;
		int stride = size * 4;
		var pixels = new byte[stride * size];
		double radius = (size - 1) / 2.0;
		for (int y = 0; y < size; y++)
		{
			double dy = (y - radius) / radius;
			for (int x = 0; x < size; x++)
			{
				double dx = (x - radius) / radius;
				double alpha = BlobPeak * Fade((1 - Math.Sqrt(dx * dx + dy * dy)) / BlobFeather);
				int at = y * stride + x * 4;
				pixels[at] = (byte)Math.Round(color.B * alpha);
				pixels[at + 1] = (byte)Math.Round(color.G * alpha);
				pixels[at + 2] = (byte)Math.Round(color.R * alpha);
				pixels[at + 3] = (byte)Math.Round(255 * alpha);
			}
		}
		var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
		result.Freeze();
		return result;
	}

	/// <summary>
	/// 文字的模糊版本，按 TextBlock 的字体、换行、截断排好，画在正文原来的范围里（四周留的一点空白也在范围内）。
	/// </summary>
	public static BitmapSource BlurText(TextBlock text, int level)
	{
		double pixelsPerDip = TextPixelsPerDipByLevel[Clamp(level)];
		double padding = Padding / pixelsPerDip;
		double width = Math.Max(1, text.ActualWidth);
		double height = Math.Max(1, text.ActualHeight);
		var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
		var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection, typeface, text.FontSize,
				text.Foreground ?? Brushes.White, 1)
		{
			MaxTextWidth = width,
			MaxTextHeight = height,
			TextAlignment = text.TextAlignment,
			Trimming = text.TextTrimming,
		};
		if (!double.IsNaN(text.LineHeight) && text.LineHeight > 0)
		{
			formatted.LineHeight = text.LineHeight;
		}
		if (text.TextWrapping == TextWrapping.NoWrap)
		{
			formatted.MaxLineCount = 1;
		}
		var visual = new DrawingVisual();
		using (var dc = visual.RenderOpen())
		{
			dc.DrawText(formatted, new Point(padding, padding));
		}
		int pixelWidth = (int)Math.Ceiling(width * pixelsPerDip) + 2 * Padding;
		int pixelHeight = (int)Math.Ceiling(height * pixelsPerDip) + 2 * Padding;
		return Blur(visual, pixelWidth, pixelHeight, 96 * pixelsPerDip, TextPeak);
	}

	/// <summary>
	/// 把画好的内容按指定像素大小画成位图，再做四遍方框模糊（预乘透明度，边缘不会发黑）。
	/// 模糊后按椭圆距离渐隐（FeatherRatio），再把最浓处拉到 targetPeak（TextPeak、IconPeak）：文字拉浓后像一条柔和的占位条，长短仍跟着文字；
	/// 图标拉回原来的浓淡。
	/// </summary>
	static BitmapSource Blur(Visual visual, int width, int height, double dpi, double targetPeak)
	{
		var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
		target.Render(visual);
		int stride = width * 4;
		var pixels = new byte[stride * height];
		target.CopyPixels(pixels, stride, 0);
		var buffer = new int[pixels.Length];
		for (int i = 0; i < pixels.Length; i++)
		{
			buffer[i] = pixels[i];
		}
		var temp = new int[buffer.Length];
		for (int pass = 0; pass < BlurPasses; pass++)
		{
			BoxBlur(buffer, temp, width, height, 4, stride);
			BoxBlur(temp, buffer, height, width, stride, 4);
		}
		var feathered = new double[buffer.Length];
		double peak = 0;
		// 椭圆按最外一圈像素的中心算：只按位图边缘算的话，四条边正中的像素还剩一点颜色，放大后在图像边界被齐刷刷切断，
		// 上下左右各成一道亮边（Max 实机反馈）
		double radiusX = Math.Max(1, (width - 1) / 2.0);
		double radiusY = Math.Max(1, (height - 1) / 2.0);
		for (int y = 0; y < height; y++)
		{
			double dy = (y - (height - 1) / 2.0) / radiusY;
			for (int x = 0; x < width; x++)
			{
				// 到中心的椭圆距离：中心为 0，最外一圈像素中心所在的椭圆上为 1，椭圆外全透明
				double dx = (x - (width - 1) / 2.0) / radiusX;
				double fade = Fade((1 - Math.Sqrt(dx * dx + dy * dy)) / FeatherRatio);
				int at = y * stride + x * 4;
				for (int channel = 0; channel < 4; channel++)
				{
					feathered[at + channel] = buffer[at + channel] * fade;
				}
				peak = Math.Max(peak, feathered[at + 3]);
			}
		}
		double gain = peak > 0 ? Math.Max(1, targetPeak * 255 / peak) : 1;
		for (int i = 0; i < pixels.Length; i++)
		{
			pixels[i] = (byte)Math.Clamp((int)Math.Round(feathered[i] * gain), 0, 255);
		}
		var result = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Pbgra32, null, pixels, stride);
		result.Freeze();
		return result;
	}

	/// <summary>
	/// 渐隐曲线（smoothstep）：t 为 0 时全透明，到 1 时不变。
	/// </summary>
	static double Fade(double t)
	{
		t = Math.Clamp(t, 0, 1);
		return t * t * (3 - 2 * t);
	}

	/// <summary>
	/// 半径 1 的方框模糊：沿一个方向（step 是相邻像素的间隔，lineStep 是相邻两行的间隔）每个通道取自己和两边共三个的平均，越界的当作透明。
	/// </summary>
	static void BoxBlur(int[] source, int[] target, int length, int lines, int step, int lineStep)
	{
		for (int line = 0; line < lines; line++)
		{
			int start = line * lineStep;
			for (int i = 0; i < length; i++)
			{
				int at = start + i * step;
				for (int channel = 0; channel < 4; channel++)
				{
					int sum = source[at + channel];
					if (i > 0)
					{
						sum += source[at - step + channel];
					}
					if (i < length - 1)
					{
						sum += source[at + step + channel];
					}
					target[at + channel] = (sum + 1) / 3;
				}
			}
		}
	}
}

/// <summary>
/// 隐私模式下能打码的图标图片：Privacy.IconsHidden 为 true 时在原来的范围里画主题色的色团。
/// </summary>
internal sealed class PrivacyImage : Image
{
	protected override void OnRender(DrawingContext dc)
	{
		var source = Source;
		if (source == null || !Privacy.GetIconsHidden(this))
		{
			base.OnRender(dc);
			return;
		}
		// 和 Stretch="Uniform" 一样按图像比例放进格子里居中
		var size = RenderSize;
		double aspect = source.Height > 0 ? source.Width / source.Height : 1;
		double width = Math.Min(size.Width, size.Height * aspect);
		double height = aspect > 0 ? width / aspect : size.Height;
		dc.DrawImage(Privacy.BlurIcon(source), new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height));
	}
}
