using System.Drawing;
using MyDesktop.Models;

namespace MyDesktop.Services;

internal static class FenceRoll
{
	public static RollEdge AutoEdge(Rectangle bounds, Rectangle work, int near)
	{
		if (Math.Abs(bounds.Top - work.Top) <= near) { return RollEdge.Top; }
		if (Math.Abs(bounds.Bottom - work.Bottom) <= near) { return RollEdge.Bottom; }
		if (Math.Abs(bounds.Left - work.Left) <= near) { return RollEdge.Left; }
		return Math.Abs(bounds.Right - work.Right) <= near ? RollEdge.Right : RollEdge.Top;
	}

	public static RectangleF Reveal(SizeF size, RollEdge edge, float thickness, double progress)
	{
		if (size.Width <= 0 || size.Height <= 0 || thickness <= 0 || progress < 0 || progress > 1) { throw new ArgumentOutOfRangeException(nameof(progress)); }
		float width = Math.Min(thickness, size.Width), height = Math.Min(thickness, size.Height);
		float x = width + (size.Width - width) * (float)progress, y = height + (size.Height - height) * (float)progress;
		return edge switch
		{
			RollEdge.Top => new(0, 0, size.Width, y),
			RollEdge.Bottom => new(0, size.Height - y, size.Width, y),
			RollEdge.Left => new(0, 0, x, size.Height),
			RollEdge.Right => new(size.Width - x, 0, x, size.Height),
		};
	}

	public static RectangleF Content(SizeF size, RollEdge edge, float thickness) => edge switch
	{
		RollEdge.Top => new(0, Math.Min(thickness, size.Height), size.Width, Math.Max(0, size.Height - thickness)),
		RollEdge.Bottom => new(0, 0, size.Width, Math.Max(0, size.Height - thickness)),
		RollEdge.Left => new(Math.Min(thickness, size.Width), 0, Math.Max(0, size.Width - thickness), size.Height),
		RollEdge.Right => new(0, 0, Math.Max(0, size.Width - thickness), size.Height),
	};

	public static Rectangle Collapsed(Rectangle bounds, RollEdge edge, int thickness)
	{
		var part = Reveal(new(bounds.Width, bounds.Height), edge, thickness, 0);
		return new(bounds.Left + (int)part.Left, bounds.Top + (int)part.Top, (int)part.Width, (int)part.Height);
	}

	public static Rectangle FromStrip(Rectangle strip, Size expanded, RollEdge edge) => edge switch
	{
		RollEdge.Top or RollEdge.Left => new(strip.Location, expanded),
		RollEdge.Bottom => new(strip.Left, strip.Bottom - expanded.Height, expanded.Width, expanded.Height),
		RollEdge.Right => new(strip.Right - expanded.Width, strip.Top, expanded.Width, expanded.Height),
	};

	public static RollEdge MovedEdge(Rectangle strip, RollEdge previous, Rectangle work, int near) => previous switch
	{
		RollEdge.Top or RollEdge.Bottom => Math.Abs(strip.Bottom - work.Bottom) <= near ? RollEdge.Bottom : RollEdge.Top,
		RollEdge.Left or RollEdge.Right => Math.Abs(strip.Left - work.Left) <= near ? RollEdge.Left
			: Math.Abs(strip.Right - work.Right) <= near ? RollEdge.Right : previous,
	};
}
