using System.Windows;
using System.Windows.Controls;

namespace MyDesktop.Views;

/// <summary>
/// 映射分区进入子文件夹后的标题：「下载 › 图片 › 2024」逐级排开，子元素依次是省略号、第一级、分隔符、第二级……
/// 放不下时和资源管理器的地址栏一样从左边起收起前面几级、换成省略号，当前所在的那一级始终留着，还放不下就截断它。
/// </summary>
internal sealed class BreadcrumbPanel : Panel
{
	// 这次排版显示哪些子元素（下标）；最后一级被截断时它的宽度
	readonly List<int> _shown = [];
	double _lastWidth = double.NaN;

	protected override Size MeasureOverride(Size availableSize)
	{
		var unlimited = new Size(double.PositiveInfinity, availableSize.Height);
		foreach (UIElement child in InternalChildren)
		{
			child.Measure(unlimited);
		}
		_shown.Clear();
		_lastWidth = double.NaN;
		int count = InternalChildren.Count;
		if (count < 2)
		{
			return new Size(0, 0);
		}
		double Width(int i) => InternalChildren[i].DesiredSize.Width;
		double height = InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Height);
		double all = Enumerable.Range(1, count - 1).Sum(Width);
		if (all <= availableSize.Width)
		{
			_shown.AddRange(Enumerable.Range(1, count - 1));
			return new Size(all, height);
		}
		// 从第一级起每次收起一级连同它后面的分隔符，直到放得下；最后一级（下标 count - 1）不收
		double ellipsis = Width(0);
		double rest = all;
		int first = 1;
		while (first < count - 1 && ellipsis + rest > availableSize.Width)
		{
			rest -= Width(first) + Width(first + 1);
			first += 2;
		}
		_shown.Add(0);
		_shown.AddRange(Enumerable.Range(first, count - first));
		if (ellipsis + rest > availableSize.Width)
		{
			// 只剩省略号和当前这一级还放不下：截断当前这一级
			_lastWidth = Math.Max(0, availableSize.Width - ellipsis);
			InternalChildren[count - 1].Measure(new Size(_lastWidth, availableSize.Height));
			return new Size(availableSize.Width, height);
		}
		return new Size(ellipsis + rest, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double x = 0;
		for (int i = 0; i < InternalChildren.Count; i++)
		{
			var child = InternalChildren[i];
			if (!_shown.Contains(i))
			{
				child.Arrange(new Rect(0, 0, 0, 0));
				continue;
			}
			double width = i == InternalChildren.Count - 1 && !double.IsNaN(_lastWidth) ? _lastWidth : child.DesiredSize.Width;
			child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, width, child.DesiredSize.Height));
			x += width;
		}
		return finalSize;
	}
}
