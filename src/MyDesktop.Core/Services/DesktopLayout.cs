using System.Drawing;
using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

internal sealed record DesktopMonitor(Rectangle WorkArea, double Scale);
internal sealed record DesktopIconPlacement(DesktopEntry Entry, Rectangle Bounds, int IconSize);

internal static class DesktopLayout
{
	public static IReadOnlyList<DesktopIconPlacement> Build(DesktopSnapshot snapshot, IEnumerable<FenceSettings> fences,
			IReadOnlyList<Rectangle> blocked, IReadOnlyList<DesktopMonitor> monitors)
	{
		var assigned = fences.Where(fence => !fence.IsPortal).SelectMany(fence => fence.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var loose = snapshot.Items.Where(entry => !assigned.Contains(entry.Key)).ToArray();
		if (loose.Length == 0)
		{
			return [];
		}
		if (snapshot.Spacing.X <= 0 || snapshot.Spacing.Y <= 0 || snapshot.IconSize <= 0 || monitors.Count == 0
			|| snapshot.ListBounds.Width <= 0 || snapshot.ListBounds.Height <= 0
			|| monitors.Any(monitor => monitor.Scale <= 0 || monitor.WorkArea.Width <= 0 || monitor.WorkArea.Height <= 0))
		{
			throw new InvalidDataException("桌面视图的间距、图标大小或显示器信息无效，不能接管系统图标。");
		}
		var result = new List<DesktopIconPlacement>(loose.Length);
		var spacing = new Size(snapshot.Spacing.X, snapshot.Spacing.Y);
		var origin = new Point(snapshot.ListBounds.Left + snapshot.Items.Min(entry => entry.Position.X),
			snapshot.ListBounds.Top + snapshot.Items.Min(entry => entry.Position.Y));
		int rows = Math.Max(1, (MonitorAt(origin, monitors).WorkArea.Bottom - origin.Y) / spacing.Height);
		int columns = Math.Max(1, (snapshot.ListBounds.Right - origin.X) / spacing.Width);
		int index = 0;
		foreach (var entry in snapshot.AutoArrange ? loose.OrderBy(entry => entry.Position.X).ThenBy(entry => entry.Position.Y) : loose.AsEnumerable())
		{
			Point cell;
			if (snapshot.AutoArrange)
			{
				Rectangle bounds;
				bool available;
				do
				{
					if (index >= (long)rows * columns)
					{
						throw new InvalidOperationException("桌面没有足够空位显示全部散放图标，已保留系统图标。");
					}
					cell = new Point(origin.X + index / rows * spacing.Width, origin.Y + index % rows * spacing.Height);
					index++;
					bounds = new Rectangle(cell, spacing);
					var inner = bounds;
					inner.Inflate(-spacing.Width / 8, -spacing.Height / 8);
					available = monitors.Any(monitor => monitor.WorkArea.Contains(new Point(cell.X + spacing.Width / 2, cell.Y + spacing.Height / 4)))
						&& !blocked.Any(rectangle => rectangle.IntersectsWith(inner));
				} while (!available);
			}
			else
			{
				cell = new Point(snapshot.ListBounds.Left + entry.Position.X, snapshot.ListBounds.Top + entry.Position.Y);
				double scale = MonitorAt(cell, monitors).Scale;
				int pixels = (int)Math.Round(snapshot.IconSize * scale);
				cell.Offset(-Math.Max(0, (spacing.Width - pixels) / 2), (int)Math.Round(2 * scale));
			}
			result.Add(new DesktopIconPlacement(entry, new Rectangle(cell, spacing), snapshot.IconSize));
		}
		return result;
	}

	static DesktopMonitor MonitorAt(Point point, IReadOnlyList<DesktopMonitor> monitors) => monitors.MinBy(monitor =>
	{
		var work = monitor.WorkArea;
		double dx = Math.Max(Math.Max(work.Left - point.X, 0), point.X - work.Right);
		double dy = Math.Max(Math.Max(work.Top - point.Y, 0), point.Y - work.Bottom);
		return dx * dx + dy * dy;
	})!;
}
