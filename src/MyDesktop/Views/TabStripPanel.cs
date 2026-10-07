using System.Windows;
using System.Windows.Controls;

namespace MyDesktop.Views;

/// <summary>
/// 分区标题栏上的标签条：标签按各自需要的宽度从左往右排，放不下时平分宽度（标题过长显示省略号）。
/// 竖放的标题栏是整体旋转的，这里同样按横排处理。
/// </summary>
internal sealed class TabStripPanel : Panel
{
	// 测量时是否放不下、要平分宽度，排列时沿用
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
		_shared = InternalChildren.Count > 0 && total > availableSize.Width;
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
		foreach (UIElement child in InternalChildren)
		{
			double width = _shared ? share : child.DesiredSize.Width;
			child.Arrange(new Rect(x, 0, width, finalSize.Height));
			x += width;
		}
		return finalSize;
	}
}
