using System.Diagnostics;
using System.Drawing;
using Microsoft.UI.Xaml.Controls;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;
using FenceView = MyDesktop.WinUI.Views.FenceView;

namespace MyDesktop.WinUI.Services;

internal sealed class FencePlacement(DesktopSurface surface, AppSettings settings, bool readOnly)
{
	FenceView? _active;
	FenceView? _mergeTarget;
	Rectangle _start;
	Point _origin;
	FenceEdges _edges;
	(IReadOnlyList<int> Widths, IReadOnlyList<int> Heights) _grid;
	int _version;
	bool _moved;
	public bool IsActive => _active != null;
	internal event Action? Changed;
	internal event Func<FenceView, FenceView, bool>? MergeRequested;

	internal bool Begin(FenceView fence, Point point, FenceEdges edges)
	{
		if (readOnly || fence.Frame.Active.Locked || fence.IsEditing || surface.PlacementAllowed?.Invoke() == false) { return false; }
		if (_active != null)
		{
			_version++;
			var previous = _active;
			_active = null;
			previous.CancelPlacementPointer();
			previous.CommitPosition();
		}
		_active = fence;
		fence.FinishRoll();
		SetMergeTarget(null);
		_moved = false;
		surface.RaiseFence(fence);
		_start = Bounds(fence);
		_origin = point;
		_edges = edges;
		_grid = fence.IsCollapsed ? (Array.Empty<int>(), Array.Empty<int>()) : fence.GridSizes(surface.Scale, surface.WorkArea);
		Changed?.Invoke();
		return true;
	}

	internal void Update(Point point)
	{
		if (_active is not { } fence) { return; }
		var delta = new Point(point.X - _origin.X, point.Y - _origin.Y);
		if (!_moved && _edges == FenceEdges.None && Math.Abs(delta.X) <= 4 * surface.Scale && Math.Abs(delta.Y) <= 4 * surface.Scale) { return; }
		_moved = true;
		Rectangle bounds;
		if (_edges == FenceEdges.None)
		{
			var visible = VisibleAt(fence, _start);
			var moved = FenceGeometry.Move(visible, delta, surface.WorkArea, Others(fence), settings, surface.Scale);
			bounds = new Rectangle(_start.X + moved.X - visible.X, _start.Y + moved.Y - visible.Y, _start.Width, _start.Height);
		}
		else
		{
			var minimum = new Size((int)Math.Ceiling(120 * surface.Scale), (int)Math.Ceiling(36 * surface.Scale));
			bounds = FenceGeometry.Resize(_start, delta, _edges, surface.WorkArea, fence.IsCollapsed ? [] : Others(fence),
				settings, surface.Scale, minimum, _grid.Widths, _grid.Heights);
		}
		Apply(fence, bounds);
		SetMergeTarget(_edges == FenceEdges.None ? surface.Fences.FirstOrDefault(other => other != fence && !other.Frame.Active.Locked && !other.IsEditing
			&& FenceTabs.HeaderBounds(surface, other).Contains(point)) : null);
	}

	internal async Task EndAsync(bool canceled)
	{
		if (_active is not { } fence) { return; }
		if (canceled) { Cancel(); return; }
		int version = ++_version;
		var merge = _mergeTarget;
		SetMergeTarget(null);
		if (merge != null && _moved && _edges == FenceEdges.None)
		{
			_active = null;
			if (MergeRequested?.Invoke(fence, merge) == true) { Changed?.Invoke(); return; }
			_active = fence;
		}
		var bounds = Bounds(fence);
		var target = bounds;
		if (bounds != _start)
		{
			var visible = VisibleAt(fence, bounds);
			var spot = FenceGeometry.FindFreeSpot(visible, surface.WorkArea, Others(fence), (int)Math.Round(settings.SnapGap * surface.Scale));
			target = spot is { } free ? new Rectangle(bounds.X + free.X - visible.X, bounds.Y + free.Y - visible.Y, bounds.Width, bounds.Height) : _start;
		}
		if (target != bounds)
		{
			var clock = Stopwatch.StartNew();
			while (true)
			{
				double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 180), k = 1 - Math.Pow(1 - t, 3);
				int Lerp(int from, int to) => from + (int)Math.Round((to - from) * k);
				Apply(fence, new Rectangle(Lerp(bounds.X, target.X), Lerp(bounds.Y, target.Y), Lerp(bounds.Width, target.Width), Lerp(bounds.Height, target.Height)));
				if (t >= 1) { break; }
				await Task.Delay(16);
				if (version != _version) { return; }
			}
		}
		_active = null;
		fence.CommitPosition();
		Changed?.Invoke();
	}

	internal void Cancel()
	{
		if (_active is not { } fence) { return; }
		_version++;
		_active = null;
		SetMergeTarget(null);
		Apply(fence, _start);
		fence.CancelPlacementPointer();
		Changed?.Invoke();
	}

	void SetMergeTarget(FenceView? target)
	{
		if (_mergeTarget == target) { return; }
		_mergeTarget?.SetMergeTarget(false);
		_mergeTarget = target;
		target?.SetMergeTarget(true);
	}

	Rectangle Bounds(FenceView fence)
	{
		var visible = surface.ExpandedBoundsOf(fence);
		return new Rectangle(visible.Left + surface.WorkArea.Left, visible.Top + surface.WorkArea.Top,
			(int)Math.Round(fence.Width * surface.Scale), (int)Math.Round(fence.Height * surface.Scale));
	}

	Rectangle VisibleAt(FenceView fence, Rectangle expanded)
	{
		if (fence.Frame.Active.RolledUp) { return FenceRoll.Collapsed(expanded, fence.Frame.Active.RollEdge, (int)Math.Round(36 * surface.Scale)); }
		var visible = fence.RevealedBounds;
		return new Rectangle(expanded.X + (int)Math.Round(visible.X * surface.Scale), expanded.Y + (int)Math.Round(visible.Y * surface.Scale),
			(int)Math.Round(visible.Width * surface.Scale), (int)Math.Round(visible.Height * surface.Scale));
	}

	Rectangle[] Others(FenceView fence) => surface.Fences.Where(other => other != fence).Select(other =>
	{
		var rect = surface.LayoutBoundsOf(other);
		return new Rectangle(rect.Left + surface.WorkArea.Left, rect.Top + surface.WorkArea.Top, rect.Width, rect.Height);
	}).ToArray();

	void Apply(FenceView fence, Rectangle bounds)
	{
		if (bounds.Width == (int)Math.Round(fence.Width * surface.Scale) && bounds.Height == (int)Math.Round(fence.Height * surface.Scale))
		{
			fence.TranslateTo((bounds.Left - surface.WorkArea.Left) / surface.Scale - Canvas.GetLeft(fence),
				(bounds.Top - surface.WorkArea.Top) / surface.Scale - Canvas.GetTop(fence));
			return;
		}
		fence.ArrangeAt((bounds.Left - surface.WorkArea.Left) / surface.Scale, (bounds.Top - surface.WorkArea.Top) / surface.Scale,
			bounds.Width / surface.Scale, bounds.Height / surface.Scale);
	}
}
