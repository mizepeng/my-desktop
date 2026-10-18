using System.Drawing;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Services;

internal sealed class DesktopGestures(DesktopSession session)
{
	SelectionBand? _band;
	FenceView? _fence;
	Point _origin;
	Rectangle? _clip;
	bool _moved;
	public bool IsActive => _band != null;

	internal void Begin(Point origin, bool additive, DesktopSurface surface, FenceView? fence = null)
	{
		if (session.IsPreview || session.Takeover?.IsActive != true || session.DragDrop.IsActive || session.IsMovingFence) { return; }
		Cancel();
		surface.ActivateForSelection();
		session.SetContext(surface, fence);
		_fence = fence;
		_origin = origin;
		_moved = false;
		_clip = fence == null ? null : surface.ContentBoundsOf(fence);
		var targets = fence == null ? session.Surfaces.SelectMany(item => item.SelectionTargets(null)).ToArray() : surface.SelectionTargets(fence);
		_band = new SelectionBand(session.Selection, origin, targets, additive);
		session.UpdateInteraction();
		_band.Update(origin);
	}

	internal void Move(Point point)
	{
		if (_band == null) { return; }
		if (!_moved && Math.Abs(point.X - _origin.X) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(point.Y - _origin.Y) < GetSystemMetrics(SM_CYDRAG)) { return; }
		_moved = true;
		_band.Update(point);
		Rectangle bounds = _clip is { } clip ? Rectangle.Intersect(_band.Bounds, clip) : _band.Bounds;
		foreach (var surface in session.Surfaces)
		{
			surface.SetInputCapture(_clip == null);
			surface.DrawBand(bounds, _fence);
		}
	}

	internal void End(Point point)
	{
		if (_band == null) { return; }
		Move(point);
		Finish();
	}

	internal void Cancel()
	{
		if (_band == null) { return; }
		_band.Cancel();
		Finish();
	}

	void Finish()
	{
		_band = null;
		_fence = null;
		session.UpdateInteraction();
		foreach (var surface in session.Surfaces.Where(item => !item.IsClosed))
		{
			surface.DrawBand(null);
			surface.SetInputCapture(false);
		}
		session.FilesCompleted(false);
	}
}
