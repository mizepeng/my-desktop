using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MyDesktop.Views;

/// <summary>
/// 文字阴影：在里面的 TextBlock 后面叠一份黑色副本，副本加上模糊当作阴影，正文本身不带任何效果。
/// DropShadowEffect 直接加在文字上时，文字要先画进中间位图再合成，笔画明显发虚（实测，用户反馈「字体发虚」）；
/// 正文不带效果才和不加阴影时一样清楚。副本的文字、字体、换行、截断、边距、可见性等都跟着正文。
/// </summary>
internal sealed class TextShadow : Decorator
{
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

	readonly TextBlock _copy = new() { IsHitTestVisible = false };
	bool _copyAttached;
	// 已经照着正文设好副本
	TextBlock? _mirrored;

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

	protected override int VisualChildrenCount => (Child != null ? 1 : 0) + (_copyAttached ? 1 : 0);

	protected override Visual GetVisualChild(int index)
	{
		// 副本在下面
		if (_copyAttached && index == 0)
		{
			return _copy;
		}
		return Child ?? throw new ArgumentOutOfRangeException(nameof(index));
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
		}
		return base.MeasureOverride(constraint);
	}

	void Mirror(TextBlock text)
	{
		_mirrored = text;
		foreach (var property in AlwaysBound)
		{
			_copy.SetBinding(property, new Binding(property.Name) { Source = text });
		}
		foreach (var property in BoundIfSet)
		{
			if (IsSet(text, property))
			{
				_copy.SetBinding(property, new Binding(property.Name) { Source = text });
			}
		}
		foreach (var property in CopiedIfSet)
		{
			if (IsSet(text, property))
			{
				_copy.SetValue(property, text.GetValue(property));
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
		}
		return base.ArrangeOverride(arrangeSize);
	}

	void UpdateShadow()
	{
		bool show = Shadow != null && Child is TextBlock;
		if (show != _copyAttached)
		{
			_copyAttached = show;
			if (show)
			{
				AddVisualChild(_copy);
			}
			else
			{
				RemoveVisualChild(_copy);
			}
			InvalidateMeasure();
		}
		_copy.Effect = Shadow;
		var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(ShadowOpacity, 0, 1) * 255), 0, 0, 0));
		brush.Freeze();
		_copy.Foreground = brush;
	}
}
