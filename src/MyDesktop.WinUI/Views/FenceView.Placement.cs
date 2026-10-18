using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Services;
using Windows.Foundation;
using DrawPoint = System.Drawing.Point;
using DrawRectangle = System.Drawing.Rectangle;

namespace MyDesktop.WinUI.Views;

public sealed partial class FenceView
{
	static readonly InputCursor HorizontalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
	static readonly InputCursor VerticalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
	static readonly InputCursor DiagonalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
	static readonly InputCursor ReverseDiagonalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
	uint? _placementPointer;
	Guid? _placementTab;
	DrawPoint _placementOrigin;
	internal event Func<DrawPoint, FenceEdges, bool>? PlacementStarted;
	internal event Action<DrawPoint>? PlacementMoved;
	internal event Action<bool>? PlacementFinished;

	internal FenceEdges ResizeEdgesAt(Point point)
	{
		var visible = RevealedBounds;
		if (_readOnly || IsEditing || _frame.Active.Locked || !visible.Contains(point)) { return FenceEdges.None; }
		var edges = FenceEdges.None;
		if (!IsCollapsed || !IsVerticalRoll)
		{
			if (point.X < visible.Left + 5) { edges |= FenceEdges.Left; }
			if (point.X >= visible.Right - 5) { edges |= FenceEdges.Right; }
		}
		if (!IsCollapsed || IsVerticalRoll)
		{
			if (point.Y < visible.Top + 5 && Canvas.GetTop(this) + _visual.TransformMatrix.Translation.Y > 0) { edges |= FenceEdges.Top; }
			if (point.Y >= visible.Bottom - 5) { edges |= FenceEdges.Bottom; }
		}
		return edges;
	}

	bool TryStartResize(PointerRoutedEventArgs args)
	{
		if (_placementPointer != null || _tabPointer != null || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return false; }
		var edges = ResizeEdgesAt(args.GetCurrentPoint(this).Position);
		return edges != FenceEdges.None && BeginPlacementPointer(args, edges);
	}

	bool BeginPlacementPointer(PointerRoutedEventArgs args, FenceEdges edges, Guid? tab = null)
	{
		if (_readOnly || _placementPointer != null || !CapturePointer(args.Pointer)) { return false; }
		if (PlacementStarted?.Invoke(ScreenPoint(args), edges) != true) { ReleasePointerCapture(args.Pointer); return false; }
		_placementPointer = args.Pointer.PointerId;
		_placementTab = tab;
		_placementOrigin = ScreenPoint(args);
		TargetRequested?.Invoke();
		args.Handled = true;
		return true;
	}

	void Placement_PointerMoved(object sender, PointerRoutedEventArgs args)
	{
		if (_placementPointer == args.Pointer.PointerId)
		{
			UpdatePlacementPointer(args);
			args.Handled = true;
			return;
		}
		var edges = ResizeEdgesAt(args.GetCurrentPoint(this).Position);
		bool horizontal = (edges & (FenceEdges.Left | FenceEdges.Right)) != 0, vertical = (edges & (FenceEdges.Top | FenceEdges.Bottom)) != 0;
		ProtectedCursor = horizontal && vertical ? edges.HasFlag(FenceEdges.Left) == edges.HasFlag(FenceEdges.Top) ? DiagonalCursor : ReverseDiagonalCursor
			: horizontal ? HorizontalCursor : vertical ? VerticalCursor : null;
	}

	void UpdatePlacementPointer(PointerRoutedEventArgs args)
	{
		var point = ScreenPoint(args);
		double threshold = 4 * MyDesktop.Native.NativeMethods.GetDpiForWindow(_owner) / 96.0;
		if (Math.Abs(point.X - _placementOrigin.X) > threshold || Math.Abs(point.Y - _placementOrigin.Y) > threshold) { _placementTab = null; }
		PlacementMoved?.Invoke(point);
	}

	void FinishPlacementPointer(bool canceled)
	{
		if (_placementPointer == null) { return; }
		var tab = _placementTab;
		_placementTab = null;
		_placementPointer = null;
		ReleasePointerCaptures();
		ProtectedCursor = null;
		PlacementFinished?.Invoke(canceled);
		if (!canceled && tab is { } id) { ActivateTab(id); }
	}

	internal void CancelPlacementPointer() => FinishPlacementPointer(true);

	internal (IReadOnlyList<int> Widths, IReadOnlyList<int> Heights) GridSizes(double scale, DrawRectangle work)
	{
		UpdateLayout();
		var containers = _items.Select(item => Items.ContainerFromItem(item)).OfType<GridViewItem>()
			.Where(item => item.ActualWidth > 0 && item.ActualHeight > 0).ToArray();
		var scroller = FindScroller(Items);
		if (containers.Length == 0 || scroller == null || Items.ItemsPanelRoot is not { } panel) { return (Array.Empty<int>(), Array.Empty<int>()); }
		double chromeWidth = Width - scroller.ViewportWidth, chromeHeight = Height - scroller.ViewportHeight;
		var slots = containers.Select(item =>
		{
			var bounds = item.TransformToVisual(panel).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
			var margin = item.Margin;
			return new Rect(bounds.X - margin.Left, bounds.Y - margin.Top, bounds.Width + margin.Left + margin.Right, bounds.Height + margin.Top + margin.Bottom);
		}).ToArray();
		var bottoms = slots.GroupBy(slot => Math.Round(slot.Top)).OrderBy(group => group.Key).Select(group => group.Max(slot => slot.Bottom)).ToList();
		double rowHeight = bottoms[0], columnWidth = slots[0].Width;
		if (rowHeight <= 0 || columnWidth <= 0) { throw new InvalidOperationException("图标实际布局尺寸无效，无法计算整行整列吸附。"); }
		for (double bottom = bottoms[^1] + rowHeight; (chromeHeight + bottom) * scale <= work.Height; bottom += rowHeight) { bottoms.Add(bottom); }
		int Pixels(double value) => (int)Math.Ceiling(value * scale - 0.01);
		var widths = new List<int>();
		if (_frame.Active.View == MyDesktop.Models.FenceView.Icons)
		{
			for (int count = 1; (chromeWidth + count * columnWidth) * scale <= work.Width; count++) { widths.Add(Pixels(chromeWidth + count * columnWidth)); }
		}
		return (widths, bottoms.Select(bottom => Pixels(chromeHeight + bottom)).ToArray());
	}

	static ScrollViewer? FindScroller(DependencyObject element)
	{
		if (element is ScrollViewer viewer) { return viewer; }
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
		{
			if (FindScroller(VisualTreeHelper.GetChild(element, index)) is { } child) { return child; }
		}
		return null;
	}
}
