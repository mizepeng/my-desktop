using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 文字阴影：在里面的 TextBlock 后面叠两份黑色副本，一份加模糊当作阴影，一份用着色器画淡描边（TextOutlineEffect），正文本身不带任何效果。
/// DropShadowEffect 直接加在文字上时，文字要先画进中间位图再合成，笔画明显发虚（实测，用户反馈「字体发虚」）；
/// 正文不带效果才和不加阴影时一样清楚。只有模糊阴影时浅色壁纸上反差不够，边缘清晰度只有资源管理器桌面的一半左右，看着还是虚（#10，实测对比）；
/// 加上八个方向各 1 像素、每层 25% 的描边后和资源管理器相当，描边再浓就比系统桌面明显发黑。
/// 副本的文字、字体、换行、截断、边距、可见性等都跟着正文。
/// UseGdi 为 true 时（桌面图标层）不建副本，正文只参与排版、不显示，由 GdiText 用 ClearType 把名称连同阴影画成位图，对齐到整像素贴上去：
/// WPF 文字在透明窗口里竖笔画常落在半个像素上发虚，资源管理器桌面用的就是 GDI ClearType（见 docs/dev/constraints.md）。
/// 隐私模式打码时（Privacy.NamesHidden，IsTitle 为 true 时看 Privacy.TitlesHidden）正文照常排版但被裁掉、不建副本，改画 Privacy.BlurText 的模糊位图。
/// </summary>
internal sealed class TextShadow : Decorator
{
	static readonly TextOutlineEffect OutlineEffect = CreateOutline();

	/// <summary>
	/// 加在副本上的模糊效果；为 null 时不画阴影。
	/// </summary>
	public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(nameof(Shadow), typeof(Effect), typeof(TextShadow),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((TextShadow)d).UpdateShadow()));

	/// <summary>
	/// 阴影的不透明度。
	/// </summary>
	public static readonly DependencyProperty ShadowOpacityProperty = DependencyProperty.Register(nameof(ShadowOpacity), typeof(double), typeof(TextShadow),
			new FrameworkPropertyMetadata(0.75, (d, _) => ((TextShadow)d).UpdateShadow()));

	// 每个绑定约占 1.2 KB（实测 300 个名称绑 19 个属性多占 7.4 MB），只绑运行中会变的：
	// 文字、可见性和不透明度（触发器会改）、最大高度（选中展开完整名称时会改）一律绑定
	static readonly DependencyProperty[] AlwaysBound = [TextBlock.TextProperty, VisibilityProperty, OpacityProperty, MaxHeightProperty];

	// 随字体设置改动的资源引用，正文设了才绑定
	static readonly DependencyProperty[] BoundIfSet = [TextBlock.FontSizeProperty, MarginProperty, TextBlock.LineHeightProperty];

	// 写死在模板里、不会再变的，第一次排版时照抄
	static readonly DependencyProperty[] CopiedIfSet =
	[
		TextBlock.FontFamilyProperty, TextBlock.FontWeightProperty, TextBlock.FontStyleProperty, TextBlock.TextAlignmentProperty,
		TextBlock.TextWrappingProperty, TextBlock.TextTrimmingProperty, TextBlock.LineStackingStrategyProperty, TextBlock.PaddingProperty,
		HorizontalAlignmentProperty, VerticalAlignmentProperty, LayoutTransformProperty,
	];

	/// <summary>
	/// 用 GDI 画名称，设在窗口上往下继承；默认 false，分区仍用 WPF 文字。
	/// </summary>
	public static readonly DependencyProperty UseGdiProperty = DependencyProperty.RegisterAttached("UseGdi", typeof(bool), typeof(TextShadow),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as TextShadow)?.UpdateShadow()));

	/// <summary>
	/// 这是分区标题（包括标签、竖排标题），隐私模式按「分区标题」打码；默认是图标名称。
	/// </summary>
	public static readonly DependencyProperty IsTitleProperty = DependencyProperty.Register(nameof(IsTitle), typeof(bool), typeof(TextShadow),
			new FrameworkPropertyMetadata(false, (d, _) => ((TextShadow)d).UpdatePrivacy()));

	// GDI 画法下跟着正文的文字，变了就重画
	static readonly DependencyProperty GdiTextProperty = DependencyProperty.Register("GdiText", typeof(string), typeof(TextShadow),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	readonly TextBlock _copy = new() { IsHitTestVisible = false };
	// 描边由着色器向四周扩，副本本身要是不透明的黑色，不偏移
	readonly TextBlock _outline = new() { IsHitTestVisible = false, Foreground = Brushes.Black, Effect = OutlineEffect };
	bool _copyAttached;
	// 已经照着正文设好副本
	TextBlock? _mirrored;
	// GDI 画法：正在用的正文、画好的位图和画它时的参数
	TextBlock? _gdiText;
	GdiText.Result? _gdi;
	GdiText.Request? _gdiRequest;
	// 隐私模式：被打码的正文、画好的模糊位图和画它时的参数
	TextBlock? _obscured;
	BitmapSource? _blur;
	BlurKey? _blurKey;

	readonly record struct BlurKey(int Level, string Text, FontFamily Family, double Size, FontWeight Weight, FontStyle Style, Brush? Foreground,
			double Width, double Height, TextAlignment Alignment, TextWrapping Wrapping, TextTrimming Trimming, double LineHeight);

	public static void SetUseGdi(DependencyObject element, bool value) => element.SetValue(UseGdiProperty, value);

	public static bool GetUseGdi(DependencyObject element) => (bool)element.GetValue(UseGdiProperty);

	public Effect? Shadow
	{
		get => (Effect?)GetValue(ShadowProperty);
		set => SetValue(ShadowProperty, value);
	}

	public double ShadowOpacity
	{
		get => (double)GetValue(ShadowOpacityProperty);
		set => SetValue(ShadowOpacityProperty, value);
	}

	public bool IsTitle
	{
		get => (bool)GetValue(IsTitleProperty);
		set => SetValue(IsTitleProperty, value);
	}

	protected override int VisualChildrenCount => (Child != null ? 1 : 0) + (_copyAttached ? 2 : 0);

	protected override Visual GetVisualChild(int index)
	{
		// 两份副本在下面：模糊阴影最底，描边其次
		if (_copyAttached && index < 2)
		{
			return index == 0 ? _copy : _outline;
		}
		return Child ?? throw new ArgumentOutOfRangeException(nameof(index));
	}

	static TextOutlineEffect CreateOutline()
	{
		var effect = new TextOutlineEffect { Strength = 0.25, Offset = 1 };
		effect.Freeze();
		return effect;
	}

	protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
	{
		base.OnVisualChildrenChanged(visualAdded, visualRemoved);
		UpdateShadow();
	}

	protected override Size MeasureOverride(Size constraint)
	{
		if (_copyAttached)
		{
			// 第一次排版时正文的属性肯定都已设好，加进来的那一刻不一定
			if (Child is TextBlock text && text != _mirrored)
			{
				Mirror(text);
			}
			_copy.Measure(constraint);
			_outline.Measure(constraint);
		}
		return base.MeasureOverride(constraint);
	}

	void Mirror(TextBlock text)
	{
		_mirrored = text;
		foreach (var copy in new[] { _copy, _outline })
		{
			foreach (var property in AlwaysBound)
			{
				copy.SetBinding(property, new Binding(property.Name) { Source = text });
			}
			foreach (var property in BoundIfSet)
			{
				if (IsSet(text, property))
				{
					copy.SetBinding(property, new Binding(property.Name) { Source = text });
				}
			}
			foreach (var property in CopiedIfSet)
			{
				if (IsSet(text, property))
				{
					copy.SetValue(property, text.GetValue(property));
				}
			}
		}
		// 阴影在文字下方 1 处。RenderTransform 是在 LayoutTransform 转过之后才加的（实测），
		// 竖放标题栏里转回正立的文字要把偏移也跟着转，否则阴影跑到文字侧面
		var offset = _copy.LayoutTransform.Value.Transform(new Vector(0, 1));
		_copy.RenderTransform = new TranslateTransform(offset.X, offset.Y);
	}

	/// <summary>
	/// 正文自己设了这个属性（不是默认值，也不是从上层继承的字体、字号）。
	/// </summary>
	static bool IsSet(TextBlock text, DependencyProperty property)
	{
		var source = DependencyPropertyHelper.GetValueSource(text, property).BaseValueSource;
		return source is not (BaseValueSource.Default or BaseValueSource.Inherited);
	}

	protected override Size ArrangeOverride(Size arrangeSize)
	{
		if (_copyAttached)
		{
			_copy.Arrange(new Rect(arrangeSize));
			_outline.Arrange(new Rect(arrangeSize));
		}
		// 正文的大小、行数（选中时展开完整名称）、字体变了都会重新排版
		if (_gdiText != null || _obscured != null)
		{
			InvalidateVisual();
		}
		return base.ArrangeOverride(arrangeSize);
	}

	void UpdateShadow()
	{
		UpdateGdi();
		UpdateObscured();
		bool show = Shadow != null && Child is TextBlock && _gdiText == null && _obscured == null;
		if (show != _copyAttached)
		{
			_copyAttached = show;
			if (show)
			{
				AddVisualChild(_copy);
				AddVisualChild(_outline);
			}
			else
			{
				RemoveVisualChild(_copy);
				RemoveVisualChild(_outline);
			}
			InvalidateMeasure();
		}
		_copy.Effect = Shadow;
		var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(ShadowOpacity, 0, 1) * 255), 0, 0, 0));
		brush.Freeze();
		_copy.Foreground = brush;
	}

	/// <summary>
	/// 进入或退出 GDI 画法：进入时正文透明（仍参与排版和触发器），文字和可见性变了就重画。
	/// </summary>
	void UpdateGdi()
	{
		var text = GetUseGdi(this) ? Child as TextBlock : null;
		if (text != _gdiText)
		{
			if (_gdiText != null)
			{
				_gdiText.ClearValue(OpacityProperty);
				_gdiText.IsVisibleChanged -= GdiText_IsVisibleChanged;
				ClearValue(GdiTextProperty);
				ClearValue(RenderOptions.BitmapScalingModeProperty);
			}
			_gdiText = text;
			if (text != null)
			{
				text.Opacity = 0;
				text.IsVisibleChanged += GdiText_IsVisibleChanged;
				SetBinding(GdiTextProperty, new Binding(nameof(TextBlock.Text)) { Source = text });
				// 位图按物理像素一比一贴上去，不插值
				RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
			}
		}
		if (text == null)
		{
			_gdi = null;
			_gdiRequest = null;
		}
		InvalidateVisual();
	}

	void GdiText_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => InvalidateVisual();

	/// <summary>
	/// 隐私模式的打码状态变了（Privacy 的附加属性、IsTitle）：进出打码，副本跟着摘掉或加回。
	/// </summary>
	internal void UpdatePrivacy()
	{
		var text = Hidden() ? Child as TextBlock : null;
		if (text != _obscured)
		{
			UpdateShadow();
		}
	}

	bool Hidden() => IsTitle ? Privacy.GetTitlesHidden(this) : Privacy.GetNamesHidden(this);

	/// <summary>
	/// 进入或退出打码：进入时把正文整个裁掉（不动它的不透明度和可见性，触发器照常生效），文字和可见性变了就重画。
	/// </summary>
	void UpdateObscured()
	{
		var text = Hidden() ? Child as TextBlock : null;
		if (text == _obscured)
		{
			return;
		}
		if (_obscured != null)
		{
			_obscured.ClearValue(ClipProperty);
			_obscured.IsVisibleChanged -= GdiText_IsVisibleChanged;
			if (_gdiText == null)
			{
				ClearValue(GdiTextProperty);
			}
		}
		_obscured = text;
		// 露出后多半还会再打码（鼠标移开），隐私模式还要给这类文字打码时留着模糊位图，不用重画
		if (!(IsTitle ? Privacy.Current.Titles : Privacy.Current.Names))
		{
			_blur = null;
			_blurKey = null;
		}
		if (text != null)
		{
			text.Clip = Geometry.Empty;
			text.IsVisibleChanged += GdiText_IsVisibleChanged;
			SetBinding(GdiTextProperty, new Binding(nameof(TextBlock.Text)) { Source = text });
		}
		else if (_gdiText != null)
		{
			SetBinding(GdiTextProperty, new Binding(nameof(TextBlock.Text)) { Source = _gdiText });
		}
		InvalidateVisual();
	}

	/// <summary>
	/// 打码：按正文排好的大小画模糊位图，跟着正文的变换（竖排标题转过 90°）贴上去，不透明度跟着正文（GDI 画法下正文一直是透明的，不跟）。
	/// </summary>
	void RenderObscured(DrawingContext dc, TextBlock text)
	{
		if (!text.IsVisible || string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0 || text.ActualHeight <= 0)
		{
			return;
		}
		int level = Privacy.GetLevel(this);
		var key = new BlurKey(level, text.Text, text.FontFamily, text.FontSize, text.FontWeight, text.FontStyle, text.Foreground, text.ActualWidth, text.ActualHeight,
				text.TextAlignment, text.TextWrapping, text.TextTrimming, text.LineHeight);
		if (key != _blurKey)
		{
			_blur = Privacy.BlurText(text, level);
			_blurKey = key;
		}
		var group = new DrawingGroup
		{
			Transform = text.TransformToVisual(this) as Transform ?? Transform.Identity,
			Opacity = _gdiText == null ? text.Opacity : 1,
		};
		RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.Linear);
		group.Children.Add(new ImageDrawing(_blur, new Rect(0, 0, text.ActualWidth, text.ActualHeight)));
		dc.DrawDrawing(group);
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		InvalidateVisual();
	}

	/// <summary>
	/// GDI 画法：按正文排好的大小和行数画位图，左上角对齐到整个物理像素后贴上去。
	/// </summary>
	protected override void OnRender(DrawingContext dc)
	{
		if (_obscured != null)
		{
			RenderObscured(dc, _obscured);
			return;
		}
		var text = _gdiText;
		if (text == null || !text.IsVisible || string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0 || text.ActualHeight <= 0
				|| PresentationSource.FromVisual(this)?.RootVisual is not Visual root)
		{
			return;
		}
		double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
		var origin = text.TransformToAncestor(root).Transform(default);
		var self = TransformToAncestor(root).Transform(default);
		int left = (int)Math.Round(origin.X * scale);
		int top = (int)Math.Round(origin.Y * scale);
		double lineHeight = double.IsNaN(text.LineHeight) || text.LineHeight <= 0 ? text.FontSize * 4 / 3 : text.LineHeight;
		// 阴影照资源管理器桌面的样子，不用 WPF 画法的模糊参数和描边
		var shadow = Shadow == null ? null : GdiText.ExplorerShadow(scale);
		var request = new GdiText.Request(text.Text, FaceName(text.FontFamily), (int)Math.Round(text.FontSize * scale), text.FontWeight.ToOpenTypeWeight(),
				text.FontStyle != FontStyles.Normal, Align(text.TextAlignment), text.TextWrapping != TextWrapping.NoWrap, text.TextTrimming != TextTrimming.None,
				(int)Math.Round(text.ActualWidth * scale), Math.Max(1, (int)Math.Round(text.ActualHeight / lineHeight)),
				(text.Foreground as SolidColorBrush)?.Color ?? Colors.White, shadow, 96 * scale);
		if (request != _gdiRequest)
		{
			_gdi = GdiText.Render(request);
			_gdiRequest = request;
		}
		var bitmap = _gdi!.Bitmap;
		int pad = _gdi.Padding;
		dc.DrawImage(bitmap, new Rect((left - pad) / scale - self.X, (top - pad) / scale - self.Y, bitmap.PixelWidth / scale, bitmap.PixelHeight / scale));
	}

	/// <summary>
	/// WPF 字体族对应的 GDI 字体名：有后备列表时取第一个，嵌入字体取 # 后面的名称。
	/// </summary>
	static string FaceName(FontFamily family)
	{
		var name = family.Source.Split(',')[0].Trim();
		int hash = name.IndexOf('#');
		return hash >= 0 ? name[(hash + 1)..] : name;
	}

	static uint Align(TextAlignment alignment) => alignment switch
	{
		TextAlignment.Center => DT_CENTER,
		TextAlignment.Right => DT_RIGHT,
		TextAlignment.Left or TextAlignment.Justify => 0,
	};
}
