using System.Drawing;
using MyDesktop.Models;

namespace MyDesktop.Services;

[Flags]
internal enum FenceEdges
{
	None = 0,
	Left = 1,
	Right = 2,
	Top = 4,
	Bottom = 8,
}

/// <summary>
/// 以物理像素计算分区吸附与避让，沿用原界面的邻接、最小退让和最近空位规则。
/// </summary>
internal static class FenceGeometry
{
	public static Rectangle Move(Rectangle start, Point delta, Rectangle work, IReadOnlyList<Rectangle> others, AppSettings settings, double scale)
	{
		var rect = new Rectangle(start.X + delta.X, start.Y + delta.Y, start.Width, start.Height);
		if (!settings.SnapToEdges) { return rect; }
		var lines = Collect(rect, work, others, settings, scale);
		int threshold = (int)(12 * scale);
		rect.Offset(Closer(Snap(rect.Left, lines.Left, threshold), Snap(rect.Right, lines.Right, threshold)),
			Closer(Snap(rect.Top, lines.Top, threshold), Snap(rect.Bottom, lines.Bottom, threshold)));
		return rect;
	}

	public static Rectangle Resize(Rectangle start, Point delta, FenceEdges edges, Rectangle work, IReadOnlyList<Rectangle> others,
			AppSettings settings, double scale, Size minimum, IReadOnlyList<int> widths, IReadOnlyList<int> heights)
	{
		bool left = edges.HasFlag(FenceEdges.Left), right = edges.HasFlag(FenceEdges.Right);
		bool top = edges.HasFlag(FenceEdges.Top), bottom = edges.HasFlag(FenceEdges.Bottom);
		if (edges == FenceEdges.None || left && right || top && bottom || (edges & ~(FenceEdges.Left | FenceEdges.Right | FenceEdges.Top | FenceEdges.Bottom)) != 0)
		{
			throw new ArgumentException("调整大小的边无效。", nameof(edges));
		}
		int l = start.Left + (left ? delta.X : 0), r = start.Right + (right ? delta.X : 0);
		int t = start.Top + (top ? delta.Y : 0), b = start.Bottom + (bottom ? delta.Y : 0);
		var lines = settings.SnapToEdges ? Collect(Rectangle.FromLTRB(l, t, r, b), work, others, settings, scale) : new SnapLines();
		if (settings.SnapToGrid)
		{
			lines.Left.AddRange(widths.Select(width => r - width));
			lines.Right.AddRange(widths.Select(width => l + width));
			lines.Top.AddRange(heights.Select(height => b - height));
			lines.Bottom.AddRange(heights.Select(height => t + height));
		}
		int threshold = (int)(12 * scale);
		if (left) { l += Snap(l, lines.Left, threshold) ?? 0; }
		if (right) { r += Snap(r, lines.Right, threshold) ?? 0; }
		if (top) { t += Snap(t, lines.Top, threshold) ?? 0; }
		if (bottom) { b += Snap(b, lines.Bottom, threshold) ?? 0; }
		if (r - l < minimum.Width) { if (left) { l = r - minimum.Width; } else { r = l + minimum.Width; } }
		if (b - t < minimum.Height) { if (top) { t = b - minimum.Height; } else { b = t + minimum.Height; } }
		int gap = (int)Math.Round(settings.SnapGap * scale);
		foreach (var other in others)
		{
			var inflated = Rectangle.Inflate(other, gap, gap);
			if (other.IntersectsWith(start) || !inflated.IntersectsWith(Rectangle.FromLTRB(l, t, r, b))) { continue; }
			int pullRight = right && other.Left >= start.Right ? r - Math.Max(start.Right, other.Left - gap) : 0;
			int pullLeft = left && other.Right <= start.Left ? Math.Min(start.Left, other.Right + gap) - l : 0;
			int pullBottom = bottom && other.Top >= start.Bottom ? b - Math.Max(start.Bottom, other.Top - gap) : 0;
			int pullTop = top && other.Bottom <= start.Top ? Math.Min(start.Top, other.Bottom + gap) - t : 0;
			int pull = new[] { pullRight, pullLeft, pullBottom, pullTop }.Where(value => value > 0).DefaultIfEmpty(0).Min();
			if (pull == 0) { continue; }
			if (pull == pullRight) { r -= pull; }
			else if (pull == pullLeft) { l += pull; }
			else if (pull == pullBottom) { b -= pull; }
			else { t += pull; }
		}
		return Rectangle.FromLTRB(l, t, r, b);
	}

	public static Rectangle? FindFreeSpot(Rectangle rect, Rectangle work, IReadOnlyList<Rectangle> others, int gap)
	{
		if (!others.Any(other => other.IntersectsWith(rect))) { return rect; }
		int maxLeft = Math.Max(work.Left, work.Right - rect.Width), maxTop = Math.Max(work.Top, work.Bottom - rect.Height);
		var lefts = new List<int> { Math.Clamp(rect.Left, work.Left, maxLeft), work.Left, maxLeft };
		var tops = new List<int> { Math.Clamp(rect.Top, work.Top, maxTop), work.Top, maxTop };
		foreach (var other in others)
		{
			lefts.Add(other.Right + gap);
			lefts.Add(other.Left - gap - rect.Width);
			tops.Add(other.Bottom + gap);
			tops.Add(other.Top - gap - rect.Height);
		}
		Rectangle? best = null;
		long distance = long.MaxValue;
		foreach (int left in lefts.Where(x => x >= work.Left && x <= maxLeft).Distinct())
		{
			foreach (int top in tops.Where(y => y >= work.Top && y <= maxTop).Distinct())
			{
				long dx = (long)left - rect.Left, dy = (long)top - rect.Top;
				var candidate = new Rectangle(left, top, rect.Width, rect.Height);
				if (dx * dx + dy * dy < distance && !others.Any(other => other.IntersectsWith(candidate)))
				{
					best = candidate;
					distance = dx * dx + dy * dy;
				}
			}
		}
		return best;
	}

	sealed class SnapLines
	{
		public readonly List<int> Left = [], Right = [], Top = [], Bottom = [];
	}

	static SnapLines Collect(Rectangle rect, Rectangle work, IReadOnlyList<Rectangle> others, AppSettings settings, double scale)
	{
		var lines = new SnapLines();
		lines.Left.Add(work.Left);
		lines.Right.Add(work.Right);
		lines.Top.Add(work.Top);
		lines.Bottom.Add(work.Bottom);
		int gap = (int)Math.Round(settings.SnapGap * scale), near = (int)(12 * scale);
		foreach (var other in others)
		{
			if (rect.Top < other.Bottom + near && other.Top < rect.Bottom + near)
			{
				lines.Left.Add(other.Left);
				lines.Left.Add(other.Right + gap);
				lines.Right.Add(other.Right);
				lines.Right.Add(other.Left - gap);
			}
			if (rect.Left < other.Right + near && other.Left < rect.Right + near)
			{
				lines.Top.Add(other.Top);
				lines.Top.Add(other.Bottom + gap);
				lines.Bottom.Add(other.Bottom);
				lines.Bottom.Add(other.Top - gap);
			}
		}
		return lines;
	}

	static int? Snap(int edge, List<int> lines, int threshold)
	{
		int? best = null;
		foreach (int line in lines)
		{
			int delta = line - edge;
			if (Math.Abs(delta) <= threshold && (best == null || Math.Abs(delta) < Math.Abs(best.Value))) { best = delta; }
		}
		return best;
	}

	static int Closer(int? a, int? b) => a is int x && (b is not int y || Math.Abs(x) <= Math.Abs(y)) ? x : b ?? 0;
}
