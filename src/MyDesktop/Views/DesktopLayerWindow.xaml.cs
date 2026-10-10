using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 散放图标层：一个显示器上没有放进分区的桌面图标，位置、大小和间距都取自资源管理器。
/// 窗口左上角固定在工作区左上角，只盖住图标所在的范围（见 ExtentSize）；图标之外完全透明，点击穿透到系统桌面，
/// 系统的桌面右键菜单（查看、排序方式等）照常可用。本程序发起拖动期间临时扩成整个工作区并改为可接收放置，防止落到资源管理器隐藏的图标视图上。
/// </summary>
internal partial class DesktopLayerWindow : Window
{
	const double FadeMilliseconds = 180;

	/// <summary>
	/// 窗口在图标所占范围的右边、下边多留的边距（DIP），容得下名称的文字阴影。
	/// </summary>
	const double EdgeMargin = 8;

	/// <summary>
	/// 窗口在最下面一行格子之下多留的行数：选中时展开的完整名称、改名框不超出这几行时窗口不用跟着变。
	/// </summary>
	const double SpareRows = 2;

	static readonly SolidColorBrush CaptureBackground = Frozen(Color.FromArgb(1, 0, 0, 0));

	readonly DesktopTakeover _takeover;
	readonly ObservableCollection<FenceItem> _items = [];
	readonly DropPreview _dropPreview = new();
	readonly ItemDropForwarder _forwarder = new();
	IntPtr _hwnd;
	RECT _workArea;
	double _scale = 1;
	int _iconPixels;
	bool _allowClose;

	// 窗口范围：格子大小（DIP）用来估计还没排好版的图标；拖动期间扩成整个工作区
	double _cellWidth;
	double _cellHeight;
	bool _capturing;
	bool _extentPending;

	// 鼠标交互
	Point _pressPoint;
	FenceItem? _pressedItem;
	bool _deferSelection;
	HashSet<FenceItem> _bandBase = [];
	readonly ClickToRename _clickRename;
	// 展开了完整名称的图标（选中的里面最后选中的那个）
	FenceItem? _expandedName;

	// 拖入
	int _dragVersion;
	bool _dragInside;

	public DesktopLayerWindow(DesktopTakeover takeover, IntPtr monitor)
	{
		_takeover = takeover;
		Monitor = monitor;
		InitializeComponent();
		ItemsList.ItemsSource = _items;
		// 桌面图标层上点图标时前台可能仍是资源管理器的桌面
		_clickRename = new ClickToRename(BeginRename, item => (IsActive || DesktopHost.IsDesktopSurface(GetForegroundWindow()))
				&& ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem == item);
	}

	public IntPtr Monitor { get; }

	public IntPtr Handle => _hwnd;

	/// <summary>
	/// 所在显示器的工作区（屏幕物理像素）：窗口左上角固定在这里，图标的坐标以它为原点。
	/// </summary>
	public RECT WorkArea => _workArea;

	public IReadOnlyList<FenceItem> Items => _items;

	public List<FenceItem> SelectedItems => ItemsList.SelectedItems.Cast<FenceItem>().ToList();

	public FenceItem? FindItem(string key) => _items.FirstOrDefault(i => string.Equals(i.FullPath, key, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 图标图像在屏幕上的位置（物理像素）与图像，一键整理动画的起点；本层没有这个图标、图标还没加载或没显示时返回 null。
	/// </summary>
	public (RECT Rect, ImageSource Icon)? GetIcon(string key)
	{
		return FindItem(key)?.Icon is { } icon && GetIconRect(key) is RECT rect ? (rect, icon) : null;
	}

	/// <summary>
	/// 图标图像在屏幕上的位置（物理像素），图标还没加载时也有；删除分区动画的终点。本层没有这个图标或没显示时返回 null。
	/// </summary>
	public RECT? GetIconRect(string key)
	{
		if (!IsVisible || FindItem(key) is not { } item
				|| ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container
				|| ItemOps.FindChild<Image>(container, "IconImage") is not { IsVisible: true } image)
		{
			return null;
		}
		return ItemOps.ScreenRect(image);
	}

	#region 生命周期与位置

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
		// 与分区相同：去掉 WPF 的隐藏所有者，用 WS_EX_TOOLWINDOW 隐藏任务栏按钮和 Alt+Tab
		SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
		long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
		SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
	}

	/// <summary>
	/// 摆到显示器工作区（物理像素）的左上角，大小按图标所在的范围（见 ExtentSize）。
	/// </summary>
	public void Place(RECT workArea, double scale)
	{
		_workArea = workArea;
		_scale = scale;
		new WindowInteropHelper(this).EnsureHandle();
		var (width, height) = ExtentSize();
		// 首次定位若跨越不同 DPI 的显示器，WPF 会按新 DPI 缩放窗口，第二次把尺寸校正回物理像素
		SetWindowPos(_hwnd, IntPtr.Zero, workArea.Left, workArea.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
		SetWindowPos(_hwnd, IntPtr.Zero, workArea.Left, workArea.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
	}

	public void ShowOnDesktop()
	{
		BeginAnimation(OpacityProperty, null);
		Opacity = 1;
		Show();
		PlaceAboveDesktop();
	}

	public void PlaceAboveDesktop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			DesktopHost.PlaceAboveDesktop(_hwnd);
		}
	}

	public void FadeOut()
	{
		var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMilliseconds));
		animation.Completed += (_, _) =>
		{
			// 淡出过程中可能又被要求显示
			if (!_takeover.LayersVisible)
			{
				Hide();
			}
		};
		BeginAnimation(OpacityProperty, animation);
	}

	public void FadeIn()
	{
		if (!IsVisible)
		{
			BeginAnimation(OpacityProperty, null);
			Opacity = 0;
			Show();
			PlaceAboveDesktop();
		}
		BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeMilliseconds)));
	}

	/// <summary>
	/// 显示器缩放比例变化后按新的比例重新覆盖工作区、排布图标。
	/// </summary>
	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		Dispatcher.InvokeAsync(_takeover.OnDpiChanged, DispatcherPriority.Background);
	}

	public void CloseForReal()
	{
		_allowClose = true;
		Close();
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		// Alt+F4 不能关掉图标层
		if (!_allowClose)
		{
			e.Cancel = true;
		}
		base.OnClosing(e);
	}

	/// <summary>
	/// 本程序发起拖动期间整层几乎透明而非完全透明，放置落在本层而不是资源管理器隐藏的图标视图上；
	/// 这期间窗口临时扩成整个工作区，空白处也能接住放置，结束后缩回图标所在的范围。
	/// </summary>
	public void SetCaptureDrops(bool capture)
	{
		Background = capture ? CaptureBackground : Brushes.Transparent;
		_capturing = capture;
		UpdateExtent();
	}

	IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (ShellContextMenu.TryHandleMenuMessage(msg, wParam, lParam, out var menuResult))
		{
			handled = true;
			return menuResult;
		}
		switch (msg)
		{
			case WM_WINDOWPOSCHANGING:
				DesktopHost.KeepAboveDesktop(hwnd, lParam);
				break;
			case WM_SYSCOMMAND:
				int command = (int)wParam & 0xFFF0;
				if (command == SC_MINIMIZE || command == SC_MAXIMIZE || command == SC_CLOSE)
				{
					handled = true;
				}
				break;
		}
		return IntPtr.Zero;
	}

	#endregion

	#region 图标

	/// <summary>
	/// 更新本层显示的图标。
	/// </summary>
	/// <param name="placed">图标及其格子左上角（屏幕物理像素）。</param>
	/// <param name="spacing">资源管理器的图标间距（物理像素）。</param>
	/// <param name="iconSize">资源管理器的图标大小（DIP）。</param>
	public void SetItems(IReadOnlyList<(DesktopEntry Entry, POINT Cell)> placed, POINT spacing, int iconSize)
	{
		double scale = _scale;
		double cellWidth = spacing.X / scale;
		_cellWidth = cellWidth;
		_cellHeight = spacing.Y / scale;
		Resources["IconSize"] = (double)iconSize;
		Resources["CellWidth"] = cellWidth;
		// 去掉选中框的边距、边框后留给名称的宽度，与资源管理器的折行位置一致
		Resources["LabelWidth"] = Math.Max(16, cellWidth - 4);
		_iconPixels = (int)Math.Round(iconSize * scale);
		var existing = new Dictionary<string, FenceItem>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in _items)
		{
			existing.TryAdd(item.FullPath, item);
		}
		var fresh = new List<FenceItem>(placed.Count);
		var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (entry, cell) in placed)
		{
			// 资源管理器的视图偶尔同时列出两个同名项目（比如替换文件的那一会儿），同一个条目放两次会让 SyncItems 移动越界，只取第一个
			if (!added.Add(entry.Key))
			{
				continue;
			}
			// 仅大小写不同的改名会命中旧条目，此时按新名称重建
			if (existing.TryGetValue(entry.Key, out var item) && string.Equals(item.FullPath, entry.Key, StringComparison.Ordinal))
			{
				if (item.Update(entry))
				{
					item.IconPixelSize = 0;
				}
			}
			else
			{
				item = FenceItem.Create(entry);
			}
			item.X = (cell.X - _workArea.Left) / scale;
			item.Y = (cell.Y - _workArea.Top) / scale;
			fresh.Add(item);
		}
		SyncItems(fresh);
		// 在新位置画出来之前就把窗口调到位，图标挪到原来的范围之外时不会被截掉一帧
		UpdateExtent();
		foreach (var item in fresh)
		{
			RequestIcon(item);
		}
	}

	/// <summary>
	/// 增量同步到界面集合，保留已有项的容器与选中状态。
	/// </summary>
	void SyncItems(List<FenceItem> target)
	{
		var keep = new HashSet<FenceItem>(target);
		for (int i = _items.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(_items[i]))
			{
				_items.RemoveAt(i);
			}
		}
		for (int i = 0; i < target.Count; i++)
		{
			var item = target[i];
			int index = i < _items.Count && _items[i] == item ? i : _items.IndexOf(item);
			if (index < 0)
			{
				_items.Insert(i, item);
			}
			else if (index != i)
			{
				_items.Move(index, i);
			}
		}
	}

	public void ReloadIcons()
	{
		foreach (var item in _items)
		{
			item.IconPixelSize = 0;
			RequestIcon(item);
		}
	}

	void RequestIcon(FenceItem item)
	{
		int pixels = _iconPixels;
		if (pixels <= 0 || item.IconPixelSize == pixels)
		{
			return;
		}
		item.IconPixelSize = pixels;
		ShellIconLoader.Request(item.FullPath, pixels, item.Modified, image =>
		{
			if (image != null && item.IconPixelSize == pixels)
			{
				item.Icon = image;
			}
		}, item.IconKey);
	}

	/// <summary>
	/// 各图标可点中范围（屏幕物理像素），供鼠标钩子区分「点在图标上」和「点在桌面空白处」。
	/// </summary>
	public List<RECT> GetIconRects()
	{
		var rects = new List<RECT>();
		if (!IsVisible)
		{
			return rects;
		}
		foreach (var item in _items)
		{
			if (ItemBounds(item) is Rect bounds)
			{
				rects.Add(ToScreen(bounds));
			}
		}
		return rects;
	}

	/// <summary>
	/// 图标选中框在本窗口中的范围（DIP）。
	/// </summary>
	Rect? ItemBounds(FenceItem item)
	{
		if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible)
		{
			return null;
		}
		FrameworkElement target = container.Template?.FindName("Bd", container) as Border ?? (FrameworkElement)container;
		return target.TransformToAncestor(this).TransformBounds(new Rect(target.RenderSize));
	}

	RECT ToScreen(Rect bounds)
	{
		return new RECT(
				_workArea.Left + (int)Math.Floor(bounds.Left * _scale),
				_workArea.Top + (int)Math.Floor(bounds.Top * _scale),
				_workArea.Left + (int)Math.Ceiling(bounds.Right * _scale),
				_workArea.Top + (int)Math.Ceiling(bounds.Bottom * _scale));
	}

	/// <summary>
	/// 图标格子左上角的屏幕位置（物理像素）。
	/// </summary>
	public POINT CellOrigin(FenceItem item)
	{
		return new POINT(_workArea.Left + (int)Math.Round(item.X * _scale), _workArea.Top + (int)Math.Round(item.Y * _scale));
	}

	public FenceItem? Find(string key) => _items.FirstOrDefault(i => string.Equals(i.FullPath, key, StringComparison.OrdinalIgnoreCase));

	#endregion

	#region 窗口范围

	/// <summary>
	/// 窗口应有的大小（物理像素）：盖住图标所在的范围再加余量，不超出工作区；本程序发起拖动期间是整个工作区。
	/// 透明窗口按整窗大小占内存（见 CLAUDE.md），铺满工作区的话只为角落里几个图标就要占几十 MB。
	/// 图标在窗口里的左上角就是它在画布上的 X、Y。最下面的格子之下留 SpareRows 行，展开的完整名称、改名框在这个范围内时窗口不用变；
	/// 超出时按已排好版的实际大小算。
	/// </summary>
	(int Width, int Height) ExtentSize()
	{
		if (_capturing)
		{
			return (Math.Max(1, _workArea.Width), Math.Max(1, _workArea.Height));
		}
		double right = 0;
		double cellBottom = 0;
		double renderedBottom = 0;
		foreach (var item in _items)
		{
			var rendered = ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container ? container.RenderSize : default;
			right = Math.Max(right, item.X + Math.Max(_cellWidth, rendered.Width));
			cellBottom = Math.Max(cellBottom, item.Y + _cellHeight);
			renderedBottom = Math.Max(renderedBottom, item.Y + rendered.Height);
		}
		double bottom = Math.Max(cellBottom + _cellHeight * SpareRows, renderedBottom);
		int width = (int)Math.Ceiling((right + EdgeMargin) * _scale);
		int height = (int)Math.Ceiling((bottom + EdgeMargin) * _scale);
		return (Math.Clamp(width, 1, Math.Max(1, _workArea.Width)), Math.Clamp(height, 1, Math.Max(1, _workArea.Height)));
	}

	/// <summary>
	/// 按 ExtentSize 调整窗口大小：左上角不动，只改宽高（同时挪左上角会闪，见 CLAUDE.md）。
	/// </summary>
	void UpdateExtent()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		var (width, height) = ExtentSize();
		var current = GetWindowRect(_hwnd);
		if (current.Width != width || current.Height != height)
		{
			SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		}
	}

	/// <summary>
	/// 图标占的范围变了（展开完整名称、改名框变高、图标大小变化）：在排版过程中触发，等这一轮排完再调整窗口。
	/// </summary>
	void ItemContainer_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (_extentPending)
		{
			return;
		}
		_extentPending = true;
		Dispatcher.InvokeAsync(() =>
		{
			_extentPending = false;
			UpdateExtent();
		}, DispatcherPriority.Loaded);
	}

	#endregion

	#region 选择

	public void ClearSelection()
	{
		CommitAllRenames();
		ItemsList.UnselectAll();
	}

	public void SelectAll() => ItemsList.SelectAll();

	/// <summary>
	/// 不是鼠标单击造成的选中（方向键、新建后选中等）同样是单选，取消分区里的选择；按着 Ctrl/Shift 时是追加。
	/// </summary>
	void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		var expanded = ItemOps.ExpandSelectedName(ItemsList, e, _expandedName, true);
		if (expanded != _expandedName)
		{
			_expandedName = expanded;
			// 展开、收起名称改变了图标占的范围，判断点没点在图标上要跟着更新
			_takeover.RefreshHitRects();
		}
		if (e.AddedItems.Count > 0 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
		{
			_takeover.OnLayerSelected();
		}
	}

	public void Select(FenceItem item)
	{
		ItemsList.UnselectAll();
		ItemsList.SelectedItem = item;
	}

	/// <summary>
	/// 方向键、首字母定位选中图标；extend 为 true 时追加到已有选择。
	/// </summary>
	public void SelectFromKeyboard(FenceItem item, bool extend)
	{
		CommitAllRenames();
		if (!extend)
		{
			ItemsList.UnselectAll();
		}
		if (!ItemsList.SelectedItems.Contains(item))
		{
			ItemsList.SelectedItems.Add(item);
		}
		if (IsKeyboardFocusWithin)
		{
			FocusItem(item);
		}
	}

	/// <summary>
	/// 开始框选：记下已有选择，追加模式下框选结果与之合并。
	/// </summary>
	public void BeginBand(bool additive)
	{
		CommitAllRenames();
		if (!additive)
		{
			ItemsList.UnselectAll();
		}
		_bandBase = SelectedItems.ToHashSet();
	}

	/// <summary>
	/// 框选范围变化（屏幕物理像素）：选中与之相交的图标。选框本身由 DesktopTakeover 画在单独的窗口里。
	/// </summary>
	public void UpdateBand(RECT rect)
	{
		var local = new Rect((rect.Left - _workArea.Left) / _scale, (rect.Top - _workArea.Top) / _scale, rect.Width / _scale, rect.Height / _scale);
		foreach (var item in _items)
		{
			if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || ItemBounds(item) is not Rect bounds)
			{
				continue;
			}
			bool selected = local.IntersectsWith(bounds) || _bandBase.Contains(item);
			if (container.IsSelected != selected)
			{
				container.IsSelected = selected;
			}
		}
	}

	public void EndBand()
	{
		_bandBase = [];
	}

	#endregion

	#region 鼠标

	void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (ItemOps.IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		CommitAllRenames();
		var item = ItemFromSource(e.OriginalSource);
		_clickRename.Press(e, item, _takeover.AllSelectedItems() is [var only] && only == item);
		if (item == null)
		{
			return;
		}
		// 与系统桌面一样，多个显示器上的图标和分区里的图标共用一个选择；按在已选中的图标上时先保留整组选择，抬起时再单选
		bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
		_takeover.OnLayerPressed(this, additive || ItemsList.SelectedItems.Contains(item), item);
		_pressPoint = e.GetPosition(this);
		_pressedItem = item;
		_deferSelection = false;
		if (e.ClickCount >= 2)
		{
			_takeover.Open(ItemsList.SelectedItems.Contains(item) ? _takeover.AllSelectedItems() : [item], _hwnd);
			_pressedItem = null;
			e.Handled = true;
			return;
		}
		if (ItemsList.SelectedItems.Contains(item) && Keyboard.Modifiers == ModifierKeys.None)
		{
			// 在已选中的项上按下时先不改选择，便于拖动整组图标；没有拖动的话抬起时再单选它
			_deferSelection = true;
			FocusItem(item);
			e.Handled = true;
		}
	}

	void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (_pressedItem == null)
		{
			return;
		}
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			_pressedItem = null;
			return;
		}
		// 屏蔽列表自带的"按住拖动扩选"，改为拖动图标
		e.Handled = true;
		var position = e.GetPosition(this);
		if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
				&& Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
		{
			return;
		}
		var item = _pressedItem;
		_pressedItem = null;
		_deferSelection = false;
		if (!ItemsList.SelectedItems.Contains(item))
		{
			ItemsList.UnselectAll();
			ItemsList.SelectedItem = item;
		}
		var (items, desktop) = _takeover.SelectionForShell();
		StartDragOut(items, desktop, item);
	}

	void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_deferSelection && _pressedItem != null)
		{
			_takeover.OnLayerPressed(this, false);
			ItemsList.UnselectAll();
			ItemsList.SelectedItem = _pressedItem;
		}
		// 没有拖动、抬起时还在按下的图标上：单击的是已选中图标的名称时，过一会儿开始改名
		_clickRename.Release(_pressedItem);
		_deferSelection = false;
		_pressedItem = null;
	}

	void ItemsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		_clickRename.Cancel();
		if (ItemOps.IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		CommitAllRenames();
		var item = ItemFromSource(e.OriginalSource);
		if (item == null)
		{
			return;
		}
		if (!ItemsList.SelectedItems.Contains(item))
		{
			_takeover.OnLayerPressed(this, false);
			ItemsList.UnselectAll();
			ItemsList.SelectedItem = item;
		}
		var (items, desktop) = _takeover.SelectionForShell();
		ShowItemMenu(items, desktop, NativeMethods.GetCursorPos(), item);
		e.Handled = true;
	}

	/// <param name="items">菜单作用的图标，可能包含分区里一起选中的。</param>
	/// <param name="desktop">它们是不是桌面上的图标。</param>
	/// <param name="renameTarget">选「重命名」时改名的图标，属于本层。</param>
	public void ShowItemMenu(List<FenceItem> items, bool desktop, POINT point, FenceItem renameTarget)
	{
		if (items.Count == 0)
		{
			return;
		}
		try
		{
			ItemOps.ShowContextMenu(_hwnd, items, desktop, point, [], verb =>
			{
				// Shell 自己不会处理重命名（需要视图配合），改为在图标层内联编辑
				if (!string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (renameTarget.CanRename)
				{
					BeginRename(renameTarget);
				}
				return true;
			});
		}
		catch (Exception ex)
		{
			Log.Warn("显示桌面图标右键菜单失败", ex);
		}
		_takeover.Items.RefreshSoon();
	}

	void StartDragOut(List<FenceItem> items, bool desktop, FenceItem anchor)
	{
		if (items.Count == 0)
		{
			return;
		}
		Mouse.Capture(null);
		var image = ItemOps.CreateDragImage(ItemsList.ItemContainerGenerator.ContainerFromItem(anchor) as ListBoxItem, items.Count, _scale);
		var press = PointToScreen(_pressPoint);
		_takeover.OnLayerDragStarting(new POINT((int)Math.Round(press.X), (int)Math.Round(press.Y)));
		ItemOps.DragOut(_hwnd, items, desktop, this, image);
		_takeover.Items.RefreshSoon();
	}

	FenceItem? ItemFromSource(object source)
	{
		if (source is not DependencyObject element)
		{
			return null;
		}
		return (ItemsControl.ContainerFromElement(ItemsList, element) as ListBoxItem)?.DataContext as FenceItem;
	}

	void FocusItem(FenceItem item)
	{
		if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
		{
			container.Focus();
		}
	}

	#endregion

	#region 重命名

	public void BeginRename(FenceItem item)
	{
		if (!item.CanRename)
		{
			return;
		}
		CommitAllRenames();
		if (!IsActive)
		{
			// 从资源管理器接手的改名、桌面在前台时按 F2 都发生在本程序不在前台的时候，普通的 Activate 会被系统拒绝
			if (!NativeMethods.ForceForegroundWindow(_hwnd))
			{
				Log.Warn("改名时没能切到前台，键盘输入可能不会进入改名框");
			}
			Activate();
		}
		_takeover.OnLayerPressed(this, false);
		ItemsList.UnselectAll();
		ItemsList.SelectedItem = item;
		item.RenameText = item.DisplayName;
		item.IsRenaming = true;
		Dispatcher.InvokeAsync(() =>
		{
			var box = ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container
					? ItemOps.FindChild<TextBox>(container, "RenameBox")
					: null;
			if (box == null)
			{
				item.IsRenaming = false;
				return;
			}
			box.Focus();
			// 与资源管理器一致：只选中扩展名之前的部分
			int dot = item.IsFolder ? -1 : item.DisplayName.LastIndexOf('.');
			if (dot > 0)
			{
				box.Select(0, dot);
			}
			else
			{
				box.SelectAll();
			}
		}, DispatcherPriority.Loaded);
	}

	public bool IsRenaming => _items.Any(i => i.IsRenaming);

	void RenameBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (sender is not TextBox { DataContext: FenceItem item })
		{
			return;
		}
		if (e.Key == Key.Enter)
		{
			CommitRename(item);
			FocusItem(item);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			item.IsRenaming = false;
			FocusItem(item);
			e.Handled = true;
		}
	}

	void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (sender is TextBox { DataContext: FenceItem item })
		{
			CommitRename(item);
		}
	}

	public void CommitAllRenames()
	{
		foreach (var item in _items.Where(i => i.IsRenaming).ToList())
		{
			CommitRename(item);
		}
	}

	void CommitRename(FenceItem item)
	{
		if (!item.IsRenaming)
		{
			return;
		}
		item.IsRenaming = false;
		if (ItemOps.Rename(_hwnd, item, item.RenameText) is string path)
		{
			_takeover.OnRenamed(item.FullPath, path);
		}
	}

	#endregion

	#region 键盘

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		base.OnPreviewKeyDown(e);
		_clickRename.Cancel();
		if (e.Handled || e.OriginalSource is TextBox)
		{
			return;
		}
		var modifiers = Keyboard.Modifiers;
		bool ctrl = (modifiers & ModifierKeys.Control) != 0;
		bool shift = (modifiers & ModifierKeys.Shift) != 0;
		bool alt = (modifiers & ModifierKeys.Alt) != 0;
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		DesktopKey? command = key switch
		{
			Key.Enter when alt => DesktopKey.Properties,
			Key.Enter => DesktopKey.Open,
			Key.Delete => shift ? DesktopKey.DeletePermanently : DesktopKey.Delete,
			Key.F2 => DesktopKey.Rename,
			Key.A when ctrl => DesktopKey.SelectAll,
			Key.C when ctrl => DesktopKey.Copy,
			Key.X when ctrl => DesktopKey.Cut,
			Key.D when ctrl => DesktopKey.Delete,
			Key.Apps => DesktopKey.ContextMenu,
			Key.F10 when shift => DesktopKey.ContextMenu,
			Key.Left when !ctrl => DesktopKey.Left,
			Key.Up when !ctrl => DesktopKey.Up,
			Key.Right when !ctrl => DesktopKey.Right,
			Key.Down when !ctrl => DesktopKey.Down,
			Key.Home when !ctrl => DesktopKey.Home,
			Key.End when !ctrl => DesktopKey.End,
			_ => null,
		};
		if (command is DesktopKey value)
		{
			_takeover.HandleKey(value, shift);
		}
		else if (key == Key.V && ctrl)
		{
			_takeover.Paste(_hwnd);
		}
		else if (key == Key.F5)
		{
			_takeover.Items.Refresh();
		}
		else if (key == Key.Escape)
		{
			ItemsList.UnselectAll();
		}
		else if (!(key == Key.F4 && alt))
		{
			// 吞掉 Alt+F4，其余按键交给列表（方向键移动选择等）
			return;
		}
		e.Handled = true;
	}

	/// <summary>
	/// 输入字母或数字时按名称定位图标（改名框里的输入不受影响）。
	/// </summary>
	protected override void OnPreviewTextInput(TextCompositionEventArgs e)
	{
		base.OnPreviewTextInput(e);
		if (e.Handled || e.OriginalSource is TextBox || e.Text.Length == 0 || !char.IsLetterOrDigit(e.Text[0]))
		{
			return;
		}
		_takeover.TypeAhead(e.Text);
		e.Handled = true;
	}

	#endregion

	#region 拖入

	/// <summary>
	/// 拖到图标上时交给图标自己处理（文件夹、程序、回收站等），否则按图标层的规则。
	/// </summary>
	DragDropEffects ComputeEffect(DragEventArgs e)
	{
		return _forwarder.Over(_hwnd, DropItemAt(e), true, e) ?? _takeover.GetLayerDropEffect(e);
	}

	/// <summary>
	/// 拖动经过的图标；正被拖动的图标自身不算。
	/// </summary>
	FenceItem? DropItemAt(DragEventArgs e)
	{
		var item = ItemFromSource(InputHitTest(e.GetPosition(this)));
		var dragged = DesktopDrag.Current?.Keys;
		return item != null && dragged != null && dragged.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase) ? null : item;
	}

	protected override void OnDragEnter(DragEventArgs e)
	{
		base.OnDragEnter(e);
		_dragVersion++;
		e.Effects = ComputeEffect(e);
		if (!_dragInside)
		{
			_dragInside = true;
			_dropPreview.Enter(_hwnd, e);
		}
		else
		{
			_dropPreview.Over(e);
		}
		e.Handled = true;
	}

	protected override void OnDragOver(DragEventArgs e)
	{
		base.OnDragOver(e);
		_dragVersion++;
		e.Effects = ComputeEffect(e);
		_dropPreview.Over(e);
		e.Handled = true;
	}

	protected override void OnDragLeave(DragEventArgs e)
	{
		base.OnDragLeave(e);
		// 在窗口内的子元素之间移动也会触发 DragLeave（紧接着就是 DragEnter），延迟判断是否真的离开了窗口
		int version = ++_dragVersion;
		Dispatcher.InvokeAsync(() =>
		{
			if (version == _dragVersion && _dragInside)
			{
				_dragInside = false;
				_forwarder.Leave();
				_dropPreview.Leave();
			}
		}, DispatcherPriority.Input);
		e.Handled = true;
	}

	protected override void OnDrop(DragEventArgs e)
	{
		base.OnDrop(e);
		_dragVersion++;
		_dragInside = false;
		e.Handled = true;
		if (_forwarder.Drop(e) is DragDropEffects done)
		{
			// 图标自己完成了放置（移进文件夹、用程序打开、删除到回收站）
			_dropPreview.Drop(e, done);
			e.Effects = done;
			_takeover.Items.RefreshSoon();
			return;
		}
		var effect = _takeover.GetLayerDropEffect(e);
		_dropPreview.Drop(e, effect);
		e.Effects = _takeover.DropOnLayer(e, effect, _hwnd);
	}

	#endregion

	static SolidColorBrush Frozen(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}
}
