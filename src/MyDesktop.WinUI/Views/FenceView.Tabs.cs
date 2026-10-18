using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Native;
using Windows.Foundation;
using DrawPoint = System.Drawing.Point;

namespace MyDesktop.WinUI.Views;

public sealed partial class FenceView
{
	DispatcherQueueTimer _tabHoverTimer = null!;
	Guid? _hoveredTab;
	uint? _tabPointer;
	internal Func<bool>? TabSwitchAllowed { get; set; }
	internal event Action<Guid, int>? TabMoveRequested;
	internal event Action<Guid>? TabDetachRequested;
	internal event Func<Guid, DrawPoint, bool>? TabDragStarted;
	internal event Action<DrawPoint>? TabDragMoved;
	internal event Action<bool>? TabDragFinished;
	internal bool IsMergeTarget { get; private set; }

	void InitializeTabs()
	{
		_tabHoverTimer = DispatcherQueue.CreateTimer();
		_tabHoverTimer.IsRepeating = false;
		_tabHoverTimer.Tick += (_, _) =>
		{
			var id = _hoveredTab;
			StopTabHover();
			if (id is { } tab) { ActivateTab(tab); }
		};
		Header.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
		{
			HoverTab(args.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? null : TabAt(args.OriginalSource as DependencyObject));
		}), true);
		Header.PointerExited += (_, _) => StopTabHover();
		Header.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
		{
			if (_frame.Members.Count < 2 || TabSwitchAllowed?.Invoke() == false) { return; }
			int delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
			if (delta == 0) { return; }
			StepTab(delta > 0 ? -1 : 1);
			args.Handled = true;
		}), true);
		Header.RightTapped += (_, args) =>
		{
			if (IsEditing || TabSwitchAllowed?.Invoke() == false) { return; }
			StopTabHover();
			if (TabAt(args.OriginalSource as DependencyObject) is { } id) { ActivateTab(id); }
			TargetRequested?.Invoke();
			CreateTitleMenu().ShowAt(Header, args.GetPosition(Header));
			args.Handled = true;
		};
		PointerMoved += (_, args) =>
		{
			if (_tabPointer != args.Pointer.PointerId) { return; }
			TabDragMoved?.Invoke(ScreenPoint(args));
			args.Handled = true;
		};
		PointerReleased += (_, args) =>
		{
			if (_tabPointer != args.Pointer.PointerId) { return; }
			TabDragMoved?.Invoke(ScreenPoint(args));
			FinishTabPointer(false);
			args.Handled = true;
		};
		PointerCaptureLost += (_, args) => { if (_tabPointer == args.Pointer.PointerId) { FinishTabPointer(true); } };
	}

	void RefreshTabs()
	{
		var existing = Tabs.Children.OfType<ToggleButton>().ToDictionary(tab => (Guid)tab.Tag);
		var controls = _frame.Members.Select(member =>
		{
			if (!existing.TryGetValue(member.Id, out var tab))
			{
				var id = member.Id;
				tab = new ToggleButton { Tag = id, AllowFocusOnInteraction = false, IsTabStop = false };
				tab.Click += (_, _) => { ActivateTab(id); RefreshTabs(); };
			}
			tab.Content = member.Title;
			tab.IsChecked = member.Id == _frame.Active.Id;
			return tab;
		}).ToArray();
		if (!Tabs.Children.SequenceEqual(controls))
		{
			Tabs.Children.Clear();
			foreach (var tab in controls) { Tabs.Children.Add(tab); }
		}
	}

	internal void HoverTab(Guid? id)
	{
		if (!_settings.SwitchTabOnHover || IsEditing || TabSwitchAllowed?.Invoke() == false || id == _frame.Active.Id) { id = null; }
		if (_hoveredTab == id) { return; }
		StopTabHover();
		_hoveredTab = id;
		if (id == null) { return; }
		if (_settings.TabHoverDelay == 0) { ActivateTab(id.Value); return; }
		_tabHoverTimer.Interval = TimeSpan.FromMilliseconds(_settings.TabHoverDelay);
		_tabHoverTimer.Start();
	}

	internal void StopTabHover()
	{
		_tabHoverTimer.Stop();
		_hoveredTab = null;
	}

	internal void StepTab(int direction)
	{
		StopTabHover();
		int index = _frame.Members.ToList().FindIndex(member => member.Id == _frame.Active.Id) + direction;
		if (index >= 0 && index < _frame.Members.Count) { ActivateTab(_frame.Members[index].Id); }
	}

	internal MenuFlyout CreateTitleMenu()
	{
		bool editable = !_readOnly && TabSwitchAllowed?.Invoke() != false;
		var menu = new MenuFlyout();
		HoldExpansion(menu);
		var fold = new MenuFlyoutItem { Text = _frame.Active.RolledUp ? "展开分区" : "收起分区", IsEnabled = editable };
		fold.Click += (_, _) => SetRolledUp(!_frame.Active.RolledUp);
		menu.Items.Add(fold);
		var directions = new MenuFlyoutSubItem { Text = "收起方向", IsEnabled = editable };
		AddDirection(null, "自动（按贴着的屏幕边）");
		AddDirection(MyDesktop.Models.RollEdge.Top, "向上");
		AddDirection(MyDesktop.Models.RollEdge.Bottom, "向下");
		AddDirection(MyDesktop.Models.RollEdge.Left, "向左");
		AddDirection(MyDesktop.Models.RollEdge.Right, "向右");
		menu.Items.Add(directions);
		var rename = new MenuFlyoutItem { Text = "重命名分区", IsEnabled = editable };
		rename.Click += (_, _) => BeginTitleEdit();
		menu.Items.Add(rename);
		var refresh = new MenuFlyoutItem { Text = "刷新" };
		refresh.Click += (_, _) => RefreshContent();
		menu.Items.Add(refresh);
		if (_frame.Members.Count > 1)
		{
			int index = _frame.Members.ToList().FindIndex(member => member.Id == _frame.Active.Id);
			Guid id = _frame.Active.Id;
			var left = new MenuFlyoutItem { Text = "标签左移", IsEnabled = editable && !_frame.Active.Locked && index > 0 };
			left.Click += (_, _) => TabMoveRequested?.Invoke(id, index - 1);
			var right = new MenuFlyoutItem { Text = "标签右移", IsEnabled = editable && !_frame.Active.Locked && index < _frame.Members.Count - 1 };
			right.Click += (_, _) => TabMoveRequested?.Invoke(id, index + 1);
			var detach = new MenuFlyoutItem { Text = "拆出标签", IsEnabled = editable && !_frame.Active.Locked };
			detach.Click += (_, _) => TabDetachRequested?.Invoke(id);
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(left);
			menu.Items.Add(right);
			menu.Items.Add(detach);
		}
		menu.Items.Add(new MenuFlyoutSeparator());
		var exit = new MenuFlyoutItem { Text = "退出 MyDesktop" };
		exit.Click += (_, _) => ExitRequested?.Invoke();
		menu.Items.Add(exit);
		return menu;

		void AddDirection(MyDesktop.Models.RollEdge? direction, string text)
		{
			var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = _frame.Active.RollDirection == direction };
			item.Click += (_, _) => SetRollDirection(direction);
			directions.Items.Add(item);
		}
	}

	void BeginTabPointer(PointerRoutedEventArgs args, Guid id)
	{
		if (_readOnly || _tabPointer != null || !CapturePointer(args.Pointer)) { return; }
		if (TabDragStarted?.Invoke(id, ScreenPoint(args)) != true) { ReleasePointerCapture(args.Pointer); return; }
		_tabPointer = args.Pointer.PointerId;
		TargetRequested?.Invoke();
		args.Handled = true;
	}

	void FinishTabPointer(bool canceled)
	{
		if (_tabPointer == null) { return; }
		_tabPointer = null;
		ReleasePointerCaptures();
		TabDragFinished?.Invoke(canceled);
	}

	internal void CancelTabPointer() => FinishTabPointer(true);

	internal int TabIndexAt(DrawPoint point)
	{
		UpdateLayout();
		var window = NativeMethods.GetWindowRect(_owner);
		double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
		double along = IsVerticalRoll ? (point.Y - window.Top) / scale - Canvas.GetTop(this) - _visual.TransformMatrix.Translation.Y
			: (point.X - window.Left) / scale - Canvas.GetLeft(this) - _visual.TransformMatrix.Translation.X;
		for (int index = 0; index < Tabs.Children.Count; index++)
		{
			if (Tabs.Children[index] is not FrameworkElement { ActualWidth: > 0 } tab) { return -1; }
			var bounds = tab.TransformToVisual(this).TransformBounds(new Rect(0, 0, tab.ActualWidth, tab.ActualHeight));
			if (along < (IsVerticalRoll ? bounds.Bottom : bounds.Right)) { return index; }
		}
		return Tabs.Children.Count - 1;
	}

	internal void SetMergeTarget(bool enabled)
	{
		IsMergeTarget = enabled;
		Tint.BorderThickness = new Thickness(enabled ? 2 : 0);
		Tint.BorderBrush = enabled ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 180, 255)) : null;
	}

	static Guid? TabAt(DependencyObject? source)
	{
		for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
		{
			if (element is ToggleButton { Tag: Guid id }) { return id; }
		}
		return null;
	}
}
