using System.Windows;
using System.Windows.Controls;

namespace MyDesktop.Views;

/// <summary>
/// 分区标题栏上的标签条：铺满时标签平分整条的宽度；否则按各自需要的宽度从左往右（或靠右）排，放不下时平分宽度（标题过长显示省略号）。
/// 竖放的标题栏是整体旋转的，这里同样按横排处理。
/// </summary>
internal sealed class TabStripPanel : Panel
{
	/// <summary>
	/// 标签平分整条的宽度。可继承，设在标签条（ItemsControl）上传给这里。
	/// </summary>
	public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached("Fill", typeof(bool), typeof(TabStripPanel),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

	/// <summary>
	/// 不铺满时靠右排。可继承，设在标签条（ItemsControl）上传给这里。
	/// </summary>
	public static readonly DependencyProperty AlignRightProperty = DependencyProperty.RegisterAttached("AlignRight", typeof(bool), typeof(TabStripPanel),
			new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsArrange));

	public static void SetFill(DependencyObject element, bool value) => element.SetValue(FillProperty, value);

	public static bool GetFill(DependencyObject element) => (bool)element.GetValue(FillProperty);

	public static void SetAlignRight(DependencyObject element, bool value) => element.SetValue(AlignRightProperty, value);

	public static bool GetAlignRight(DependencyObject element) => (bool)element.GetValue(AlignRightProperty);

	// 测量时是否平分宽度（铺满，或放不下），排列时沿用
	bool _shared;

	protected override Size MeasureOverride(Size availableSize)
	{
		double total = 0;
		double height = 0;
		foreach (UIElement child in InternalChildren)
		{
			child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
			total += child.DesiredSize.Width;
			height = Math.Max(height, child.DesiredSize.Height);
		}
		bool fill = GetFill(this) && !double.IsInfinity(availableSize.Width);
		_shared = InternalChildren.Count > 0 && (fill || total > availableSize.Width);
		if (_shared)
		{
			double share = availableSize.Width / InternalChildren.Count;
			foreach (UIElement child in InternalChildren)
			{
				child.Measure(new Size(share, availableSize.Height));
			}
			total = availableSize.Width;
		}
		return new Size(total, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double share = InternalChildren.Count > 0 ? finalSize.Width / InternalChildren.Count : 0;
		double x = 0;
		if (!_shared && GetAlignRight(this))
		{
			x = Math.Max(0, finalSize.Width - InternalChildren.Cast<UIElement>().Sum(child => child.DesiredSize.Width));
		}
		foreach (UIElement child in InternalChildren)
		{
			double width = _shared ? share : child.DesiredSize.Width;
			child.Arrange(new Rect(x, 0, width, finalSize.Height));
			x += width;
		}
		return finalSize;
	}
}
