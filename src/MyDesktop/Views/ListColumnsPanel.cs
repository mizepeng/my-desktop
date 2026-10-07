using System.Windows;
using System.Windows.Controls;

namespace MyDesktop.Views;

/// <summary>
/// 分区列表视图的排布：和资源管理器的「小图标」视图一样从左往右、从上往下排，分区多宽就排几列（每列至少 MinColumnWidth），
/// 各列平分宽度；一行的高度取这一行里最高的那项。
/// </summary>
internal sealed class ListColumnsPanel : Panel
{
	public const double MinColumnWidth = 200;

	/// <summary>
	/// 上次排布时的列数，拖动调整顺序时据此判断插入标记画成横线还是竖线。
	/// </summary>
	public int Columns { get; private set; } = 1;

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
		Columns = Math.Max(1, (int)(width / MinColumnWidth));
		double columnWidth = width / Columns;
		double height = 0;
		double rowHeight = 0;
		for (int i = 0; i < InternalChildren.Count; i++)
		{
			var child = InternalChildren[i];
			child.Measure(new Size(columnWidth, double.PositiveInfinity));
			rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
			if ((i + 1) % Columns == 0 || i == InternalChildren.Count - 1)
			{
				height += rowHeight;
				rowHeight = 0;
			}
		}
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double columnWidth = finalSize.Width / Columns;
		double y = 0;
		for (int start = 0; start < InternalChildren.Count; start += Columns)
		{
			int end = Math.Min(start + Columns, InternalChildren.Count);
			double rowHeight = 0;
			for (int i = start; i < end; i++)
			{
				rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
			}
			for (int i = start; i < end; i++)
			{
				InternalChildren[i].Arrange(new Rect((i - start) * columnWidth, y, columnWidth, rowHeight));
			}
			y += rowHeight;
		}
		return finalSize;
	}
}
