using System.Drawing;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;

namespace MyDesktop.WinUI.Services;

internal sealed class FenceTabs(DesktopSession session)
{
	DesktopSurface? _surface;
	FenceView? _dragged;
	Guid _tab;
	Point _point;
	internal bool IsDragging => _dragged != null;
	internal bool IsDetaching { get; private set; }

	bool CanEdit(DesktopSurface surface, FenceView fence, bool sorting = false) => !session.IsPreview && surface.Fences.Contains(fence)
		&& !fence.Frame.Active.Locked && !fence.IsEditing && (!session.IsInteracting || sorting && _dragged == fence);

	internal bool Merge(DesktopSurface surface, FenceView source, FenceView target)
	{
		if (source == target || !CanEdit(surface, source) || !CanEdit(surface, target)) { return false; }
		var sourceIds = source.Frame.Members.Select(member => member.Id).ToArray();
		var group = FenceGroups.Merge(session.Settings, source.Frame.Active.Id, target.Frame.Active.Id);
		target.RebindFrame(new FenceWorkspace(session.Settings).Frames.Single(frame => frame.Members.Any(member => member.Id == group.Active)));
		foreach (var id in sourceIds.Where(id => id != group.Active)) { session.Selection.Remove(id); }
		surface.RemoveFence(source);
		session.LayoutChanged(surface, target);
		return true;
	}

	internal bool Move(DesktopSurface surface, FenceView fence, Guid id, int index)
	{
		if (!CanEdit(surface, fence, true) || !fence.Frame.Members.Any(member => member.Id == id)) { return false; }
		if (!FenceGroups.Move(session.Settings, id, index)) { return false; }
		fence.RebindFrame(new FenceWorkspace(session.Settings).Frames.Single(frame => frame.Members.Any(member => member.Id == id)));
		session.LayoutChanged(surface, fence);
		return true;
	}

	internal FenceView? Detach(DesktopSurface surface, FenceView source, Guid id, Point? point)
	{
		if (!CanEdit(surface, source) || source.Frame.Members.Count < 2 || !source.Frame.Members.Any(member => member.Id == id)) { return null; }
		var model = source.Frame.Members.Single(member => member.Id == id);
		var selection = session.Selection.InScope(id);
		Guid remaining = source.Frame.Members.First(member => member.Id != id).Id;
		FenceGroups.Detach(session.Settings, id);
		var frames = new FenceWorkspace(session.Settings).Frames;
		source.RebindFrame(frames.Single(frame => frame.Members.Any(member => member.Id == remaining)));
		var candidate = DetachedBounds(surface, source, point);
		var visible = new Rectangle(candidate.Location, new Size(candidate.Width, model.RolledUp ? (int)Math.Round(36 * surface.Scale) : candidate.Height));
		var others = surface.Fences.Select(fence => HeaderAndContentBounds(surface, fence)).ToArray();
		var free = FenceGeometry.FindFreeSpot(visible, surface.WorkArea, others, (int)Math.Round(session.Settings.SnapGap * surface.Scale));
		var location = free?.Location ?? candidate.Location;
		var detached = frames.Single(frame => frame.Members.Any(member => member.Id == id));
		detached.SetBounds(location.X, location.Y, model.Width, model.Height, (int)Math.Round(surface.Scale * 96));
		var view = surface.AddFence(detached);
		if (selection.Count > 0) { session.Selection.Set(id, selection, true); }
		session.LayoutChanged(surface, view);
		return view;
	}

	internal bool BeginDrag(DesktopSurface surface, FenceView fence, Guid id, Point point)
	{
		if (!CanEdit(surface, fence) || fence.Frame.Members.Count < 2 || !fence.Frame.Members.Any(member => member.Id == id)) { return false; }
		_surface = surface;
		_dragged = fence;
		_tab = id;
		_point = point;
		IsDetaching = false;
		foreach (var item in session.Surfaces.SelectMany(host => host.Fences)) { item.StopTabHover(); }
		surface.RaiseFence(fence);
		surface.SetInputCapture(true);
		session.UpdateInteraction();
		return true;
	}

	internal void UpdateDrag(Point point)
	{
		if (_dragged is not { } fence || _surface is not { } surface) { return; }
		_point = point;
		var header = HeaderBounds(surface, fence);
		header.Inflate(header.Height, header.Height);
		IsDetaching = !header.Contains(point);
		if (IsDetaching)
		{
			var preview = DetachedBounds(surface, fence, point);
			if (fence.Frame.Active.RolledUp) { preview.Height = (int)Math.Round(36 * surface.Scale); }
			surface.DrawBand(preview);
		}
		else
		{
			surface.DrawBand(null);
			int index = fence.TabIndexAt(point);
			if (index >= 0) { Move(surface, fence, _tab, index); }
		}
	}

	internal void EndDrag(bool canceled)
	{
		if (_dragged is not { } fence || _surface is not { } surface) { return; }
		bool detach = IsDetaching && !canceled;
		_dragged = null;
		_surface = null;
		IsDetaching = false;
		fence.CancelTabPointer();
		surface.DrawBand(null);
		surface.SetInputCapture(false);
		session.UpdateInteraction();
		if (detach) { Detach(surface, fence, _tab, _point); }
		session.FilesCompleted(false);
	}

	Rectangle DetachedBounds(DesktopSurface surface, FenceView source, Point? point)
	{
		var bounds = HeaderAndContentBounds(surface, source);
		bounds.Height = (int)Math.Round(source.Height * surface.Scale);
		var work = surface.WorkArea;
		int left = point?.X - bounds.Width / 2 ?? bounds.Right + (int)Math.Round(session.Settings.SnapGap * surface.Scale);
		int top = point?.Y - (int)Math.Round(18 * surface.Scale) ?? bounds.Top;
		return new Rectangle(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
			Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), bounds.Width, bounds.Height);
	}

	internal static Rectangle HeaderBounds(DesktopSurface surface, FenceView fence)
	{
		var bounds = HeaderAndContentBounds(surface, fence);
		bounds.Height = (int)Math.Round(36 * surface.Scale);
		return bounds;
	}

	static Rectangle HeaderAndContentBounds(DesktopSurface surface, FenceView fence)
	{
		var bounds = surface.BoundsOf(fence);
		return new Rectangle(bounds.Left + surface.WorkArea.Left, bounds.Top + surface.WorkArea.Top, bounds.Width, bounds.Height);
	}
}
