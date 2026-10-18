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
		target.SetTemporaryExpanded(source.IsTemporarilyExpanded);
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
		var visible = model.RolledUp ? FenceRoll.Collapsed(candidate, model.RollEdge, (int)Math.Round(36 * surface.Scale)) : candidate;
		var others = surface.Fences.Select(fence => HeaderAndContentBounds(surface, fence)).ToArray();
		var free = FenceGeometry.FindFreeSpot(visible, surface.WorkArea, others, (int)Math.Round(session.Settings.SnapGap * surface.Scale));
		var location = free is { } spot ? new Point(candidate.X + spot.X - visible.X, candidate.Y + spot.Y - visible.Y) : candidate.Location;
		var detached = frames.Single(frame => frame.Members.Any(member => member.Id == id));
		detached.SetBounds(location.X, location.Y, model.Width, model.Height, (int)Math.Round(surface.Scale * 96));
		var view = surface.AddFence(detached);
		view.SetTemporaryExpanded(source.IsTemporarilyExpanded);
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
		int padding = Math.Min(header.Width, header.Height);
		header.Inflate(padding, padding);
		IsDetaching = !header.Contains(point);
		if (IsDetaching)
		{
			var preview = DetachedBounds(surface, fence, point);
			if (fence.Frame.Active.RolledUp) { preview = FenceRoll.Collapsed(preview, fence.Frame.Active.RollEdge, (int)Math.Round(36 * surface.Scale)); }
			surface.DrawBand(preview, fence);
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
		var local = surface.ExpandedBoundsOf(source);
		var bounds = new Rectangle(local.Left + surface.WorkArea.Left, local.Top + surface.WorkArea.Top, local.Width, local.Height);
		var work = surface.WorkArea;
		var header = source.HeaderBounds;
		int left = point?.X - (int)Math.Round((header.X + header.Width / 2) * surface.Scale) ?? bounds.Right + (int)Math.Round(session.Settings.SnapGap * surface.Scale);
		int top = point?.Y - (int)Math.Round((header.Y + header.Height / 2) * surface.Scale) ?? bounds.Top;
		return new Rectangle(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
			Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), bounds.Width, bounds.Height);
	}

	internal static Rectangle HeaderBounds(DesktopSurface surface, FenceView fence)
	{
		var bounds = surface.ExpandedBoundsOf(fence);
		var header = fence.HeaderBounds;
		return new Rectangle(bounds.Left + surface.WorkArea.Left + (int)Math.Round(header.X * surface.Scale),
			bounds.Top + surface.WorkArea.Top + (int)Math.Round(header.Y * surface.Scale),
			(int)Math.Round(header.Width * surface.Scale), (int)Math.Round(header.Height * surface.Scale));
	}

	static Rectangle HeaderAndContentBounds(DesktopSurface surface, FenceView fence)
	{
		var bounds = surface.LayoutBoundsOf(fence);
		return new Rectangle(bounds.Left + surface.WorkArea.Left, bounds.Top + surface.WorkArea.Top, bounds.Width, bounds.Height);
	}
}
