using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 桌面上的一个分区窗口，挂在桌面层，支持拖放、右键菜单、卷起、吸附等。
/// 桌面分区只记录成员，展示桌面上被归入它的图标，文件始终留在桌面；映射分区展示任意一个文件夹的内容。
/// </summary>
internal partial class FenceWindow : Window
{
	const double TitleBarDip = 30;
	// 分区的边框粗细（Frame 的 BorderThickness）
	const double FrameBorderDip = 1;
	const double ResizeBorderDip = 5;
	const double SnapDistanceDip = 12;
	const double FadeMilliseconds = 180;
	// 毛玻璃在拖动开始时淡出、松手或换壁纸后淡入的时长
	const double BlurFadeOutMilliseconds = 100;
	const double BlurFadeInMilliseconds = 300;
	const double MinWidthDip = 120;
	// 离屏幕边缘这么近算贴着这条边，决定自动卷起方向
	const double DockDistanceDip = 24;
	// 松手时压住了别的分区，滑到空位的时长
	const double SlideMilliseconds = 180;

	static readonly DropShadowEffect TextShadowEffect = CreateShadow();
	// 调整大小时列数 × 行数提示：平时半透明黑底；两个方向都刚好装满时蓝底，只有一个方向刚好装满时那个数字用浅蓝色
	static readonly SolidColorBrush HintBackground = Frozen(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
	static readonly SolidColorBrush SnappedHintBackground = Frozen(Color.FromArgb(0xE6, 0x3B, 0x82, 0xF6));
	static readonly SolidColorBrush SnappedHintText = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
	static readonly int[] CustomColors = new int[16];

	readonly FenceManager _manager;
	readonly ObservableCollection<FenceItem> _items = [];
	// 一键整理时正从桌面飞来的图标（完整解析名），飞到之前先不显示
	readonly HashSet<string> _arriving = new(StringComparer.OrdinalIgnoreCase);
	readonly DispatcherTimer _refreshTimer;
	readonly DispatcherTimer _collapseTimer;
	readonly DispatcherTimer _buttonsTimer;
	IntPtr _hwnd;
	FileSystemWatcher? _watcher;
	ScrollViewer? _scroller;
	Brush? _frameBorder;
	// 毛玻璃背景的画刷，开着毛玻璃且取到了壁纸时才有
	ImageBrush? _blurBrush;
	// 拖动或调整大小期间毛玻璃暂时淡出
	bool _blurHidden;
	bool _folderExisted;
	// 映射分区里进入的子文件夹；为空时显示映射的文件夹本身
	string? _subFolder;
	bool _allowClose;
	bool _tempExpanded;
	// 详细信息视图里名称列至少留的宽度，后三列的宽度
	const double DetailsNameMinWidth = 120;
	const double DetailsDateWidth = 116;
	const double DetailsTypeWidth = 110;
	const double DetailsSizeWidth = 80;
	// 卷起、展开动画的裁剪区域，没在播时为 null
	RectangleGeometry? _rollClip;
	// 卷起、展开要挪窗口左上角时顶替显示的截图（见 MoveBehindStandIn）
	StandInWindow? _standIn;
	static readonly Duration RollDuration = TimeSpan.FromMilliseconds(180);
	bool _menuOpen;

	// 移动/缩放跟踪：按鼠标相对起点的绝对位移计算目标位置
	POINT _dragCursorStart;
	RECT _dragRectStart;
	RECT? _dragLastRect;
	bool _dragFinished;
	bool _inSizeMove;
	bool _heightResized;
	bool _widthResized;
	// 每次开始拖动加一，松手后的滑动发现它变了就停下
	int _slideVersion;
	// 拖动分区时鼠标所在的、松手就合并进去的分区标题栏
	FenceWindow? _mergeTarget;

	// 标签页：按住 Shift 拖动的标签、是否已拖出标题栏（松手就拆开）及拆开后位置的预览框
	FenceTab? _draggedTab;
	bool _tabDetaching;
	DrawFrameWindow? _detachPreview;
	// 拖着图标停留的别的标签，停够一会儿就切过去
	FenceWindow? _hoverTab;
	// 鼠标停留的别的标签（没拖东西），开了「悬停切换标签」时停够设定的时间就切过去
	FenceWindow? _mouseTab;
	readonly DispatcherTimer _mouseTabTimer;
	// 展开了完整名称的图标（选中的里面最后选中的那个）
	FenceItem? _expandedName;
	readonly DispatcherTimer _tabHoverTimer;

	// 标题栏当前摆在哪一侧，没变时不重排布局
	RollEdge? _titleEdge;

	// 列表鼠标交互
	Point _pressPoint;
	FenceItem? _pressedItem;
	bool _deferSelection;
	bool _rubberBand;
	// 框选已越过拖动阈值、正在显示选框
	bool _rubberShown;
	// 按住的是右键：松开时弹出右键菜单
	bool _rubberRight;
	DrawFrameWindow? _marquee;
	HashSet<FenceItem> _rubberBase = [];

	// 拖入：本程序发起的拖动取其项目标识（系统图标没有文件路径）和其中的文件，外部拖动两者都是文件路径
	string[]? _dropKeys;
	string[] _dropFiles = [];
	bool _dropFromDesktop;
	bool _dragInside;
	int _dragVersion;
	int _insertIndex = -1;
	readonly DropPreview _dropPreview = new();
	readonly ItemDropForwarder _forwarder = new();
	readonly ClickToRename _clickRename;

	public FenceWindow(FenceManager manager, FenceSettings model)
	{
		_manager = manager;
		Model = model;
		InitializeComponent();
		ItemsList.ItemsSource = _items;
		_clickRename = new ClickToRename(BeginRename, item => IsActive && ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem == item);
		// 按在空白处时框选的处理会把事件标记为已处理，仍要收到它才能取消别处的选择
		ItemsList.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(ItemsList_PreviewMouseDown), true);
		// 列表可用宽度变了（分区改大小、出现或去掉滚动条）时，详细信息视图重排各列
		ItemsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
		{
			if (e.ViewportWidthChange != 0)
			{
				UpdateDetailsColumns();
			}
		}));
		_refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		_refreshTimer.Tick += (_, _) =>
		{
			_refreshTimer.Stop();
			RefreshItems();
		};
		_collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		_collapseTimer.Tick += CollapseTimer_Tick;
		_buttonsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		_buttonsTimer.Tick += (_, _) =>
		{
			_buttonsTimer.Stop();
			// 刚切换过来的标签在鼠标动之前收不到 MouseEnter，IsMouseOver 还是 false：鼠标实际还在分区上就过会儿再看
			if (!_menuOpen && !IsMouseOver && GetBounds().Contains(NativeMethods.GetCursorPos()))
			{
				_buttonsTimer.Start();
			}
			else if (!_menuOpen && !IsMouseOver)
			{
				ShowTitleButtons(false);
			}
		};
		_tabHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		_tabHoverTimer.Tick += (_, _) =>
		{
			_tabHoverTimer.Stop();
			if (_hoverTab is FenceWindow tab && _dragInside)
			{
				_hoverTab = null;
				_ = _manager.ActivateTabAsync(tab, false);
			}
		};
		_mouseTabTimer = new DispatcherTimer();
		_mouseTabTimer.Tick += (_, _) =>
		{
			_mouseTabTimer.Stop();
			var tab = _mouseTab;
			_mouseTab = null;
			// 按着鼠标时是在拖分区或标签，不切换
			if (tab != null && TitleBar.IsMouseOver && Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released)
			{
				_ = _manager.ActivateTabAsync(tab, false);
			}
		};
	}

	/// <summary>
	/// 标题栏上的一个标签，对应标签组里的一个分区。
	/// </summary>
	public sealed class FenceTab(FenceWindow window, bool isActive, bool isVertical, bool isFilled)
	{
		public bool IsFilled => isFilled;

		public FenceWindow Window => window;

		public string Title => window.Model.Title;

		public string VerticalTitle => ToVerticalText(window.Model.Title);

		public bool IsActive => isActive;

		public bool IsVertical => isVertical;

		public bool IsPortal => window.Model.IsPortal;

		public ImageSource? FolderIcon => window.Model.IsPortal ? ShellIconLoader.FolderIcon : null;
	}

	public FenceSettings Model { get; }

	public IntPtr Handle => _hwnd;

	/// <summary>
	/// 桌面分区：成员是桌面上的项目，文件留在桌面；否则为映射分区。
	/// </summary>
	bool IsDesktopFence => !Model.IsPortal;

	/// <summary>
	/// 映射分区当前显示的文件夹：在分区里进入子文件夹后是该子文件夹，否则是映射的文件夹。
	/// </summary>
	string CurrentFolder => _subFolder ?? Model.FolderPath;

	/// <summary>
	/// 正在重命名其中的项目（自动整理会等改完名再处理）。
	/// </summary>
	public bool IsRenaming => _items.Any(i => i.IsRenaming);

	AppSettings Settings => _manager.Settings;

	double ScaleFactor => VisualTreeHelper.GetDpi(this).DpiScaleX;

	bool IsCollapsed => Model.RolledUp && !_tempExpanded;

	IconSizeMode EffectiveIconSize => Model.IconSize ?? Settings.DefaultIconSize;

	double IconDip => Model.View == FenceView.Icons ? EffectiveIconSize.ToDip() : Appearance.ListIconDip;

	public IReadOnlyList<FenceItem> Items => _items;

	public List<FenceItem> SelectedItems => ItemsList.SelectedItems.Cast<FenceItem>().ToList();

	public bool HasSelection => ItemsList.SelectedItems.Count > 0;

	static bool IsAdditive => (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

	#region 生命周期

	public void ShowOnDesktop()
	{
		new WindowInteropHelper(this).EnsureHandle();
		double scale = ScaleFactor;
		if (Model.Width < 60)
		{
			Model.Width = (int)(340 * scale);
		}
		if (Model.Height < 60)
		{
			Model.Height = (int)(240 * scale);
		}
		// 没卷起的分区按当前位置定标题栏在哪一侧，卷起的沿用保存的
		if (!Model.RolledUp)
		{
			Model.RollEdge = Model.RollDirection ?? AutoRollEdge(ExpandedRect);
		}
		UpdateTitle();
		ApplyAppearance();
		ApplyViewMode();
		// 首次定位若跨越不同 DPI 的显示器，WPF 会按新 DPI 缩放窗口，第二次把尺寸校正回保存的物理像素
		ApplyBounds();
		ApplyBounds();
		// 标签组里没显示的标签照常加载内容，只是不显示
		if (!_manager.IsHiddenTab(this))
		{
			Show();
			PlaceAboveDesktop();
		}
		OnFolderChanged();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
		// 去掉 WPF 为隐藏任务栏按钮而设置的隐藏所有者：所有者会约束层级；任务栏与 Alt+Tab 由 WS_EX_TOOLWINDOW 隐藏
		SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
		long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
		SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
		long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
		SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style & ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
	}

	public void CloseForReal()
	{
		_allowClose = true;
		StopWatcher();
		_clickRename.Cancel();
		_standIn?.Dispose();
		_refreshTimer.Stop();
		_collapseTimer.Stop();
		_buttonsTimer.Stop();
		_tabHoverTimer.Stop();
		_mouseTabTimer.Stop();
		_detachPreview?.Dispose();
		_marquee?.Dispose();
		Close();
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		// Alt+F4 等途径不能关掉分区，只有删除分区或退出程序时才真正关闭
		if (!_allowClose)
		{
			e.Cancel = true;
		}
		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		base.OnClosed(e);
		StopWatcher();
		if (!_allowClose)
		{
			_manager.OnWindowLost(this);
		}
	}

	public void PlaceAboveDesktop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			DesktopHost.PlaceAboveDesktop(_hwnd);
		}
	}

	public RECT GetBounds() => GetWindowRect(_hwnd);

	/// <summary>
	/// 散放图标排布时要避开的范围（物理像素）：按保存的位置和尺寸，卷起时只算收在边上的那一条，不受悬停临时展开影响。
	/// </summary>
	public RECT GetLayoutBounds() => LayoutBoundsOf(ExpandedRect);

	/// <summary>
	/// 展开范围是 expanded 时占的范围：卷起时只算收起的那一条（与 GetLayoutBounds 相同）。
	/// </summary>
	public RECT LayoutBoundsOf(RECT expanded) => Model.RolledUp ? CollapsedRect(expanded) : expanded;

	/// <summary>
	/// 标题栏在屏幕上的范围（物理像素），拖动别的分区到这里松手就合并成标签页。
	/// </summary>
	public RECT TitleBarRect() => ItemOps.ScreenRect(TitleBar);

	#endregion

	#region 窗口消息：置底、缩放边框、吸附

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
			{
				KeepDragResult(lParam);
				DesktopHost.KeepAboveDesktop(hwnd, lParam);
				break;
			}
			case WM_WINDOWPOSCHANGED:
			{
				UpdateBlurViewbox();
				break;
			}
			case WM_ENTERSIZEMOVE:
			{
				_slideVersion++;
				// 拖动、调整大小时盖住旁边的分区
				_manager.BringToFront(this);
				_dragCursorStart = NativeMethods.GetCursorPos();
				_dragRectStart = GetBounds();
				_dragLastRect = null;
				_dragFinished = false;
				_inSizeMove = true;
				_heightResized = false;
				_widthResized = false;
				break;
			}
			case WM_NCHITTEST:
			{
				int hit = HitTestResizeBorder(lParam);
				if (hit != 0)
				{
					handled = true;
					return new IntPtr(hit);
				}
				break;
			}
			case WM_SYSCOMMAND:
			{
				int command = (int)wParam & 0xFFF0;
				if (command == SC_MINIMIZE || command == SC_MAXIMIZE || command == SC_CLOSE)
				{
					handled = true;
				}
				break;
			}
			case WM_MOVING:
			{
				HideBlurWhileMoving();
				// 鼠标停在别的分区标题栏上时，松手就合并成标签页
				SetMergeTarget(_manager.MergeTargetAt(this, NativeMethods.GetCursorPos()));
				if (Settings.SnapToEdges)
				{
					SnapMoving(lParam);
					handled = true;
					return new IntPtr(1);
				}
				break;
			}
			case WM_SIZING:
			{
				HideBlurWhileMoving();
				_heightResized |= (int)wParam is not (WMSZ_LEFT or WMSZ_RIGHT);
				_widthResized |= (int)wParam is not (WMSZ_TOP or WMSZ_BOTTOM);
				var grid = GridSizes();
				// 不吸附时也要接管：被拖动的边要止于相邻分区
				SnapSizing((int)wParam, lParam, grid);
				ShowGridHint(Marshal.PtrToStructure<RECT>(lParam), grid);
				handled = true;
				return new IntPtr(1);
			}
			case WM_EXITSIZEMOVE:
			{
				GridHint.Visibility = Visibility.Collapsed;
				_dragFinished = true;
				// 等系统的拖动循环完全结束后再收尾
				Dispatcher.InvokeAsync(FinishMoveSize, DispatcherPriority.Input);
				break;
			}
		}
		return IntPtr.Zero;
	}

	int HitTestResizeBorder(IntPtr lParam)
	{
		if (Model.Locked)
		{
			return 0;
		}
		long value = lParam.ToInt64();
		int x = unchecked((short)(value & 0xFFFF));
		int y = unchecked((short)((value >> 16) & 0xFFFF));
		var rect = GetWindowRect(_hwnd);
		int border = (int)Math.Ceiling(ResizeBorderDip * ScaleFactor);
		// 收起后只能顺着那一条的长边方向调整大小
		bool lengthwiseOnly = IsCollapsed;
		bool vertical = IsVerticalRoll;
		bool left = !(lengthwiseOnly && vertical) && x < rect.Left + border;
		bool right = !(lengthwiseOnly && vertical) && x >= rect.Right - border;
		bool top = !(lengthwiseOnly && !vertical) && y < rect.Top + border;
		bool bottom = !(lengthwiseOnly && !vertical) && y >= rect.Bottom - border;
		if (top)
		{
			return left ? HTTOPLEFT : right ? HTTOPRIGHT : HTTOP;
		}
		if (bottom)
		{
			return left ? HTBOTTOMLEFT : right ? HTBOTTOMRIGHT : HTBOTTOM;
		}
		return left ? HTLEFT : right ? HTRIGHT : 0;
	}

	sealed class SnapLines
	{
		public readonly List<int> Left = [];
		public readonly List<int> Right = [];
		public readonly List<int> Top = [];
		public readonly List<int> Bottom = [];
	}

	/// <summary>
	/// 收集吸附线：所在显示器工作区边缘，以及相邻分区的对齐线与留缝邻接线。
	/// </summary>
	SnapLines CollectSnapLines(RECT rect)
	{
		var lines = new SnapLines();
		var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
		lines.Left.Add(work.Left);
		lines.Right.Add(work.Right);
		lines.Top.Add(work.Top);
		lines.Bottom.Add(work.Bottom);
		int gap = (int)Math.Round(Settings.SnapGap * ScaleFactor);
		int near = (int)(SnapDistanceDip * ScaleFactor);
		foreach (var other in _manager.Windows)
		{
			if (other == this || !other.IsVisible)
			{
				continue;
			}
			var o = other.GetBounds();
			// 只与另一方向上重叠或挨得很近的分区对齐，避免被远处分区的延长线"吸住"
			if (rect.Top < o.Bottom + near && o.Top < rect.Bottom + near)
			{
				lines.Left.Add(o.Left);
				lines.Left.Add(o.Right + gap);
				lines.Right.Add(o.Right);
				lines.Right.Add(o.Left - gap);
			}
			if (rect.Left < o.Right + near && o.Left < rect.Right + near)
			{
				lines.Top.Add(o.Top);
				lines.Top.Add(o.Bottom + gap);
				lines.Bottom.Add(o.Bottom);
				lines.Bottom.Add(o.Top - gap);
			}
		}
		return lines;
	}

	/// <summary>
	/// 系统按「上一次结果 + 鼠标增量」推算新位置，被吸附改写掉的位移会丢失，窗口因此被粘住拖不走；
	/// 这里改按鼠标相对拖动起点的绝对位移计算，越过吸附距离后自然脱离。
	/// </summary>
	void SnapMoving(IntPtr lParam)
	{
		var proposed = Marshal.PtrToStructure<RECT>(lParam);
		var cursor = NativeMethods.GetCursorPos();
		int left = _dragRectStart.Left + cursor.X - _dragCursorStart.X;
		int top = _dragRectStart.Top + cursor.Y - _dragCursorStart.Y;
		var rect = new RECT(left, top, left + proposed.Width, top + proposed.Height);
		var lines = CollectSnapLines(rect);
		int threshold = (int)(SnapDistanceDip * ScaleFactor);
		int dx = Closer(Snap(rect.Left, lines.Left, threshold), Snap(rect.Right, lines.Right, threshold));
		int dy = Closer(Snap(rect.Top, lines.Top, threshold), Snap(rect.Bottom, lines.Bottom, threshold));
		rect = new RECT(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
		Marshal.StructureToPtr(rect, lParam, false);
		_dragLastRect = rect;
	}

	/// <summary>
	/// 与移动同理，被拖动的边按鼠标相对起点的绝对位移计算后再吸附，最后止于相邻分区。
	/// </summary>
	void SnapSizing(int edge, IntPtr lParam, (List<int> Widths, List<int> Heights) grid)
	{
		var rect = Marshal.PtrToStructure<RECT>(lParam);
		var cursor = NativeMethods.GetCursorPos();
		int dx = cursor.X - _dragCursorStart.X;
		int dy = cursor.Y - _dragCursorStart.Y;
		bool left = edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT;
		bool right = edge is WMSZ_RIGHT or WMSZ_TOPRIGHT or WMSZ_BOTTOMRIGHT;
		bool top = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
		bool bottom = edge is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT;
		if (left)
		{
			rect.Left = _dragRectStart.Left + dx;
		}
		if (right)
		{
			rect.Right = _dragRectStart.Right + dx;
		}
		if (top)
		{
			rect.Top = _dragRectStart.Top + dy;
		}
		if (bottom)
		{
			rect.Bottom = _dragRectStart.Bottom + dy;
		}
		var lines = Settings.SnapToEdges ? CollectSnapLines(rect) : new SnapLines();
		if (Settings.SnapToGrid && !IsCollapsed)
		{
			// 按图标整行、整列吸附：候选尺寸换算成被拖动那条边的位置
			lines.Left.AddRange(grid.Widths.Select(w => rect.Right - w));
			lines.Right.AddRange(grid.Widths.Select(w => rect.Left + w));
			lines.Top.AddRange(grid.Heights.Select(h => rect.Bottom - h));
			lines.Bottom.AddRange(grid.Heights.Select(h => rect.Top + h));
		}
		int threshold = (int)(SnapDistanceDip * ScaleFactor);
		if (left)
		{
			rect.Left += Snap(rect.Left, lines.Left, threshold) ?? 0;
		}
		if (right)
		{
			rect.Right += Snap(rect.Right, lines.Right, threshold) ?? 0;
		}
		if (top)
		{
			rect.Top += Snap(rect.Top, lines.Top, threshold) ?? 0;
		}
		if (bottom)
		{
			rect.Bottom += Snap(rect.Bottom, lines.Bottom, threshold) ?? 0;
		}
		int minWidth = (int)Math.Ceiling(MinWidth * ScaleFactor);
		int minHeight = (int)Math.Ceiling(MinHeight * ScaleFactor);
		if (rect.Width < minWidth)
		{
			if (left)
			{
				rect.Left = rect.Right - minWidth;
			}
			else
			{
				rect.Right = rect.Left + minWidth;
			}
		}
		if (rect.Height < minHeight)
		{
			if (top)
			{
				rect.Top = rect.Bottom - minHeight;
			}
			else
			{
				rect.Bottom = rect.Top + minHeight;
			}
		}
		// 卷起的分区只占收起的那一条，窗口却可能是悬停展开的样子，松手后再统一处理重叠
		if (!Model.RolledUp)
		{
			StopAtNeighbors(ref rect, left, right, top, bottom);
		}
		Marshal.StructureToPtr(rect, lParam, false);
		_dragLastRect = rect;
	}

	/// <summary>
	/// 分区之间不重叠：调整大小时被拖动的边碰到相邻分区就停住（留出吸附间距），最多退回拖动前的位置；
	/// 拖动前就与之重叠的分区不管。同时压住一个分区的两条边时，退回得少的那条停住。
	/// </summary>
	void StopAtNeighbors(ref RECT rect, bool left, bool right, bool top, bool bottom)
	{
		var start = _dragRectStart;
		int gap = (int)Math.Round(Settings.SnapGap * ScaleFactor);
		foreach (var other in _manager.Windows)
		{
			var o = other.GetLayoutBounds();
			if (other == this || o.IntersectsWith(start) || !o.Inflate(gap).IntersectsWith(rect))
			{
				continue;
			}
			// 各条边需要退回多少，只算朝这个分区伸过去的边
			int pullRight = right && o.Left >= start.Right ? rect.Right - Math.Max(start.Right, o.Left - gap) : 0;
			int pullLeft = left && o.Right <= start.Left ? Math.Min(start.Left, o.Right + gap) - rect.Left : 0;
			int pullBottom = bottom && o.Top >= start.Bottom ? rect.Bottom - Math.Max(start.Bottom, o.Top - gap) : 0;
			int pullTop = top && o.Bottom <= start.Top ? Math.Min(start.Top, o.Bottom + gap) - rect.Top : 0;
			int pull = new[] { pullRight, pullLeft, pullBottom, pullTop }.Where(p => p > 0).DefaultIfEmpty(0).Min();
			if (pull == 0)
			{
				continue;
			}
			if (pull == pullRight)
			{
				rect.Right -= pull;
			}
			else if (pull == pullLeft)
			{
				rect.Left += pull;
			}
			else if (pull == pullBottom)
			{
				rect.Bottom -= pull;
			}
			else
			{
				rect.Top += pull;
			}
		}
	}

	/// <summary>
	/// 刚好容纳整数行、整数列图标时的窗口宽高（物理像素），按当前实际布局计算，超出现有内容的部分按行高/列宽外推。
	/// </summary>
	(List<int> Widths, List<int> Heights) GridSizes()
	{
		var widths = new List<int>();
		var heights = new List<int>();
		var containers = Enumerable.Range(0, _items.Count)
				.Select(i => ItemsList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)
				.OfType<ListBoxItem>()
				.Where(c => c.IsVisible)
				.ToList();
		if (containers.Count == 0 || VisualTreeHelper.GetParent(containers[0]) is not Panel panel)
		{
			return (widths, heights);
		}
		_scroller ??= FindDescendant<ScrollViewer>(ItemsList);
		if (_scroller == null)
		{
			return (widths, heights);
		}
		double scale = ScaleFactor;
		var rect = GetBounds();
		var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
		// 窗口外框（边框、标题栏、列表内边距、出现时的竖向滚动条）按当前实际布局量出：
		// 非整数缩放下边框会被取整得更宽，按固定数值算会少零点几像素，刚好装下时也会冒出滚动条
		double chromeWidth = ActualWidth - _scroller.ViewportWidth;
		double chromeHeight = ActualHeight - _scroller.ViewportHeight;
		// 向上取整，窗口宁可多一点也不能比内容小（减去的小量用于抵消浮点误差）
		int ToPixels(double dip) => (int)Math.Ceiling(dip * scale - 0.01);

		var slots = containers.Select(c =>
		{
			var bounds = c.TransformToAncestor(panel).TransformBounds(new Rect(c.RenderSize));
			var margin = c.Margin;
			return new Rect(bounds.Left - margin.Left, bounds.Top - margin.Top, bounds.Width + margin.Left + margin.Right, bounds.Height + margin.Top + margin.Bottom);
		}).ToList();
		var rowBottoms = slots.GroupBy(s => Math.Round(s.Top)).OrderBy(g => g.Key).Select(g => g.Max(s => s.Bottom)).ToList();
		double rowHeight = rowBottoms[0];
		for (double bottom = rowBottoms[^1] + rowHeight; (chromeHeight + bottom) * scale <= work.Height; bottom += rowHeight)
		{
			rowBottoms.Add(bottom);
		}
		heights.AddRange(rowBottoms.Select(b => ToPixels(chromeHeight + b)));

		// 列表视图只有一列，不按列吸附
		if (Model.View == FenceView.Icons)
		{
			double columnWidth = slots[0].Width;
			for (int n = 1; (chromeWidth + n * columnWidth) * scale <= work.Width; n++)
			{
				widths.Add(ToPixels(chromeWidth + n * columnWidth));
			}
		}
		return (widths, heights);
	}

	/// <summary>
	/// 调整大小时在分区中央显示装得下的列数 × 行数（列表视图只显示行数）：两个方向都刚好装满整列、整行时整个提示变蓝，
	/// 只有一个方向刚好装满时只把那个数字标成蓝色。
	/// </summary>
	void ShowGridHint(RECT rect, (List<int> Widths, List<int> Heights) grid)
	{
		if (IsCollapsed || grid.Heights.Count == 0)
		{
			GridHint.Visibility = Visibility.Collapsed;
			return;
		}
		// 留 1 像素余量，避免换算取整让刚好吸附到的尺寸少算一行
		int rows = grid.Heights.Count(h => h <= rect.Height + 1);
		int columns = grid.Widths.Count(w => w <= rect.Width + 1);
		bool list = Model.View != FenceView.Icons;
		bool rowsFit = grid.Heights.Any(h => Math.Abs(h - rect.Height) <= 1);
		// 列表视图只有一列，只看行
		bool columnsFit = list || grid.Widths.Any(w => Math.Abs(w - rect.Width) <= 1);
		bool allFit = rowsFit && columnsFit;
		GridHint.Background = allFit ? SnappedHintBackground : HintBackground;
		GridHintText.Inlines.Clear();
		if (!list)
		{
			GridHintText.Inlines.Add(HintRun(columns.ToString(), columnsFit && !allFit));
			GridHintText.Inlines.Add(new Run(" × "));
		}
		GridHintText.Inlines.Add(HintRun(list ? $"{rows} 行" : rows.ToString(), rowsFit && !allFit));
		GridHint.Visibility = Visibility.Visible;
	}

	/// <summary>
	/// 提示里的一个数字，这个方向刚好装满时标成蓝色。
	/// </summary>
	static Run HintRun(string text, bool fits) => fits ? new Run(text) { Foreground = SnappedHintText } : new Run(text);

	/// <summary>
	/// 拖动或缩放结束时，系统会按鼠标位置再定位一次而忽略最后的吸附结果，这里改回拖动中最后显示的位置；
	/// 按 Esc 取消（回到起点）时不干预。
	/// </summary>
	void KeepDragResult(IntPtr lParam)
	{
		if (!_dragFinished || _dragLastRect is not RECT last)
		{
			return;
		}
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		bool moved = (pos.flags & SWP_NOMOVE) == 0 && (pos.x != last.Left || pos.y != last.Top);
		bool resized = (pos.flags & SWP_NOSIZE) == 0 && (pos.cx != last.Width || pos.cy != last.Height);
		bool atStartPosition = (pos.flags & SWP_NOMOVE) != 0 || (pos.x == _dragRectStart.Left && pos.y == _dragRectStart.Top);
		bool atStartSize = (pos.flags & SWP_NOSIZE) != 0 || (pos.cx == _dragRectStart.Width && pos.cy == _dragRectStart.Height);
		if ((!moved && !resized) || (atStartPosition && atStartSize))
		{
			return;
		}
		pos.x = last.Left;
		pos.y = last.Top;
		pos.cx = last.Width;
		pos.cy = last.Height;
		pos.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
		Marshal.StructureToPtr(pos, lParam, false);
	}

	async void FinishMoveSize()
	{
		// 系统若在 WM_EXITSIZEMOVE 之前就做了最终定位，这里补一次校正（取消拖动、回到起点的情况除外）
		if (_dragLastRect is RECT last)
		{
			var current = GetBounds();
			bool atLast = current.Left == last.Left && current.Top == last.Top && current.Width == last.Width && current.Height == last.Height;
			bool atStart = current.Left == _dragRectStart.Left && current.Top == _dragRectStart.Top
					&& current.Width == _dragRectStart.Width && current.Height == _dragRectStart.Height;
			if (!atLast && !atStart)
			{
				SetWindowPos(_hwnd, IntPtr.Zero, last.Left, last.Top, last.Width, last.Height, SWP_NOZORDER | SWP_NOACTIVATE);
			}
		}
		_dragLastRect = null;
		_dragFinished = false;
		var mergeTarget = _mergeTarget;
		SetMergeTarget(null);
		// 松手时鼠标在别的分区标题栏上：合并成标签页（按 Esc 取消、回到原处时不合并）
		if (mergeTarget != null && !GetBounds().Equals(_dragRectStart))
		{
			await _manager.MergeAsync(this, mergeTarget);
		}
		else if (!await SaveAndAvoidOverlap())
		{
			// 滑动途中又开始拖动，由那一次收尾
			return;
		}
		_inSizeMove = false;
		_heightResized = false;
		_widthResized = false;
		ShowBlurAfterMoving();
		// 悬停展开时调整完大小，鼠标若已移出，按正常节奏收起
		if (_tempExpanded)
		{
			RestartTimer(_collapseTimer);
		}
	}

	/// <summary>
	/// 保存拖动结果。分区之间不重叠：松手时压住了别的分区，滑到最近的空位，所在屏幕上放不下时滑回拖动前的位置；
	/// 只单击标题栏、没有移动时不动，原本就重叠的旧布局等下次拖动时再处理。滑动途中又开始拖动时返回 false。
	/// </summary>
	async Task<bool> SaveAndAvoidOverlap()
	{
		SaveBounds();
		var bounds = GetBounds();
		if (bounds.Equals(_dragRectStart))
		{
			return true;
		}
		var layout = GetLayoutBounds();
		var target = _manager.FindFreeSpot(this, layout) is RECT spot
				? new RECT(bounds.Left + spot.Left - layout.Left, bounds.Top + spot.Top - layout.Top, bounds.Right + spot.Left - layout.Left, bounds.Bottom + spot.Top - layout.Top)
				: _dragRectStart;
		if (target.Equals(bounds))
		{
			return true;
		}
		if (!await SlideTo(target))
		{
			return false;
		}
		SaveBounds();
		return true;
	}

	/// <summary>
	/// 先快后慢地滑到 target（物理像素）；途中又开始拖动或分区被删除时停下，返回 false。
	/// </summary>
	public async Task<bool> SlideTo(RECT target)
	{
		int version = ++_slideVersion;
		var from = GetBounds();
		var clock = Stopwatch.StartNew();
		while (true)
		{
			double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / SlideMilliseconds);
			double k = 1 - Math.Pow(1 - t, 3);
			int Lerp(int a, int b) => a + (int)Math.Round((b - a) * k);
			int left = Lerp(from.Left, target.Left);
			int top = Lerp(from.Top, target.Top);
			SetWindowPos(_hwnd, IntPtr.Zero, left, top, Lerp(from.Right, target.Right) - left, Lerp(from.Bottom, target.Bottom) - top, SWP_NOZORDER | SWP_NOACTIVATE);
			if (t >= 1)
			{
				return true;
			}
			await FlyingIcon.NextFrame();
			if (version != _slideVersion || _allowClose)
			{
				return false;
			}
		}
	}

	static int? Snap(int edge, List<int> lines, int threshold)
	{
		int? best = null;
		foreach (var line in lines)
		{
			int delta = line - edge;
			if (Math.Abs(delta) <= threshold && (best == null || Math.Abs(delta) < Math.Abs(best.Value)))
			{
				best = delta;
			}
		}
		return best;
	}

	static int Closer(int? a, int? b)
	{
		if (a is int x && (b is not int y || Math.Abs(x) <= Math.Abs(y)))
		{
			return x;
		}
		return b ?? 0;
	}

	void SaveBounds()
	{
		var rect = GetBounds();
		if (Model.RolledUp)
		{
			SaveRolledBounds(rect);
		}
		else
		{
			Model.X = rect.Left;
			Model.Y = rect.Top;
			Model.Width = rect.Width;
			Model.Height = rect.Height;
			// 标题栏随新位置换到贴着的那一侧
			var edge = Model.RollDirection ?? AutoRollEdge(rect);
			if (edge != Model.RollEdge)
			{
				Model.RollEdge = edge;
				ApplyBounds();
			}
		}
		// 用户调整过的位置和大小属于当前所在显示器的缩放比例
		Model.LayoutDpi = GetDpiForRect(ExpandedRect);
		_manager.SyncGroup(this);
		_manager.SaveSoon();
		_manager.OnFenceLayoutChanged();
	}

	/// <summary>
	/// 卷起状态下移动或调整大小后反推展开后的范围：窗口是收起的那一条，或悬停临时展开时可能被屏幕边缘截短的分区。
	/// 收向的一侧与窗口对齐，另一方向上的尺寸只在用户拖动了对应的边时才更新；
	/// 自动方向时，收起的那一条被拖到屏幕下边或左右边就改为收向那条边，临时展开时按新位置重新确定。
	/// </summary>
	void SaveRolledBounds(RECT window)
	{
		bool vertical = IsVerticalRoll;
		int width = vertical && !_widthResized ? Model.Width : window.Width;
		int height = !vertical && !_heightResized ? Model.Height : window.Height;
		RECT Expanded(RollEdge edge)
		{
			int left = edge == RollEdge.Right ? window.Right - width : window.Left;
			int top = edge == RollEdge.Bottom ? window.Bottom - height : window.Top;
			return new RECT(left, top, left + width, top + height);
		}
		var edge = Model.RollEdge;
		var expanded = Expanded(edge);
		if (Model.RollDirection == null)
		{
			if (IsCollapsed)
			{
				var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref window, MONITOR_DEFAULTTONEAREST));
				int near = (int)(DockDistanceDip * ScaleFactor);
				edge = vertical
						? IsNear(window.Left, work.Left, near) ? RollEdge.Left : IsNear(window.Right, work.Right, near) ? RollEdge.Right : edge
						: IsNear(window.Bottom, work.Bottom, near) ? RollEdge.Bottom : RollEdge.Top;
				// 收起的那一条留在松手的位置，展开范围改按新的一侧推算
				expanded = Expanded(edge);
			}
			else
			{
				edge = AutoRollEdge(expanded);
			}
		}
		Model.X = expanded.Left;
		Model.Y = expanded.Top;
		Model.Width = expanded.Width;
		Model.Height = expanded.Height;
		if (edge != Model.RollEdge)
		{
			Model.RollEdge = edge;
			ApplyBounds();
		}
	}

	#endregion

	#region 尺寸、卷起、外观

	public void ApplyBounds()
	{
		// 别处直接摆放（缩放变化、同组同步、切换标签等）时，正在播的卷起、展开动画直接跳到结束
		StopRollAnimation();
		PlaceWindow();
	}

	/// <summary>
	/// 按卷起状态窗口应占的范围（物理像素）。
	/// </summary>
	RECT TargetRect() => !Model.RolledUp ? ExpandedRect : IsCollapsed ? CollapsedRect() : TempExpandedRect();

	void PlaceWindow()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		var rect = TargetRect();
		// WPF 在窗口尺寸变化时会立即重新布局并出一帧：展开时先让内容区可见再放大窗口，否则那一帧只有背景、没有图标；
		// 收起时先缩小窗口再折叠内容区，否则那一帧是空着的大窗口
		bool collapsed = IsCollapsed;
		if (!collapsed)
		{
			UpdateRollVisuals();
		}
		// 左右收起时窗口只有标题栏那么宽：先放开最小宽度再改大小，改完再按当前形态设回，免得被最小宽度撑开
		double minWidth = collapsed && IsVerticalRoll ? TitleBarDip + 2 : MinWidthDip;
		MinWidth = Math.Min(MinWidth, minWidth);
		SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
		MinWidth = minWidth;
		if (collapsed)
		{
			UpdateRollVisuals();
		}
	}

	/// <summary>
	/// 卷起、展开（包括悬停临时展开和收回）时带动画：展开时窗口先放大到展开后的范围、只露出标题栏那一条，裁剪区域再伸展开；
	/// 收起时裁剪区域先缩回那一条，再缩小窗口。内容始终按展开的大小排好，不会边动边重新排版；动画中途反向时从当前位置接着走。
	/// </summary>
	void ApplyRollBounds()
	{
		bool running = _rollClip != null;
		bool shownCollapsed = ContentHost.Visibility != Visibility.Visible;
		if (_hwnd == IntPtr.Zero || !IsVisible || (!running && shownCollapsed == IsCollapsed))
		{
			ApplyBounds();
			return;
		}
		double? extent = _rollClip is { } clip ? RevealExtent(clip.Rect) : null;
		StopRollAnimation();
		double strip = CollapsedHeight() / ScaleFactor;
		var root = (FrameworkElement)Content;
		if (IsCollapsed)
		{
			var size = new Size(root.ActualWidth, root.ActualHeight);
			// 缩回那一条后再缩小窗口；挪窗口前裁剪还在，截图（如果要顶替）和屏幕上看到的一样
			AnimateReveal(size, extent ?? RevealExtent(size), strip, () => MoveWindow(RevealRect(size, strip), () =>
			{
				ClearRollClip();
				PlaceWindow();
			}, null));
		}
		else
		{
			var target = TargetRect();
			var size = new Size(target.Width / ScaleFactor, target.Height / ScaleFactor);
			double from = extent ?? strip;
			// 先设好裁剪再放大窗口（窗口尺寸一变 WPF 就会出一帧），放好后再伸展开
			MoveWindow(new Rect(0, 0, root.ActualWidth, root.ActualHeight), () =>
			{
				SetRollClip(RevealRect(size, from));
				PlaceWindow();
			}, () => AnimateReveal(size, from, RevealExtent(size), null));
		}
	}

	void AnimateReveal(Size size, double from, double to, Action? completed)
	{
		var clip = SetRollClip(RevealRect(size, from));
		var animation = new RectAnimation(RevealRect(size, from), RevealRect(size, to), RollDuration)
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
		};
		// 结束事件在渲染过程中触发，收尾（可能要缩小窗口）挪到之后再做；在那之前裁剪停在终点，画面不变
		animation.Completed += (_, _) => Dispatcher.BeginInvoke(() =>
		{
			// 已被新的动画或直接摆放接替
			if (_rollClip != clip)
			{
				return;
			}
			if (completed != null)
			{
				completed();
			}
			else
			{
				ClearRollClip();
			}
		});
		clip.BeginAnimation(RectangleGeometry.RectProperty, animation);
	}

	RectangleGeometry SetRollClip(Rect rect)
	{
		var clip = new RectangleGeometry(rect, Settings.CornerRadius, Settings.CornerRadius);
		_rollClip = clip;
		((UIElement)Content).Clip = clip;
		return clip;
	}

	void ClearRollClip()
	{
		_rollClip = null;
		((UIElement)Content).Clip = null;
	}

	void StopRollAnimation()
	{
		if (_rollClip == null)
		{
			return;
		}
		_rollClip.BeginAnimation(RectangleGeometry.RectProperty, null);
		ClearRollClip();
	}

	/// <summary>
	/// 执行 move 摆放窗口，再执行 then；要挪动窗口左上角时（标题栏在下边、右边的分区卷起和展开）先用截图顶替。
	/// </summary>
	/// <param name="visible">窗口里现在看得到的部分（DIP），顶替时截这一块。</param>
	void MoveWindow(Rect visible, Action move, Action? then)
	{
		var bounds = GetBounds();
		var target = TargetRect();
		// 窗口没显示时（比如动画途中标签被切走）直接挪
		if (IsVisible && (bounds.Left != target.Left || bounds.Top != target.Top))
		{
			MoveBehindStandIn(visible, move);
		}
		else
		{
			move();
		}
		then?.Invoke();
	}

	/// <summary>
	/// 透明窗口挪动左上角时，DWM 可能正好在窗口已经挪了、新画面还没给上的那一刻合成一帧，旧画面摆在新位置上闪一下
	/// （实测：展开时标题栏那一条闪到另一侧，收起时整条消失；标题栏在上边、左边时卷起展开不挪左上角，所以不闪）。
	/// 所以先用截图在同一次屏幕刷新里顶替分区（分区用 DWM 藏起来），挪好再换回：WPF 在 SetWindowPos 里就同步画好了新的一帧（实测），挪完马上就能换回。
	/// </summary>
	void MoveBehindStandIn(Rect visible, Action move)
	{
		bool shown = ShowStandIn(visible);
		move();
		if (shown)
		{
			DwmFlush();
			SetCloaked(false);
			_standIn?.Hide();
		}
	}

	/// <summary>
	/// 把分区 visible（DIP）里现在的画面放到截图窗口上，并在同一次屏幕刷新里把分区藏起来；失败时返回 false，分区照常显示。
	/// </summary>
	bool ShowStandIn(Rect visible)
	{
		try
		{
			double scale = ScaleFactor;
			var bounds = GetBounds();
			var screen = new RECT(bounds.Left + (int)Math.Round(visible.Left * scale), bounds.Top + (int)Math.Round(visible.Top * scale),
					bounds.Left + (int)Math.Round(visible.Right * scale), bounds.Top + (int)Math.Round(visible.Bottom * scale));
			var image = Snapshot(visible, screen.Width, screen.Height);
			_standIn ??= new StandInWindow();
			// 截图显示出来和分区藏起来要落在同一次屏幕刷新里：截图带透明度，两个叠着显示会更暗
			DwmFlush();
			_standIn.Show(image, screen, _hwnd);
			SetCloaked(true);
			return true;
		}
		catch (Exception ex)
		{
			Log.Warn("卷起、展开时截图顶替失败", ex);
			_standIn?.Hide();
			return false;
		}
	}

	/// <summary>
	/// 用 WPF 把窗口里 region（DIP）范围的画面画成图：width × height 物理像素、预乘透明度，和屏幕上的像素一一对应。
	/// 不截屏幕：动态壁纸（Wallpaper Engine）用 GDI 截屏是黑的，截进去后分区背后会变黑闪一下（实测）；带着透明度，后面的壁纸照常透出来。
	/// </summary>
	BitmapSource Snapshot(Rect region, int width, int height)
	{
		double scale = ScaleFactor;
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			var brush = new VisualBrush((Visual)Content) { Viewbox = region, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
			context.DrawRectangle(brush, null, new Rect(0, 0, width / scale, height / scale));
		}
		var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		bitmap.Freeze();
		return bitmap;
	}

	/// <summary>
	/// 用 DWM 把窗口藏起来（cloak）或取消：藏着时照常绘制，只是不显示在屏幕上，显示和隐藏都在下一次屏幕刷新时生效。
	/// </summary>
	public void SetCloaked(bool cloaked)
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		int value = cloaked ? 1 : 0;
		DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref value, sizeof(int));
	}

	/// <summary>
	/// 从标题栏所在的一侧露出 extent 那么高（上下收起时）或那么宽（左右收起时）的范围（DIP）。
	/// </summary>
	Rect RevealRect(Size size, double extent) => Model.RollEdge switch
	{
		RollEdge.Top => new Rect(0, 0, size.Width, extent),
		RollEdge.Bottom => new Rect(0, size.Height - extent, size.Width, extent),
		RollEdge.Left => new Rect(0, 0, extent, size.Height),
		RollEdge.Right => new Rect(size.Width - extent, 0, extent, size.Height),
	};

	double RevealExtent(Size size) => IsVerticalRoll ? size.Width : size.Height;

	double RevealExtent(Rect rect) => IsVerticalRoll ? rect.Width : rect.Height;

	/// <summary>
	/// 收起后剩下那一条的厚度（物理像素）：标题栏和两侧边框按 WPF 布局取整的方式各自取整再相加。
	/// 不能把 TitleBarDip + 2 乘缩放比例后整体取整：150% 时 1 DIP 的边框取整成 2 像素，三者共 49 像素，整体算只有 48，
	/// 收起时标题栏被挤偏 1 像素，标题栏在下边、右边的分区展开、收起时就会动一下（在上边、左边时不受影响）。
	/// </summary>
	int CollapsedHeight()
	{
		double scale = ScaleFactor;
		return (int)Math.Round(TitleBarDip * scale) + 2 * (int)Math.Round(FrameBorderDip * scale);
	}

	RECT ExpandedRect => new(Model.X, Model.Y, Model.X + Model.Width, Model.Y + Model.Height);

	bool IsVerticalRoll => Model.RollEdge is RollEdge.Left or RollEdge.Right;

	/// <summary>
	/// 收起后剩下的那一条：沿收向的一侧，宽度（或高度）正好是标题栏。
	/// </summary>
	RECT CollapsedRect() => CollapsedRect(ExpandedRect);

	RECT CollapsedRect(RECT r)
	{
		int thickness = CollapsedHeight();
		return Model.RollEdge switch
		{
			RollEdge.Top => new RECT(r.Left, r.Top, r.Right, r.Top + thickness),
			RollEdge.Bottom => new RECT(r.Left, r.Bottom - thickness, r.Right, r.Bottom),
			RollEdge.Left => new RECT(r.Left, r.Top, r.Left + thickness, r.Bottom),
			RollEdge.Right => new RECT(r.Right - thickness, r.Top, r.Right, r.Bottom),
		};
	}

	/// <summary>
	/// 悬停临时展开：从收向的一侧朝屏幕内展开，另一侧不超出工作区。
	/// </summary>
	RECT TempExpandedRect()
	{
		var r = ExpandedRect;
		var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST));
		int min = CollapsedHeight() * 3;
		return Model.RollEdge switch
		{
			RollEdge.Top => new RECT(r.Left, r.Top, r.Right, Math.Max(r.Top + min, Math.Min(r.Bottom, work.Bottom))),
			RollEdge.Bottom => new RECT(r.Left, Math.Min(r.Bottom - min, Math.Max(r.Top, work.Top)), r.Right, r.Bottom),
			RollEdge.Left => new RECT(r.Left, r.Top, Math.Max(r.Left + min, Math.Min(r.Right, work.Right)), r.Bottom),
			RollEdge.Right => new RECT(Math.Min(r.Right - min, Math.Max(r.Left, work.Left)), r.Top, r.Right, r.Bottom),
		};
	}

	/// <summary>
	/// 自动卷起方向：贴着屏幕上边或下边时上下收（同时贴着左右边也按上下），只贴左右边时左右收，不贴边时向上。
	/// </summary>
	RollEdge AutoRollEdge(RECT rect)
	{
		var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
		int near = (int)(DockDistanceDip * ScaleFactor);
		if (IsNear(rect.Top, work.Top, near))
		{
			return RollEdge.Top;
		}
		if (IsNear(rect.Bottom, work.Bottom, near))
		{
			return RollEdge.Bottom;
		}
		if (IsNear(rect.Left, work.Left, near))
		{
			return RollEdge.Left;
		}
		return IsNear(rect.Right, work.Right, near) ? RollEdge.Right : RollEdge.Top;
	}

	/// <summary>
	/// 分区的边离屏幕边足够近才算贴着；伸出屏幕外很多的不算，免得收起后那一条落到屏幕外。
	/// </summary>
	static bool IsNear(int edge, int screenEdge, int near) => Math.Abs(edge - screenEdge) <= near;

	void SetRollDirection(RollEdge? direction)
	{
		Model.RollDirection = direction;
		Model.RollEdge = direction ?? AutoRollEdge(ExpandedRect);
		ApplyBounds();
		_manager.SyncGroup(this);
		_manager.OnFenceLayoutChanged();
		_manager.SaveSoon();
	}

	public void ToggleRollUp()
	{
		// 收向标题栏所在的一侧，卷起和固定展开时标题栏都不动
		Model.RolledUp = !Model.RolledUp;
		_tempExpanded = false;
		_collapseTimer.Stop();
		// 固定展开后可能盖到旁边的分区，放在它们上面
		if (!Model.RolledUp)
		{
			_manager.BringToFront(this);
		}
		ApplyRollBounds();
		_manager.SyncGroup(this);
		_manager.SaveSoon();
		_manager.OnFenceLayoutChanged();
	}

	void UpdateRollVisuals()
	{
		bool collapsed = IsCollapsed;
		// 标题栏固定在收向的一侧（抽屉式），收起、悬停展开、固定展开时都不动
		var edge = Model.RollEdge;
		ApplyTitlePlacement(edge);
		RollButton.Content = Model.RolledUp ? "" : "";
		RollButton.ToolTip = Model.RolledUp ? "固定展开" : "卷起";
		double radius = Math.Max(0, Settings.CornerRadius - 1);
		TitleBar.CornerRadius = collapsed ? new CornerRadius(radius) : edge switch
		{
			RollEdge.Top => new CornerRadius(radius, radius, 0, 0),
			RollEdge.Bottom => new CornerRadius(0, 0, radius, radius),
			RollEdge.Left => new CornerRadius(radius, 0, 0, radius),
			RollEdge.Right => new CornerRadius(0, radius, radius, 0),
		};
		ContentHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>
	/// 把标题栏放到指定一侧，内容区占其余部分；左右两侧的标题栏竖着放，文字旋转 90°。
	/// </summary>
	void ApplyTitlePlacement(RollEdge edge)
	{
		if (_titleEdge == edge)
		{
			return;
		}
		_titleEdge = edge;
		bool vertical = edge is RollEdge.Left or RollEdge.Right;
		int barIndex = edge is RollEdge.Top or RollEdge.Left ? 0 : 1;
		var bar = new GridLength(TitleBarDip);
		var rest = new GridLength(1, GridUnitType.Star);
		LayoutRoot.RowDefinitions.Clear();
		LayoutRoot.ColumnDefinitions.Clear();
		if (vertical)
		{
			LayoutRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = barIndex == 0 ? bar : rest });
			LayoutRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = barIndex == 0 ? rest : bar });
		}
		else
		{
			LayoutRoot.RowDefinitions.Add(new RowDefinition { Height = barIndex == 0 ? bar : rest });
			LayoutRoot.RowDefinitions.Add(new RowDefinition { Height = barIndex == 0 ? rest : bar });
		}
		Grid.SetRow(TitleBar, vertical ? 0 : barIndex);
		Grid.SetColumn(TitleBar, vertical ? barIndex : 0);
		Grid.SetRow(ContentHost, vertical ? 0 : 1 - barIndex);
		Grid.SetColumn(ContentHost, vertical ? 1 - barIndex : 0);
		TitleContent.LayoutTransform = vertical ? new RotateTransform(90) : Transform.Identity;
		TitleContent.Margin = vertical ? new Thickness(0, 10, 0, 4) : new Thickness(10, 0, 4, 0);
		UpdateTabs();
	}

	/// <summary>
	/// 合并成标签页时显示标签条，否则标题栏竖放时显示竖排标题、横放时显示横排标题；改名期间都不显示。
	/// </summary>
	void ShowTitleText(bool show)
	{
		bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
		bool tabbed = TabStrip.ItemsSource != null;
		TabStrip.Visibility = !tabbed ? Visibility.Collapsed : show ? Visibility.Visible : Visibility.Hidden;
		TitleText.Visibility = tabbed || vertical ? Visibility.Collapsed : show ? Visibility.Visible : Visibility.Hidden;
		VerticalTitleText.Visibility = !tabbed && vertical && show ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// 按所在的标签组刷新标题栏：在组里时显示各成员分区的标签，自己的那个高亮；不在组里时显示标题。
	/// </summary>
	public void UpdateTabs()
	{
		var tabs = _manager.TabsOf(this);
		bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
		bool fill = Settings.TabsFillWidth;
		TabStripPanel.SetFill(TabStrip, fill);
		TabStripPanel.SetAlignRight(TabStrip, Settings.TabAlignment == TabAlignment.Right);
		TabStrip.ItemsSource = tabs.Count > 1 ? tabs.Select(window => new FenceTab(window, window == this, vertical, fill)).ToList() : null;
		ShowTitleText(TitleEditor.Visibility != Visibility.Visible);
		UpdateBadges();
	}

	/// <summary>
	/// 鼠标事件发生在哪个标签上；不在标签上时返回 null。
	/// </summary>
	static FenceTab? TabAt(object source)
	{
		for (var element = source as DependencyObject; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
		{
			if (element is FrameworkElement { DataContext: FenceTab tab })
			{
				return tab;
			}
		}
		return null;
	}

	/// <summary>
	/// 鼠标沿标签条落在第几个标签的位置（超出两端算第一个或最后一个），按住 Shift 拖动标签时据此调整顺序；
	/// 标签刚重排、还没排好版时返回 -1。
	/// </summary>
	int TabIndexAt(POINT cursor)
	{
		bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
		int count = TabStrip.Items.Count;
		for (int i = 0; i < count; i++)
		{
			if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { IsLoaded: true } container)
			{
				return -1;
			}
			// 竖放的标题栏整体旋转了 90°，两角换算到屏幕上后取靠后的一端
			var a = container.PointToScreen(new Point(0, 0));
			var b = container.PointToScreen(new Point(container.ActualWidth, container.ActualHeight));
			double end = vertical ? Math.Max(a.Y, b.Y) : Math.Max(a.X, b.X);
			if ((vertical ? cursor.Y : cursor.X) < end)
			{
				return i;
			}
		}
		return count - 1;
	}

	/// <summary>
	/// 拖出去拆开时的展开范围（物理像素）：标题栏中间对着鼠标，收进鼠标所在显示器的工作区。
	/// </summary>
	public RECT DetachedBoundsAt(POINT cursor)
	{
		var (work, scale) = GetMonitorWorkArea(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
		int left = Math.Clamp(cursor.X - Model.Width / 2, work.Left, Math.Max(work.Left, work.Right - Model.Width));
		int top = Math.Clamp(cursor.Y - (int)Math.Round(TitleBarDip / 2 * scale), work.Top, Math.Max(work.Top, work.Bottom - Model.Height));
		return new RECT(left, top, left + Model.Width, top + Model.Height);
	}

	/// <summary>
	/// 切换标签时接替当前显示的标签：同样的位置和悬停展开状态。先用 DWM 藏着（cloak）显示出来、排到它正下方，
	/// 等画好后由 TakeOverFrom 在同一次屏幕刷新里换手：分区多是半透明的，两个叠着显示时下面那个会透出来，切换时会闪。
	/// </summary>
	public async Task ShowInPlaceOf(FenceWindow current)
	{
		_tempExpanded = current._tempExpanded;
		// 标题栏按钮也接着显示：鼠标停在标题栏上切换时，新标签要等鼠标再动才收到 MouseEnter，否则按钮先消失再出现，看上去闪一下
		if (current.TitleButtons.Opacity > 0)
		{
			ShowTitleButtons(true);
			RestartTimer(_buttonsTimer);
		}
		ApplyBounds();
		SetCloaked(true);
		try
		{
			Show();
			// 不显示期间缩放比例变过的话，按现在的比例重新摆放
			if (HasStaleDpi())
			{
				RefreshDpi();
			}
			DesktopHost.PlaceBelow(_hwnd, current.Handle);
			// 第一帧排版，第二帧图标画出来，再等一帧送到屏幕上
			await FlyingIcon.NextFrame();
			await FlyingIcon.NextFrame();
			await FlyingIcon.NextFrame();
		}
		catch
		{
			SetCloaked(false);
			throw;
		}
		if (_tempExpanded)
		{
			RestartTimer(_collapseTimer);
		}
	}

	/// <summary>
	/// 和 current 在同一次屏幕刷新里换手：自己显示出来，current 藏起来（之后由调用方隐藏它、取消 cloak）。
	/// </summary>
	public void TakeOverFrom(FenceWindow current)
	{
		DwmFlush();
		SetCloaked(false);
		current.SetCloaked(true);
	}

	/// <summary>
	/// 拖动分区经过别的分区标题栏时，标出松手会合并进去的那个。
	/// </summary>
	void SetMergeTarget(FenceWindow? target)
	{
		if (target == _mergeTarget)
		{
			return;
		}
		_mergeTarget?.ShowMergeHint(false);
		_mergeTarget = target;
		target?.ShowMergeHint(true);
	}

	/// <summary>
	/// 别的分区拖到本分区标题栏上时：边框高亮，横放的标题栏上提示松手合并（竖放的标题栏太窄，只高亮边框）。
	/// </summary>
	public void ShowMergeHint(bool on)
	{
		SetDropHighlight(on);
		bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
		MergeHint.Visibility = on && !vertical ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// 竖排标题：每个字单独一行，去掉空格，子文件夹路径的分隔符换成圆点。
	/// </summary>
	static string ToVerticalText(string text)
	{
		var characters = new List<string>();
		var elements = StringInfo.GetTextElementEnumerator(text.Replace("›", "·"));
		while (elements.MoveNext())
		{
			var element = elements.GetTextElement();
			if (!string.IsNullOrWhiteSpace(element))
			{
				characters.Add(element);
			}
		}
		return string.Join("\n", characters);
	}

	public void ApplyAppearance()
	{
		var baseColor = Appearance.ParseColor(Model.Color ?? Settings.DefaultColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
		double opacity = Math.Clamp(Model.Opacity ?? Settings.DefaultOpacity, 0.05, 1);
		// 浅色且足够不透明的背景上改用深色文字
		bool light = Appearance.Luminance(baseColor) > 0.6 && opacity >= 0.35;
		Frame.Background = Frozen(Color.FromArgb((byte)Math.Round(opacity * 255), baseColor.R, baseColor.G, baseColor.B));
		_frameBorder = Frozen(light ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
		Frame.BorderBrush = _frameBorder;
		Frame.CornerRadius = new CornerRadius(Settings.CornerRadius);
		UpdateBlur();
		TitleBar.Background = Frozen(light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x30, 0, 0, 0));
		Resources["FenceForeground"] = Frozen(light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White);
		Resources["FenceHover"] = Frozen(light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
		Resources["FenceSelected"] = Frozen(light ? Color.FromArgb(0x1E, 0, 0, 0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
		Resources["FenceSelectedBorder"] = Frozen(light ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
		Resources["TextShadow"] = Settings.TextShadow && !light ? TextShadowEffect : null;
		UpdateRollVisuals();
	}

	/// <summary>
	/// 毛玻璃：背景颜色下面铺一层模糊后的壁纸，截取分区在屏幕上所在的那一块；关掉毛玻璃或取不到壁纸时只有背景颜色，即半透明。
	/// </summary>
	void UpdateBlur()
	{
		var image = _manager.Wallpaper.Image;
		if (!(Model.Blur ?? Settings.DefaultBlur) || image == null)
		{
			_blurBrush = null;
			BlurLayer.Background = null;
			return;
		}
		BlurLayer.CornerRadius = Frame.CornerRadius;
		if (_blurBrush?.ImageSource != image)
		{
			_blurBrush = new ImageBrush(image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
			BlurLayer.Background = _blurBrush;
			UpdateBlurViewbox();
			// 刚取到或换了壁纸时淡入，和系统换壁纸的过渡差不多，不会突然跳变
			BlurLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(BlurFadeInMilliseconds)));
			return;
		}
		UpdateBlurViewbox();
	}

	/// <summary>
	/// 分区移动或改变大小后，毛玻璃改截取新位置上的那一块壁纸；拖动过程中不更新，见 HideBlurWhileMoving。
	/// </summary>
	void UpdateBlurViewbox()
	{
		if (_blurBrush != null && !_blurHidden && _hwnd != IntPtr.Zero)
		{
			_blurBrush.Viewbox = _manager.Wallpaper.ViewboxFor(GetBounds());
		}
	}

	/// <summary>
	/// 拖动或调整大小时位置每一步都在变，背景要等界面重绘后才跟上，看上去慢半拍；
	/// 所以一开始移动就把毛玻璃淡出，只剩半透明，后面的壁纸实时透出，松手后再按新位置淡入。只单击标题栏不移动时不淡出。
	/// </summary>
	void HideBlurWhileMoving()
	{
		if (_blurBrush == null || _blurHidden)
		{
			return;
		}
		_blurHidden = true;
		BlurLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(BlurFadeOutMilliseconds)));
	}

	void ShowBlurAfterMoving()
	{
		if (!_blurHidden)
		{
			return;
		}
		_blurHidden = false;
		UpdateBlurViewbox();
		BlurLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(BlurFadeInMilliseconds)));
	}

	public void ApplyViewMode()
	{
		var view = Model.View;
		double iconDip = EffectiveIconSize.ToDip();
		Resources["IconSize"] = iconDip;
		// 不显示名称时格子只留出图标和一点边距
		Resources["CellWidth"] = Settings.ShowIconNames ? iconDip + 44 : iconDip + 16;
		Resources["IconNameVisibility"] = Settings.ShowIconNames ? Visibility.Visible : Visibility.Collapsed;
		// 详细信息视图和不显示名称的图标视图不展开名称
		if (!CanExpandNames && _expandedName != null)
		{
			_expandedName.IsNameExpanded = false;
			_expandedName = null;
		}
		var (panel, template) = view switch
		{
			FenceView.Icons => ("IconsPanel", "IconTemplate"),
			FenceView.List => ("ListPanel", "ListTemplate"),
			FenceView.Details => ("DetailsPanel", "DetailsTemplate"),
		};
		ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(panel);
		ItemsList.ItemTemplate = (DataTemplate)FindResource(template);
		ItemsList.ItemContainerStyle = (Style)FindResource(view == FenceView.Icons ? "FenceItemContainer" : "FenceListItemContainer");
		DetailsHeader.Visibility = view == FenceView.Details ? Visibility.Visible : Visibility.Collapsed;
		UpdateDetailsColumns();
		UpdateDetailsHeader();
		foreach (var item in _items)
		{
			RequestIcon(item);
		}
	}

	/// <summary>
	/// 选中时展开完整名称：列表视图总是展开，图标视图要显示名称才展开，详细信息视图和资源管理器一样不展开。
	/// </summary>
	bool CanExpandNames => Model.View switch
	{
		FenceView.Icons => Settings.ShowIconNames,
		FenceView.List => true,
		FenceView.Details => false,
	};

	/// <summary>
	/// 详细信息视图的列宽：表头和每一行一样宽（不含滚动条）；名称至少留 DetailsNameMinWidth，放不下时从右往左依次不显示大小、类型、修改日期。
	/// </summary>
	void UpdateDetailsColumns()
	{
		if (Model.View != FenceView.Details)
		{
			return;
		}
		_scroller ??= FindDescendant<ScrollViewer>(ItemsList);
		if (_scroller == null)
		{
			return;
		}
		double width = _scroller.ViewportWidth;
		DetailsHeader.Width = width;
		// 每一行左右各有 5（选中框的边框和内边距），名称前面是 30 宽的图标
		double rest = width - 10 - 30 - DetailsNameMinWidth;
		bool date = rest >= DetailsDateWidth;
		bool type = date && rest >= DetailsDateWidth + DetailsTypeWidth;
		bool size = type && rest >= DetailsDateWidth + DetailsTypeWidth + DetailsSizeWidth;
		SetColumnWidth("DetailsDateWidth", date ? DetailsDateWidth : 0);
		SetColumnWidth("DetailsTypeWidth", type ? DetailsTypeWidth : 0);
		SetColumnWidth("DetailsSizeWidth", size ? DetailsSizeWidth : 0);
	}

	void SetColumnWidth(string key, double width)
	{
		if (Resources[key] is not GridLength current || current.Value != width)
		{
			Resources[key] = new GridLength(width);
		}
	}

	/// <summary>
	/// 表头上按哪一列排序就在列名旁标出升序或降序。
	/// </summary>
	void UpdateDetailsHeader()
	{
		foreach (var (glyph, field) in new[] { (NameSortGlyph, SortField.Name), (ModifiedSortGlyph, SortField.Modified), (TypeSortGlyph, SortField.Type), (SizeSortGlyph, SortField.Size) })
		{
			glyph.Text = Model.SortBy != field ? string.Empty : Model.SortDescending ? "\ue70d" : "\ue70e";
		}
	}

	/// <summary>
	/// 和资源管理器一样点表头按这一列排序，再点一次反过来。
	/// </summary>
	void DetailsHeaderCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement { Tag: SortField field })
		{
			SetSort(field, Model.SortBy == field && !Model.SortDescending);
			e.Handled = true;
		}
	}

	public void UpdateTitle()
	{
		// 进入子文件夹后标题带上相对路径，如「下载 › 图片 › 2024」
		TitleText.Text = _subFolder == null ? Model.Title : $"{Model.Title} › {Path.GetRelativePath(Model.FolderPath, _subFolder).Replace("\\", " › ")}";
		TitleText.ToolTip = Model.IsPortal ? CurrentFolder : null;
		VerticalTitleText.Text = ToVerticalText(TitleText.Text);
		VerticalTitleText.ToolTip = TitleText.ToolTip;
		BackButton.Visibility = _subFolder == null ? Visibility.Collapsed : Visibility.Visible;
		UpdateBadges();
	}

	/// <summary>
	/// 标题栏左侧的角标：映射分区的文件夹图标、锁定。合并成标签页后文件夹图标改到各自的标签上，这里只剩整组一致的锁定。
	/// </summary>
	void UpdateBadges()
	{
		bool portal = Model.IsPortal && TabStrip.ItemsSource == null;
		PortalIcon.Source = portal ? ShellIconLoader.FolderIcon : null;
		PortalIcon.Visibility = portal ? Visibility.Visible : Visibility.Collapsed;
		// 竖放的标题栏整体转了 90°，图标转回来保持正立
		PortalIcon.LayoutTransform = _titleEdge is RollEdge.Left or RollEdge.Right ? new RotateTransform(-90) : Transform.Identity;
		var badges = new List<string>();
		if (Model.Locked)
		{
			badges.Add("");
		}
		BadgeText.Text = string.Join(" ", badges);
		BadgeText.Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	public void ApplyLockState() => UpdateTitle();

	void SetDropHighlight(bool on)
	{
		Frame.BorderBrush = on ? Frozen(Color.FromArgb(0xE6, 0x60, 0xA5, 0xFA)) : _frameBorder;
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		foreach (var item in _items)
		{
			RequestIcon(item);
		}
	}

	/// <summary>
	/// 窗口的 DPI 与所在显示器的不一致：缩放比例变化后分区窗口有时收不到通知，一直停在旧的 DPI。
	/// </summary>
	public bool HasStaleDpi() => GetDpiForRect(GetBounds()) != GetDpiForWindow(_hwnd);

	/// <summary>
	/// 缩放比例变化后按新的 DPI 显示：停在旧 DPI 的窗口改一下大小，系统就会按所在显示器重新判断
	/// （实测悬停展开一次即恢复）；最后按保存的位置和大小重新摆放，标题栏高度按新比例计算。
	/// </summary>
	public void RefreshDpi()
	{
		if (HasStaleDpi())
		{
			var rect = GetBounds();
			SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width + 1, rect.Height + 1, SWP_NOZORDER | SWP_NOACTIVATE);
		}
		ApplyBounds();
	}

	static SolidColorBrush Frozen(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	static DropShadowEffect CreateShadow()
	{
		var effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.75, Color = Colors.Black, Direction = 270 };
		effect.Freeze();
		return effect;
	}

	#endregion

	#region 悬停展开、标题栏按钮、淡入淡出

	protected override void OnMouseEnter(MouseEventArgs e)
	{
		base.OnMouseEnter(e);
		// 悬停时立即出现，移开后延迟消失
		_buttonsTimer.Stop();
		_collapseTimer.Stop();
		ShowTitleButtons(true);
		if (Model.RolledUp && !_tempExpanded && Settings.ExpandOnHover)
		{
			_tempExpanded = true;
			// 展开的部分盖在旁边的分区上面
			_manager.BringToFront(this);
			ApplyRollBounds();
		}
	}

	protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseDown(e);
		_manager.BringToFront(this);
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		base.OnMouseLeave(e);
		RestartTimer(_buttonsTimer);
		if (_tempExpanded)
		{
			RestartTimer(_collapseTimer);
		}
	}

	protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseUp(e);
		// 鼠标侧键「后退」返回上一级文件夹
		if (e.ChangedButton == MouseButton.XButton1 && _subFolder != null)
		{
			NavigateUp();
			e.Handled = true;
		}
	}

	void CollapseTimer_Tick(object? sender, EventArgs e)
	{
		_collapseTimer.Stop();
		if (!_tempExpanded)
		{
			return;
		}
		// 鼠标仍在窗口内、正在移动或调整大小（吸附时边框与鼠标会错开）、菜单打开、拖放或重命名进行中时保持展开，稍后再检查
		if (GetBounds().Contains(NativeMethods.GetCursorPos()) || _inSizeMove || _menuOpen || _dragInside || IsEditing())
		{
			RestartTimer(_collapseTimer);
			return;
		}
		_tempExpanded = false;
		ApplyRollBounds();
	}

	static void RestartTimer(DispatcherTimer timer)
	{
		timer.Stop();
		timer.Start();
	}

	void ShowTitleButtons(bool visible)
	{
		var animation = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 0 : 200));
		TitleButtons.BeginAnimation(OpacityProperty, animation);
	}

	bool IsEditing() => TitleEditor.IsVisible || _items.Any(i => i.IsRenaming);

	public void FadeOut()
	{
		var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMilliseconds));
		animation.Completed += (_, _) =>
		{
			// 淡出过程中可能又被要求显示
			if (_manager.FencesHidden)
			{
				Hide();
			}
		};
		BeginAnimation(OpacityProperty, animation);
	}

	public void FadeIn()
	{
		if (_manager.IsHiddenTab(this))
		{
			// 不显示的标签也跟着淡出过，不透明度停在 0：撤掉，否则之后切换到它时整个分区是透明的
			BeginAnimation(OpacityProperty, null);
			Opacity = 1;
			return;
		}
		if (!IsVisible)
		{
			BeginAnimation(OpacityProperty, null);
			Opacity = 0;
			Show();
			PlaceAboveDesktop();
		}
		BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeMilliseconds)));
	}

	#endregion

	#region 内容加载

	public void OnFolderChanged()
	{
		if (_allowClose)
		{
			return;
		}
		// 映射的文件夹换了，或进入的子文件夹被删除、改名，回到映射的文件夹
		if (_subFolder != null && (!PathUtil.IsUnder(_subFolder, Model.FolderPath) || !Directory.Exists(_subFolder)))
		{
			_subFolder = null;
			UpdateTitle();
		}
		// 桌面分区的内容来自桌面视图，由管理器在桌面变化时刷新，不需要监视文件夹
		if (Model.IsPortal)
		{
			StartWatcher();
		}
		else
		{
			StopWatcher();
		}
		RefreshItems();
	}

	/// <summary>
	/// 映射的文件夹被外部删除或恢复时刷新（由管理器定时调用）。
	/// </summary>
	public void CheckFolder()
	{
		if (Model.IsPortal && Directory.Exists(CurrentFolder) != _folderExisted)
		{
			OnFolderChanged();
		}
	}

	/// <summary>
	/// 文件操作之后稍后刷新：桌面分区要等资源管理器更新桌面视图，映射分区直接重读文件夹。
	/// </summary>
	public void ScheduleRefresh()
	{
		if (_allowClose)
		{
			return;
		}
		if (IsDesktopFence)
		{
			_manager.RefreshDesktopSoon();
			return;
		}
		_refreshTimer.Stop();
		_refreshTimer.Start();
	}

	public void RefreshItems()
	{
		if (_allowClose)
		{
			return;
		}
		var fresh = IsDesktopFence ? LoadMembers() : LoadFolder();
		Sort(fresh);
		SyncItems(fresh);
		foreach (var item in fresh)
		{
			item.IsInFlight = _arriving.Contains(item.FullPath);
			RequestIcon(item);
		}
		ApplyCutState();
		EmptyHint.Text = IsDesktopFence ? "把桌面图标拖到这里" : _folderExisted ? "拖放文件到这里" : $"文件夹不存在\n{CurrentFolder}";
		EmptyHint.Visibility = fresh.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// 桌面分区：按成员列表从桌面视图中取出项目，显示名与桌面上的一致；暂时不在桌面上的成员不显示。
	/// </summary>
	List<FenceItem> LoadMembers()
	{
		var desktop = _manager.DesktopItems;
		if (desktop?.Snapshot == null)
		{
			// 还没读到桌面视图（资源管理器未就绪），保留现有内容
			return _items.ToList();
		}
		var existing = new Dictionary<string, FenceItem>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in _items)
		{
			existing.TryAdd(item.FullPath, item);
		}
		var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fresh = new List<FenceItem>();
		foreach (var key in Model.Members)
		{
			if (desktop.Find(key) is not DesktopEntry entry || !added.Add(entry.Key))
			{
				continue;
			}
			// 仅大小写不同的重命名会命中旧条目，此时按新名称重建
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
			fresh.Add(item);
		}
		return fresh;
	}

	List<FenceItem> LoadFolder()
	{
		var folder = CurrentFolder;
		_folderExisted = Directory.Exists(folder);
		var existing = _items.ToDictionary(i => i.FullPath, StringComparer.OrdinalIgnoreCase);
		var fresh = new List<FenceItem>();
		if (!_folderExisted)
		{
			return fresh;
		}
		bool showHidden = Settings.ShowHiddenFiles;
		bool showProtected = showHidden && DesktopHost.ShowsProtectedFiles();
		try
		{
			foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
			{
				// 和资源管理器一样只看隐藏属性，只带系统属性的照常显示；同时带系统属性的是 desktop.ini 这类受保护的文件，还要资源管理器设置了显示它们
				var attributes = info.Attributes;
				if ((attributes & FileAttributes.Hidden) != 0 && ((attributes & FileAttributes.System) != 0 ? !showProtected : !showHidden))
				{
					continue;
				}
				// 仅大小写不同的重命名会命中旧条目，此时按新名称重建
				if (existing.TryGetValue(info.FullName, out var item) && string.Equals(item.FullPath, info.FullName, StringComparison.Ordinal))
				{
					if (item.Refresh(info))
					{
						item.IconPixelSize = 0;
					}
				}
				else
				{
					item = FenceItem.Create(info);
				}
				fresh.Add(item);
			}
		}
		catch (Exception ex)
		{
			Log.Warn($"读取分区文件夹失败：{folder}", ex);
		}
		return fresh;
	}

	/// <summary>
	/// 剪贴板里被剪切的图标半透明显示。
	/// </summary>
	public void ApplyCutState()
	{
		foreach (var item in _items)
		{
			item.IsCut = _manager.IsCut(item.FullPath);
		}
	}

	/// <summary>
	/// 把桌面项目加入本分区（已从其他分区移出），新成员排在自定义顺序的最后。
	/// </summary>
	public void AddMembers(IEnumerable<string> keys)
	{
		foreach (var key in keys)
		{
			if (!Model.Members.Contains(key, StringComparer.OrdinalIgnoreCase))
			{
				Model.Members.Add(key);
			}
		}
		RefreshItems();
	}

	public FenceItem? FindItem(string key) => _items.FirstOrDefault(i => string.Equals(i.FullPath, key, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 一键整理时这些图标将从桌面飞来：归入本分区后先不显示，飞到时再由 CompleteArrival 显示。
	/// </summary>
	public void ExpectArrivals(IEnumerable<string> keys) => _arriving.UnionWith(keys);

	public void CompleteArrival(string key)
	{
		_arriving.Remove(key);
		if (FindItem(key) is { } item)
		{
			item.IsInFlight = false;
		}
	}

	/// <summary>
	/// 只留下这些还要飞来的图标，其余等着飞来的都直接显示（启动动画里没有起点或图像的图标不飞）。
	/// </summary>
	public void KeepArrivals(IEnumerable<string> keys)
	{
		_arriving.IntersectWith(keys);
		foreach (var item in _items)
		{
			item.IsInFlight = _arriving.Contains(item.FullPath);
		}
	}

	/// <summary>
	/// 图标图像在屏幕上的位置（物理像素），一键整理动画的终点、删除分区动画的起点；分区没显示、已卷起，或图标不在可见范围内（分区小了要滚动）时返回 null。
	/// </summary>
	public RECT? GetIconRect(string key)
	{
		if (!IsVisible || IsCollapsed || FindItem(key) is not { } item
				|| ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container
				|| ItemOps.FindChild<Image>(container, "IconImage") is not { IsVisible: true } image)
		{
			return null;
		}
		var rect = ItemOps.ScreenRect(image);
		var viewport = ItemOps.ScreenRect(ContentHost);
		bool inside = rect.Left >= viewport.Left && rect.Top >= viewport.Top && rect.Right <= viewport.Right && rect.Bottom <= viewport.Bottom;
		return inside ? rect : null;
	}

	/// <summary>
	/// 自定义顺序中记录项目的方式：桌面分区记完整解析名（用户桌面和公共桌面可能有同名文件），映射分区记文件名。
	/// </summary>
	string OrderKey(FenceItem item) => IsDesktopFence ? item.FullPath : item.FileName;

	string OrderKey(string key) => IsDesktopFence ? key : Path.GetFileName(key);

	void Sort(List<FenceItem> items)
	{
		// 自定义顺序只记映射的文件夹本身，进入子文件夹后按名称排列
		var sortBy = Model.SortBy == SortField.Custom && _subFolder != null ? SortField.Name : Model.SortBy;
		bool custom = sortBy == SortField.Custom;
		var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; custom && i < Model.CustomOrder.Count; i++)
		{
			rank.TryAdd(Model.CustomOrder[i], i);
		}
		// 自定义顺序里没有的（新加入的）项目排在最后
		int Rank(FenceItem item) => rank.TryGetValue(OrderKey(item), out int index) ? index : int.MaxValue;
		Comparison<FenceItem> byField = sortBy switch
		{
			SortField.Name => (a, b) => StrCmpLogicalW(a.DisplayName, b.DisplayName),
			SortField.Type => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
			SortField.Size => (a, b) => a.Size.CompareTo(b.Size),
			SortField.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
			SortField.Custom => (a, b) => Rank(a).CompareTo(Rank(b)),
		};
		int direction = !custom && Model.SortDescending ? -1 : 1;
		items.Sort((a, b) =>
		{
			// 与资源管理器一致：文件夹始终排在文件前面（自定义顺序完全按用户的摆放）
			if (!custom && a.IsFolder != b.IsFolder)
			{
				return a.IsFolder ? -1 : 1;
			}
			int result = byField(a, b);
			if (result == 0)
			{
				result = StrCmpLogicalW(a.DisplayName, b.DisplayName);
			}
			return result * direction;
		});
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

	/// <summary>
	/// 重新加载全部图标，加载完成前保留旧图标。
	/// </summary>
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
		int pixels = (int)Math.Round(IconDip * ScaleFactor);
		if (item.IconPixelSize == pixels)
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

	void StartWatcher()
	{
		StopWatcher();
		var folder = CurrentFolder;
		if (!Directory.Exists(folder))
		{
			return;
		}
		try
		{
			var watcher = new FileSystemWatcher(folder)
			{
				IncludeSubdirectories = false,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
			};
			watcher.Created += OnFolderEvent;
			watcher.Deleted += OnFolderEvent;
			watcher.Changed += OnFolderEvent;
			watcher.Renamed += OnFolderEvent;
			watcher.Error += (_, _) => Dispatcher.InvokeAsync(OnFolderChanged);
			watcher.EnableRaisingEvents = true;
			_watcher = watcher;
		}
		catch (Exception ex)
		{
			Log.Warn($"监视分区文件夹失败：{folder}", ex);
		}
	}

	public void StopWatcher()
	{
		_watcher?.Dispose();
		_watcher = null;
	}

	void OnFolderEvent(object sender, FileSystemEventArgs e) => Dispatcher.InvokeAsync(ScheduleRefresh);

	#endregion

	#region 鼠标：选择、框选、拖出、右键

	/// <summary>
	/// 在分区里按下鼠标（任意键，含空白处）时单选，取消桌面和其他分区的选择。按着 Ctrl/Shift 是追加选择，不取消；
	/// 按在已选中的图标上时先保留整组选择（可能要一起拖动或弹出它们的右键菜单），左键没拖动的话抬起时再只选它。
	/// </summary>
	void ItemsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (IsAdditive || IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		if (ItemFromSource(e.OriginalSource) is FenceItem item && ItemsList.SelectedItems.Contains(item))
		{
			return;
		}
		_manager.OnSelectionScopeActivated(this);
	}

	/// <summary>
	/// 不是鼠标单击造成的选中（方向键、拖入后选中新项目等）同样是单选，取消别处的选择；按着 Ctrl/Shift 时是追加。
	/// </summary>
	void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		_expandedName = ItemOps.ExpandSelectedName(ItemsList, e, _expandedName, CanExpandNames);
		if (e.AddedItems.Count > 0 && !IsAdditive)
		{
			_manager.OnSelectionScopeActivated(this);
		}
	}

	void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		CommitAllRenames();
		var item = ItemFromSource(e.OriginalSource);
		_clickRename.Press(e, item, _manager.AllSelectedItems() is [var only] && only == item);
		_pressPoint = e.GetPosition(ContentHost);
		_pressedItem = item;
		_deferSelection = false;

		if (item == null)
		{
			BeginRubberBand(false);
			e.Handled = true;
			return;
		}
		if (e.ClickCount >= 2)
		{
			if (ItemsList.SelectedItems.Contains(item))
			{
				OpenSelected();
			}
			else
			{
				OpenItems([item]);
			}
			_pressedItem = null;
			e.Handled = true;
			return;
		}
		if (ItemsList.SelectedItems.Contains(item) && Keyboard.Modifiers == ModifierKeys.None)
		{
			// 在已选中的项上按下时先不改选择，便于拖动整组文件；没有拖动的话抬起时再单选它
			_deferSelection = true;
			FocusItem(item);
			e.Handled = true;
		}
	}

	void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (_rubberBand)
		{
			if ((_rubberRight ? e.RightButton : e.LeftButton) == MouseButtonState.Pressed)
			{
				UpdateRubberBand(e.GetPosition(ContentHost));
			}
			else
			{
				EndRubberBand();
			}
			e.Handled = true;
			return;
		}
		if (_pressedItem == null)
		{
			return;
		}
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			_pressedItem = null;
			return;
		}
		// 屏蔽列表自带的"按住拖动扩选"，改为拖出文件
		e.Handled = true;
		var position = e.GetPosition(ContentHost);
		if (!ExceedsDragThreshold(position))
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
		var (items, desktop) = _manager.SelectionForShell(this);
		StartDragOut(items, desktop, item);
	}

	void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_rubberBand)
		{
			EndRubberBand();
			e.Handled = true;
			return;
		}
		if (_deferSelection && _pressedItem != null)
		{
			_manager.OnSelectionScopeActivated(this);
			ItemsList.UnselectAll();
			ItemsList.SelectedItem = _pressedItem;
		}
		// 没有拖动、抬起时还在按下的图标上：单击的是已选中图标的名称时，过一会儿开始改名
		_clickRename.Release(_pressedItem);
		_deferSelection = false;
		_pressedItem = null;
	}

	/// <summary>
	/// 和资源管理器一样，在空白处按住右键拖动也是框选，松开时弹出右键菜单（见 ItemsList_MouseRightButtonUp）。
	/// </summary>
	void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (_rubberBand || IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource) || ItemFromSource(e.OriginalSource) != null)
		{
			return;
		}
		CommitAllRenames();
		_pressPoint = e.GetPosition(ContentHost);
		BeginRubberBand(true);
		e.Handled = true;
	}

	void ItemsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		_clickRename.Cancel();
		var point = NativeMethods.GetCursorPos();
		if (_rubberBand && _rubberRight)
		{
			bool dragged = _rubberShown;
			EndRubberBand();
			// 拖出了选框：框住了图标就弹出它们的右键菜单，没框住就弹出分区菜单；只是单击时照常处理
			if (dragged)
			{
				if (ItemsList.SelectedItems.Count > 0)
				{
					var (selected, onDesktop) = _manager.SelectionForShell(this);
					ShowItemMenu(selected, onDesktop, point, (FenceItem)ItemsList.SelectedItems[0]!);
				}
				else
				{
					ShowFenceMenu(point);
				}
				e.Handled = true;
				return;
			}
		}
		if (IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		CommitAllRenames();
		var item = ItemFromSource(e.OriginalSource);
		if (item == null)
		{
			ItemsList.UnselectAll();
			ShowFenceMenu(point);
		}
		else
		{
			if (!ItemsList.SelectedItems.Contains(item))
			{
				ItemsList.UnselectAll();
				ItemsList.SelectedItem = item;
			}
			var (items, desktop) = _manager.SelectionForShell(this);
			ShowItemMenu(items, desktop, point, item);
		}
		e.Handled = true;
	}

	/// <param name="right">按住的是右键。</param>
	void BeginRubberBand(bool right)
	{
		if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
		{
			ItemsList.UnselectAll();
		}
		_rubberBase = ItemsList.SelectedItems.Cast<FenceItem>().ToHashSet();
		_rubberBand = true;
		_rubberRight = right;
		ItemsList.Focus();
		ItemsList.CaptureMouse();
	}

	void UpdateRubberBand(Point current)
	{
		if (!_rubberShown)
		{
			if (!ExceedsDragThreshold(current))
			{
				return;
			}
			_rubberShown = true;
		}
		var rect = new Rect(_pressPoint, current);
		// 选框画在单独的小窗口里，不让整个分区跟着鼠标重画（见 DrawFrameWindow）；伸出放图标区域的部分不画
		var area = ItemOps.ScreenRect(ContentHost);
		var from = ContentHost.PointToScreen(rect.TopLeft);
		var to = ContentHost.PointToScreen(rect.BottomRight);
		_marquee ??= DrawFrameWindow.CreateMarquee();
		_marquee.ShowAt(new RECT(Math.Max(area.Left, (int)Math.Round(from.X)), Math.Max(area.Top, (int)Math.Round(from.Y)),
				Math.Min(area.Right, (int)Math.Round(to.X)), Math.Min(area.Bottom, (int)Math.Round(to.Y))));
		foreach (var item in _items)
		{
			if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible)
			{
				continue;
			}
			var bounds = container.TransformToAncestor(ContentHost).TransformBounds(new Rect(container.RenderSize));
			bool selected = rect.IntersectsWith(bounds) || _rubberBase.Contains(item);
			if (container.IsSelected != selected)
			{
				container.IsSelected = selected;
			}
		}
	}

	void EndRubberBand()
	{
		_rubberBand = false;
		_rubberShown = false;
		_marquee?.Hide();
		ItemsList.ReleaseMouseCapture();
	}

	bool ExceedsDragThreshold(Point position)
	{
		return Math.Abs(position.X - _pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
				|| Math.Abs(position.Y - _pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
	}

	/// <param name="items">拖动的图标，可能包含其他分区和桌面上一起选中的。</param>
	/// <param name="desktop">它们是不是桌面上的图标。</param>
	/// <param name="anchor">鼠标按下的那一项，拖动预览图按它生成。</param>
	void StartDragOut(List<FenceItem> items, bool desktop, FenceItem anchor)
	{
		if (items.Count == 0)
		{
			return;
		}
		Mouse.Capture(null);
		var image = ItemOps.CreateDragImage(ItemsList.ItemContainerGenerator.ContainerFromItem(anchor) as ListBoxItem, items.Count, ScaleFactor);
		ItemOps.DragOut(_hwnd, items, desktop, this, image);
		ScheduleRefresh();
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

	static bool IsInside<T>(object source) where T : DependencyObject => ItemOps.IsInside<T>(source);

	#endregion

	#region 标题栏

	/// <summary>
	/// 拖标题栏（包括标签）移动整个分区或整组标签；没有拖动、只是点了别的标签时切过去。
	/// 按住 Shift 拖标签时改为调整标签顺序，拖出标题栏就拆成独立分区。
	/// </summary>
	void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (TitleEditor.IsVisible)
		{
			return;
		}
		CommitAllRenames();
		if (e.ClickCount == 2)
		{
			ToggleRollUp();
			e.Handled = true;
			return;
		}
		e.Handled = true;
		var tab = TabAt(e.OriginalSource);
		if (tab != null && !Model.Locked && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
		{
			_draggedTab = tab;
			_tabDetaching = false;
			TitleBar.CaptureMouse();
			return;
		}
		if (!Model.Locked)
		{
			var start = GetBounds();
			try
			{
				DragMove();
			}
			catch (InvalidOperationException)
			{
				// 鼠标已松开时 DragMove 会抛异常，忽略即可
			}
			if (!GetBounds().Equals(start))
			{
				return;
			}
		}
		if (tab != null && tab.Window != this)
		{
			_ = _manager.ActivateTabAsync(tab.Window, true);
		}
	}

	void TitleBar_MouseMove(object sender, MouseEventArgs e)
	{
		if (_draggedTab is not FenceTab tab)
		{
			HoverTabByMouse(e);
			return;
		}
		var cursor = NativeMethods.GetCursorPos();
		// 离开标题栏超过一个标题栏的高度就是要拆出去，预览拆开后的位置
		int slack = (int)Math.Round(TitleBarDip * ScaleFactor);
		_tabDetaching = !TitleBarRect().Inflate(slack).Contains(cursor);
		if (_tabDetaching)
		{
			_detachPreview ??= DrawFrameWindow.CreateFrame();
			_detachPreview.ShowAt(tab.Window.LayoutBoundsOf(tab.Window.DetachedBoundsAt(cursor)));
			return;
		}
		_detachPreview?.Hide();
		if (TabIndexAt(cursor) is int index and >= 0)
		{
			_manager.MoveTab(tab.Window, index);
		}
	}

	/// <summary>
	/// 开了「悬停切换标签」时，鼠标停在别的标签上一会儿就切过去；移到别处重新计时，离开标题栏不切。
	/// </summary>
	void HoverTabByMouse(MouseEventArgs e)
	{
		var hovered = Settings.SwitchTabOnHover && e.LeftButton == MouseButtonState.Released ? TabAt(e.OriginalSource)?.Window : null;
		if (hovered == this)
		{
			hovered = null;
		}
		if (hovered == _mouseTab)
		{
			return;
		}
		_mouseTab = hovered;
		_mouseTabTimer.Stop();
		if (hovered != null)
		{
			_mouseTabTimer.Interval = TimeSpan.FromMilliseconds(Settings.TabHoverDelay);
			_mouseTabTimer.Start();
		}
	}

	void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_draggedTab is not FenceTab tab)
		{
			return;
		}
		bool detach = _tabDetaching;
		EndTabDrag();
		if (detach)
		{
			_manager.DetachTab(tab.Window, NativeMethods.GetCursorPos());
		}
		e.Handled = true;
	}

	void TitleBar_LostMouseCapture(object sender, MouseEventArgs e) => EndTabDrag();

	void EndTabDrag()
	{
		_draggedTab = null;
		_tabDetaching = false;
		_detachPreview?.Hide();
		if (TitleBar.IsMouseCaptured)
		{
			TitleBar.ReleaseMouseCapture();
		}
	}

	async void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (TitleEditor.IsVisible)
		{
			return;
		}
		e.Handled = true;
		var point = NativeMethods.GetCursorPos();
		// 右键点的是别的标签：先切过去，菜单作用于那个分区
		if (TabAt(e.OriginalSource) is FenceTab tab && tab.Window != this)
		{
			await _manager.ActivateTabAsync(tab.Window, true);
			tab.Window.ShowFenceMenu(point);
			return;
		}
		ShowFenceMenu(point);
	}

	/// <summary>
	/// 在标签条上滚动滚轮切换标签，到头不循环。
	/// </summary>
	void TitleBar_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		var tabs = _manager.TabsOf(this);
		if (tabs.Count < 2)
		{
			return;
		}
		int index = tabs.IndexOf(this) + (e.Delta > 0 ? -1 : 1);
		if (index >= 0 && index < tabs.Count)
		{
			_ = _manager.ActivateTabAsync(tabs[index], IsActive);
		}
		e.Handled = true;
	}

	void RollButton_Click(object sender, RoutedEventArgs e) => ToggleRollUp();

	void MenuButton_Click(object sender, RoutedEventArgs e)
	{
		var corner = MenuButton.PointToScreen(new Point(0, MenuButton.ActualHeight));
		ShowFenceMenu(new POINT((int)corner.X, (int)corner.Y));
	}

	public void BeginTitleEdit()
	{
		ActivateForInput();
		TitleEditor.Text = Model.Title;
		TitleEditor.Visibility = Visibility.Visible;
		ShowTitleText(false);
		Dispatcher.InvokeAsync(() =>
		{
			TitleEditor.Focus();
			TitleEditor.SelectAll();
		}, DispatcherPriority.Input);
	}

	void CommitTitleEdit(bool accept)
	{
		if (TitleEditor.Visibility != Visibility.Visible)
		{
			return;
		}
		TitleEditor.Visibility = Visibility.Collapsed;
		ShowTitleText(true);
		var title = TitleEditor.Text.Trim();
		if (accept && title.Length > 0 && title != Model.Title)
		{
			_manager.RenameFence(this, title);
		}
	}

	void TitleEditor_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			CommitTitleEdit(true);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			CommitTitleEdit(false);
			e.Handled = true;
		}
	}

	void TitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTitleEdit(true);

	#endregion

	#region 菜单

	void ShowFenceMenu(POINT point)
	{
		bool folderExists = IsDesktopFence || Directory.Exists(CurrentFolder);
		_menuOpen = true;
		_buttonsTimer.Stop();
		ShowTitleButtons(true);
		try
		{
			using var menu = new NativeMenu();
			menu.Add("新建分区", () => _manager.CreateFence(near: this, editTitle: true));
			menu.Add("新建文件夹映射分区…", () => _manager.CreatePortalFence(this));
			menu.AddSeparator();
			menu.Add("粘贴\tCtrl+V", PasteFromClipboard, enabled: folderExists && ItemOps.ClipboardHasFiles());
			menu.Add("新建文件夹", NewFolder, enabled: folderExists);
			menu.Add("刷新\tF5", RefreshItems);
			menu.AddSeparator();
			menu.AddSubMenu("排序方式", sub =>
			{
				foreach (var field in Enum.GetValues<SortField>())
				{
					sub.Add(field.DisplayName(), () => SetSort(field, Model.SortDescending), isChecked: Model.SortBy == field, radio: true);
				}
				sub.AddSeparator();
				bool custom = Model.SortBy == SortField.Custom;
				sub.Add("递增", () => SetSort(Model.SortBy, false), isChecked: !Model.SortDescending, enabled: !custom, radio: true);
				sub.Add("递减", () => SetSort(Model.SortBy, true), isChecked: Model.SortDescending, enabled: !custom, radio: true);
			});
			menu.AddSubMenu("查看", sub =>
			{
				foreach (var size in Enum.GetValues<IconSizeMode>())
				{
					sub.Add(size.DisplayName(), () => SetView(FenceView.Icons, size), isChecked: Model.View == FenceView.Icons && EffectiveIconSize == size, radio: true);
				}
				sub.Add("列表", () => SetView(FenceView.List, Model.IconSize), isChecked: Model.View == FenceView.List, radio: true);
				sub.Add("详细信息", () => SetView(FenceView.Details, Model.IconSize), isChecked: Model.View == FenceView.Details, radio: true);
			});
			menu.AddSubMenu("背景颜色", sub =>
			{
				foreach (var (name, hex) in Appearance.ColorPresets)
				{
					bool current = string.Equals(Model.Color, hex, StringComparison.OrdinalIgnoreCase);
					sub.Add(name, () => SetColor(hex), isChecked: current, radio: true, bitmap: sub.ColorSwatch(Appearance.ParseColor(hex, Colors.Black)));
				}
				sub.AddSeparator();
				sub.Add("自定义颜色…", PickCustomColor);
				sub.Add("跟随默认外观", () => SetColor(null), isChecked: Model.Color == null, radio: true);
			});
			menu.AddSubMenu("不透明度", sub =>
			{
				foreach (var percent in Appearance.OpacityPresets)
				{
					bool current = Model.Opacity is double value && Math.Abs(value * 100 - percent) < 0.5;
					sub.Add($"{percent}%", () => SetOpacity(percent / 100.0), isChecked: current, radio: true);
				}
				sub.AddSeparator();
				sub.Add("跟随默认外观", () => SetOpacity(null), isChecked: Model.Opacity == null, radio: true);
			});
			menu.AddSubMenu("毛玻璃背景", sub =>
			{
				sub.Add("开", () => SetBlur(true), isChecked: Model.Blur == true, radio: true);
				sub.Add("关", () => SetBlur(false), isChecked: Model.Blur == false, radio: true);
				sub.AddSeparator();
				sub.Add("跟随默认外观", () => SetBlur(null), isChecked: Model.Blur == null, radio: true);
			});
			menu.AddSeparator();
			menu.Add("重命名分区", BeginTitleEdit);
			var tabs = _manager.TabsOf(this);
			if (tabs.Count > 1)
			{
				int index = tabs.IndexOf(this);
				bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
				menu.AddSubMenu("标签页", sub =>
				{
					sub.Add(vertical ? "上移" : "左移", () => _manager.MoveTab(this, index - 1), enabled: !Model.Locked && index > 0);
					sub.Add(vertical ? "下移" : "右移", () => _manager.MoveTab(this, index + 1), enabled: !Model.Locked && index < tabs.Count - 1);
					sub.AddSeparator();
					sub.Add("拆分为独立分区", () => _manager.DetachTab(this, null), enabled: !Model.Locked);
				});
			}
			menu.Add(Model.RolledUp ? "展开分区" : "卷起分区", ToggleRollUp);
			menu.AddSubMenu("卷起方向", sub =>
			{
				sub.Add("自动（按贴着的屏幕边）", () => SetRollDirection(null), isChecked: Model.RollDirection == null, radio: true);
				sub.AddSeparator();
				foreach (var edge in Enum.GetValues<RollEdge>())
				{
					sub.Add(edge.DisplayName(), () => SetRollDirection(edge), isChecked: Model.RollDirection == edge, radio: true);
				}
			});
			menu.Add("锁定分区", ToggleLock, isChecked: Model.Locked);
			if (Model.IsPortal)
			{
				menu.Add("在资源管理器中打开", OpenFolderInExplorer, enabled: folderExists);
				menu.Add("更换映射文件夹…", () => _manager.ChangePortalFolder(this));
			}
			menu.AddSeparator();
			menu.Add("MyDesktop 设置…", _manager.ShowSettings);
			menu.Add("删除分区…", () => _manager.DeleteFence(this));
			menu.Show(_hwnd, point);
		}
		finally
		{
			_menuOpen = false;
			if (!IsMouseOver)
			{
				RestartTimer(_buttonsTimer);
			}
		}
	}

	/// <param name="items">菜单作用的图标，可能包含其他分区和桌面上一起选中的。</param>
	/// <param name="desktop">它们是不是桌面上的图标。</param>
	/// <param name="renameTarget">选「重命名」时改名的图标：右键点中的那一个。</param>
	void ShowItemMenu(List<FenceItem> items, bool desktop, POINT point, FenceItem renameTarget)
	{
		if (items.Count == 0)
		{
			return;
		}
		_menuOpen = true;
		try
		{
			var paths = items.Select(i => i.FullPath).ToList();
			var extras = new List<(string, Action)>();
			if (desktop)
			{
				extras.Add(("移出分区", () => _manager.AssignToFence(null, paths)));
			}
			else
			{
				extras.Add(("移回桌面", () => MoveToDesktop(paths)));
			}
			ItemOps.ShowContextMenu(_hwnd, items, desktop, point, extras, verb =>
			{
				// Shell 自己不会处理重命名（需要视图配合），改为在分区内联编辑
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
			Log.Warn("显示文件右键菜单失败", ex);
		}
		finally
		{
			_menuOpen = false;
			ScheduleRefresh();
		}
	}

	void SetSort(SortField field, bool descending)
	{
		// 切到自定义排序时以当前显示顺序为起点；在子文件夹里切换时，映射的文件夹本身的顺序保持不变
		if (field == SortField.Custom && Model.SortBy != SortField.Custom && _subFolder == null)
		{
			Model.CustomOrder = _items.Select(OrderKey).ToList();
		}
		Model.SortBy = field;
		Model.SortDescending = descending;
		RefreshItems();
		UpdateDetailsHeader();
		_manager.SaveSoon();
	}

	void SetView(FenceView view, IconSizeMode? size)
	{
		Model.View = view;
		Model.IconSize = size;
		ApplyViewMode();
		_manager.SaveSoon();
	}

	void SetColor(string? hex)
	{
		Model.Color = hex;
		ApplyAppearance();
		_manager.SaveSoon();
	}

	void SetOpacity(double? opacity)
	{
		Model.Opacity = opacity;
		ApplyAppearance();
		_manager.SaveSoon();
	}

	void SetBlur(bool? blur)
	{
		Model.Blur = blur;
		ApplyAppearance();
		_manager.SaveSoon();
	}

	void ToggleLock()
	{
		Model.Locked = !Model.Locked;
		ApplyLockState();
		_manager.SyncGroup(this);
		_manager.SaveSoon();
	}

	void PickCustomColor()
	{
		var current = Appearance.ParseColor(Model.Color ?? Settings.DefaultColor, Colors.Black);
		var buffer = Marshal.AllocHGlobal(CustomColors.Length * sizeof(int));
		try
		{
			Marshal.Copy(CustomColors, 0, buffer, CustomColors.Length);
			var choose = new CHOOSECOLOR
			{
				lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
				hwndOwner = _hwnd,
				rgbResult = current.R | (current.G << 8) | (current.B << 16),
				lpCustColors = buffer,
				Flags = CC_RGBINIT | CC_FULLOPEN,
			};
			if (!ChooseColor(ref choose))
			{
				return;
			}
			Marshal.Copy(buffer, CustomColors, 0, CustomColors.Length);
			var picked = Color.FromRgb((byte)(choose.rgbResult & 0xFF), (byte)((choose.rgbResult >> 8) & 0xFF), (byte)((choose.rgbResult >> 16) & 0xFF));
			SetColor(Appearance.ToHex(picked));
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	#endregion

	#region 文件操作

	/// <summary>
	/// 打开整个桌面选中的图标；只有本分区有选中时按本分区的规则打开（映射分区里的单个文件夹在分区里进入）。
	/// </summary>
	void OpenSelected()
	{
		var all = _manager.AllSelectedItems();
		if (all.Count == ItemsList.SelectedItems.Count)
		{
			OpenItems(SelectedItems);
		}
		else
		{
			ItemOps.Open(_hwnd, all, true);
		}
	}

	void OpenItems(IEnumerable<FenceItem> items)
	{
		var list = items.ToList();
		// 映射分区里打开单个文件夹时在分区里进入它，不用资源管理器打开
		if (Model.IsPortal && list is [{ IsFolder: true } folder] && Directory.Exists(folder.FullPath))
		{
			NavigateTo(folder.FullPath);
			return;
		}
		ItemOps.Open(_hwnd, list, IsDesktopFence);
	}

	/// <summary>
	/// 映射分区里显示另一个文件夹（为空或等于映射的文件夹时回到映射的文件夹）。
	/// </summary>
	void NavigateTo(string? folder)
	{
		_subFolder = folder == null || PathUtil.AreEqual(folder, Model.FolderPath) ? null : folder;
		ItemsList.UnselectAll();
		UpdateTitle();
		StartWatcher();
		RefreshItems();
		_scroller?.ScrollToTop();
	}

	void NavigateUp()
	{
		if (_subFolder is not string from)
		{
			return;
		}
		NavigateTo(Path.GetDirectoryName(PathUtil.Normalize(from)));
		// 与资源管理器一致，返回上一级后选中刚才所在的文件夹
		if (FindItem(from) is { } item)
		{
			ItemsList.SelectedItem = item;
			ItemsList.ScrollIntoView(item);
		}
	}

	void BackButton_Click(object sender, RoutedEventArgs e) => NavigateUp();

	/// <summary>
	/// 取消选择（单击桌面空白处时）。
	/// </summary>
	public void ClearSelection() => ItemsList.UnselectAll();

	void OpenFolderInExplorer()
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CurrentFolder}\"") { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			Log.Warn($"打开文件夹失败：{CurrentFolder}", ex);
		}
	}

	void MoveToDesktop(IEnumerable<string> paths)
	{
		ShellFileOps.Move(_hwnd, paths, AppPaths.Desktop);
		ScheduleRefresh();
	}

	void DeleteItems(List<FenceItem> items, bool permanent)
	{
		ItemOps.Delete(_hwnd, items, permanent);
		ScheduleRefresh();
	}

	/// <summary>
	/// 新建的文件或文件夹所在的位置：桌面分区放在桌面文件夹（再归入本分区），映射分区放在映射的文件夹。
	/// </summary>
	string TargetFolder => IsDesktopFence ? AppPaths.Desktop : CurrentFolder;

	void NewFolder()
	{
		var path = PathUtil.UniquePath(TargetFolder, "新建文件夹");
		try
		{
			Directory.CreateDirectory(path);
		}
		catch (Exception ex)
		{
			MessageDialog.Show("新建文件夹", $"创建失败：{ex.Message}", "确定");
			return;
		}
		if (IsDesktopFence)
		{
			// 要等资源管理器把新文件夹加进桌面视图后才能改名
			_manager.AssignToFence(this, [path]);
			_manager.RequestRename(path);
			return;
		}
		RefreshItems();
		var item = _items.FirstOrDefault(i => PathUtil.AreEqual(i.FullPath, path));
		if (item != null)
		{
			BeginRename(item);
		}
	}

	void CopyToClipboard(List<FenceItem> items, bool cut) => ItemOps.CopyToClipboard(items, cut);

	void PasteFromClipboard()
	{
		var pasted = ItemOps.Paste(_hwnd, TargetFolder);
		if (IsDesktopFence && pasted.Count > 0)
		{
			_manager.AssignToFence(this, pasted);
		}
		ScheduleRefresh();
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
		ActivateForInput();
		ItemsList.UnselectAll();
		ItemsList.SelectedItem = item;
		ItemsList.ScrollIntoView(item);
		item.RenameText = item.DisplayName;
		item.IsRenaming = true;
		Dispatcher.InvokeAsync(() =>
		{
			var box = FindRenameBox(item);
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

	void CommitAllRenames()
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
		var oldPath = item.FullPath;
		if (ItemOps.Rename(_hwnd, item, item.RenameText) is not string newPath)
		{
			return;
		}
		if (IsDesktopFence)
		{
			// 成员和自定义顺序记的是完整路径，改名后同步更新，位置不变
			_manager.OnItemRenamed(oldPath, newPath);
		}
		else if (_subFolder == null)
		{
			// 自定义顺序按文件名记录（只记映射的文件夹本身），改名后同步更新，位置不变
			int index = Model.CustomOrder.FindIndex(n => string.Equals(n, item.FileName, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
			{
				Model.CustomOrder[index] = Path.GetFileName(newPath);
				_manager.SaveSoon();
			}
		}
		ScheduleRefresh();
	}

	TextBox? FindRenameBox(FenceItem item)
	{
		return ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container ? FindChild<TextBox>(container, "RenameBox") : null;
	}

	static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject => ItemOps.FindDescendant<T>(parent);

	static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement => ItemOps.FindChild<T>(parent, name);

	/// <summary>
	/// 分区平时不抢焦点，需要键盘输入（重命名）时才主动激活。
	/// </summary>
	void ActivateForInput()
	{
		if (!IsActive)
		{
			NativeMethods.ForceForegroundWindow(_hwnd);
			Activate();
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
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		switch (key)
		{
			case Key.Enter:
				OpenSelected();
				break;
			case Key.Back when _subFolder != null:
			case Key.Left when (modifiers & ModifierKeys.Alt) != 0 && _subFolder != null:
				NavigateUp();
				break;
			case Key.Delete:
				DeleteItems(_manager.AllSelectedItems(), shift);
				break;
			case Key.F2:
				if (SelectedItems.FirstOrDefault() is { } item)
				{
					BeginRename(item);
				}
				break;
			case Key.F5:
				RefreshItems();
				break;
			case Key.Escape:
				// 和桌面上一样，按 Esc 取消选中
				ItemsList.UnselectAll();
				break;
			case Key.A when ctrl:
				// 全选只选这个分区里的，别处的选择取消，免得接着删除时连带别处的图标
				_manager.OnSelectionScopeActivated(this);
				ItemsList.SelectAll();
				break;
			case Key.C when ctrl:
				CopyToClipboard(_manager.AllSelectedItems(), false);
				break;
			case Key.X when ctrl:
				CopyToClipboard(_manager.AllSelectedItems(), true);
				break;
			case Key.V when ctrl:
				PasteFromClipboard();
				break;
			case Key.F4 when (modifiers & ModifierKeys.Alt) != 0:
				// 吞掉 Alt+F4，分区不能被这样关掉
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	#endregion

	#region 拖入

	protected override void OnDragEnter(DragEventArgs e)
	{
		base.OnDragEnter(e);
		_dragVersion++;
		bool firstEnter = !_dragInside;
		if (firstEnter)
		{
			_dragInside = true;
			var drag = DesktopDrag.Current;
			_dropKeys = drag?.Keys.ToArray() ?? GetDropPaths(e.Data);
			_dropFiles = drag?.Files.ToArray() ?? _dropKeys ?? [];
			_dropFromDesktop = drag?.FromDesktop == true;
			SetDropHighlight(true);
			if (Model.RolledUp && !_tempExpanded)
			{
				_tempExpanded = true;
				_manager.BringToFront(this);
				ApplyRollBounds();
			}
		}
		e.Effects = ComputeEffect(e);
		if (firstEnter)
		{
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
		HoverTab(e);
		e.Handled = true;
	}

	/// <summary>
	/// 拖着图标停在别的标签上一会儿就切到那个标签，方便放进去。
	/// </summary>
	void HoverTab(DragEventArgs e)
	{
		var hovered = TitleBar.InputHitTest(e.GetPosition(TitleBar)) is DependencyObject hit ? TabAt(hit)?.Window : null;
		if (hovered == this)
		{
			hovered = null;
		}
		if (hovered == _hoverTab)
		{
			return;
		}
		_hoverTab = hovered;
		_tabHoverTimer.Stop();
		if (hovered != null)
		{
			_tabHoverTimer.Start();
		}
	}

	/// <summary>
	/// 拖到图标上时交给图标自己处理（文件夹、程序、回收站等），否则按分区的规则（调整顺序、归入分区、放进文件夹）。
	/// </summary>
	DragDropEffects ComputeEffect(DragEventArgs e)
	{
		if (_forwarder.Over(_hwnd, DropItemAt(e), IsDesktopFence, e) is DragDropEffects forwarded)
		{
			InsertMarker.Visibility = Visibility.Collapsed;
			_insertIndex = -1;
			return forwarded;
		}
		TrackReorder(e);
		return ComputeDropEffect(e);
	}

	/// <summary>
	/// 拖动经过的图标；正被拖动的图标自身不算。在本分区内调整顺序时只有拖到图标图像上才算，
	/// 落在名称、空隙处仍是调整顺序，免得拖过快捷方式时误把文件交给程序打开。
	/// </summary>
	FenceItem? DropItemAt(DragEventArgs e)
	{
		if (ContentHost.InputHitTest(e.GetPosition(ContentHost)) is not DependencyObject element
				|| ItemFromSource(element) is not FenceItem item
				|| (_dropKeys?.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase) ?? false))
		{
			return null;
		}
		return IsReorderDrag() && !IsInside<Image>(element) ? null : item;
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
				_forwarder.Leave();
				EndDragInside(true);
			}
		}, DispatcherPriority.Input);
		e.Handled = true;
	}

	protected override void OnDrop(DragEventArgs e)
	{
		base.OnDrop(e);
		_dragVersion++;
		if (_forwarder.Drop(e) is DragDropEffects done)
		{
			// 图标自己完成了放置（移进文件夹、用程序打开、删除到回收站）
			_dropPreview.Drop(e, done);
			EndDragInside(false);
			e.Effects = done;
			e.Handled = true;
			ScheduleRefresh();
			return;
		}
		var effect = ComputeDropEffect(e);
		var keys = _dropKeys;
		var paths = _dropFiles;
		bool fromDesktop = _dropFromDesktop;
		bool reorder = IsReorderDrag();
		int insertIndex = _insertIndex;
		_dropPreview.Drop(e, effect);
		EndDragInside(false);
		e.Handled = true;
		if (reorder && keys != null)
		{
			// 同一分区内拖动只调整顺序，不动文件；回报 None，拖放源无需做任何事
			e.Effects = DragDropEffects.None;
			ApplyReorder(keys, insertIndex);
			return;
		}
		if (keys == null || effect == DragDropEffects.None)
		{
			e.Effects = DragDropEffects.None;
			return;
		}
		if (IsDesktopFence && fromDesktop)
		{
			// 桌面上的图标拖进桌面分区只改归属，文件不动
			e.Effects = DragDropEffects.None;
			_manager.AssignToFence(this, keys);
			return;
		}
		// 移动由本程序完成，回报 None 让拖放源不再自行删除源文件（防止非优化移动造成误删）
		e.Effects = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
		// 压缩软件拖出的临时文件会在拖放结束后被删除，必须同步处理；其余情况异步执行，避免拖放源界面卡住
		if (paths.Any(p => PathUtil.IsUnder(p, Path.GetTempPath())))
		{
			PerformDrop(paths, effect);
		}
		else
		{
			Dispatcher.InvokeAsync(() => PerformDrop(paths, effect));
		}
	}

	void EndDragInside(bool notifyHelper)
	{
		_dragInside = false;
		_hoverTab = null;
		_tabHoverTimer.Stop();
		_dropKeys = null;
		_dropFiles = [];
		_dropFromDesktop = false;
		_insertIndex = -1;
		InsertMarker.Visibility = Visibility.Collapsed;
		SetDropHighlight(false);
		if (notifyHelper)
		{
			_dropPreview.Leave();
		}
		if (_tempExpanded)
		{
			RestartTimer(_collapseTimer);
		}
	}

	/// <summary>
	/// 拖动的全是本分区里的项目：视为调整顺序。
	/// </summary>
	bool IsReorderDrag()
	{
		if (_dropKeys is not { Length: > 0 } keys)
		{
			return false;
		}
		if (IsDesktopFence)
		{
			return _dropFromDesktop && keys.All(k => Model.Members.Contains(k, StringComparer.OrdinalIgnoreCase));
		}
		// 自定义顺序只记映射的文件夹本身，进入子文件夹后不调整顺序
		return _subFolder == null && _dropFiles.Length == keys.Length && keys.All(p => PathUtil.AreEqual(Path.GetDirectoryName(p), Model.FolderPath));
	}

	void TrackReorder(DragEventArgs e)
	{
		if (!IsReorderDrag())
		{
			InsertMarker.Visibility = Visibility.Collapsed;
			return;
		}
		var position = e.GetPosition(ContentHost);
		AutoScroll(position);
		_insertIndex = GetInsertIndex(position, out var marker);
		if (marker is Rect rect)
		{
			Canvas.SetLeft(InsertMarker, rect.X);
			Canvas.SetTop(InsertMarker, rect.Y);
			InsertMarker.Width = rect.Width;
			InsertMarker.Height = rect.Height;
			InsertMarker.Visibility = Visibility.Visible;
		}
		else
		{
			InsertMarker.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>
	/// 根据鼠标位置计算插入序号，并给出插入标记线的位置（ContentHost 坐标）。
	/// </summary>
	int GetInsertIndex(Point position, out Rect? marker)
	{
		marker = null;
		// 详细信息视图和只有一列的列表视图上下插入（横线），排成多列后和图标视图一样左右插入（竖线）
		bool list = Model.View == FenceView.Details || (Model.View == FenceView.List && ListColumns() <= 1);
		Rect? previous = null;
		for (int i = 0; i < _items.Count; i++)
		{
			if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
			{
				continue;
			}
			var bounds = container.TransformToAncestor(ContentHost).TransformBounds(new Rect(container.RenderSize));
			if (list)
			{
				if (position.Y < bounds.Top + bounds.Height / 2)
				{
					marker = new Rect(bounds.Left, bounds.Top - 1.5, bounds.Width, 3);
					return i;
				}
			}
			else if (position.Y < bounds.Top)
			{
				// 鼠标在上一行的空白处：标记画在上一行末尾
				marker = previous is Rect prev && position.Y >= prev.Top
						? new Rect(prev.Right - 1.5, prev.Top, 3, prev.Height)
						: new Rect(bounds.Left - 1.5, bounds.Top, 3, bounds.Height);
				return i;
			}
			else if (position.Y <= bounds.Bottom && position.X < bounds.Left + bounds.Width / 2)
			{
				marker = new Rect(bounds.Left - 1.5, bounds.Top, 3, bounds.Height);
				return i;
			}
			previous = bounds;
		}
		if (previous is Rect last)
		{
			marker = list ? new Rect(last.Left, last.Bottom - 1.5, last.Width, 3) : new Rect(last.Right - 1.5, last.Top, 3, last.Height);
		}
		return _items.Count;
	}

	/// <summary>
	/// 列表视图现在排成几列。
	/// </summary>
	int ListColumns()
	{
		return ItemsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem container && VisualTreeHelper.GetParent(container) is ListColumnsPanel panel
				? panel.Columns
				: 1;
	}

	/// <summary>
	/// 拖到列表上下边缘时自动滚动，方便把图标拖到看不见的位置。
	/// </summary>
	void AutoScroll(Point position)
	{
		_scroller ??= FindDescendant<ScrollViewer>(ItemsList);
		if (_scroller == null)
		{
			return;
		}
		const double edge = 24;
		if (position.Y < edge)
		{
			_scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 12);
		}
		else if (position.Y > ContentHost.ActualHeight - edge)
		{
			_scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 12);
		}
	}

	void ApplyReorder(string[] keys, int insertIndex)
	{
		var moving = new HashSet<string>(keys.Select(OrderKey), StringComparer.OrdinalIgnoreCase);
		var order = _items.Select(OrderKey).ToList();
		if (insertIndex < 0 || insertIndex > order.Count)
		{
			insertIndex = order.Count;
		}
		// 插入点之前被拖走的项目会让插入位置前移
		int target = insertIndex - order.Take(insertIndex).Count(moving.Contains);
		var dragged = order.Where(moving.Contains).ToList();
		order.RemoveAll(moving.Contains);
		order.InsertRange(Math.Clamp(target, 0, order.Count), dragged);
		Model.SortBy = SortField.Custom;
		Model.CustomOrder = order;
		RefreshItems();
		_manager.SaveSoon();
	}

	/// <summary>
	/// 在同一分区内拖动是调整顺序；桌面上的图标拖进桌面分区只改归属；
	/// 其他文件按资源管理器的规则（同盘或来自桌面/其他分区时移动，跨盘复制；Ctrl 复制、Shift 移动、Alt 创建快捷方式）
	/// 放进映射的文件夹，桌面分区则放到桌面文件夹再归入本分区。
	/// </summary>
	DragDropEffects ComputeDropEffect(DragEventArgs e)
	{
		var keys = _dropKeys;
		if (keys == null || keys.Length == 0)
		{
			return DragDropEffects.None;
		}
		if (IsReorderDrag() || (IsDesktopFence && _dropFromDesktop))
		{
			return (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
		}
		var folder = TargetFolder;
		// 系统图标没有文件可以移动或复制
		var paths = _dropFiles;
		if (paths.Length == 0 || !Directory.Exists(folder))
		{
			return DragDropEffects.None;
		}
		// 把文件夹拖进它自己（及其子目录）对应的分区不处理
		if (paths.Any(p => PathUtil.AreEqual(p, folder) || PathUtil.IsUnder(folder, p)))
		{
			return DragDropEffects.None;
		}
		// 映射分区的子文件夹里原地拖动，不移动也不调整顺序
		if (!IsDesktopFence && paths.All(p => PathUtil.AreEqual(Path.GetDirectoryName(p), folder)))
		{
			return DragDropEffects.None;
		}
		return ItemOps.ChooseDropEffect(e, _manager.PrefersMove(paths, folder));
	}

	void PerformDrop(string[] paths, DragDropEffects effect)
	{
		var folder = TargetFolder;
		if (IsDesktopFence)
		{
			// 先登记预期的新路径，资源管理器更新视图时新图标直接出现在本分区；桌面上已有同名项目的不登记，免得把它拉进来
			var expected = paths.Select(p => Path.Combine(folder, Path.GetFileName(p.TrimEnd('\\'))))
					.Where(p => !File.Exists(p) && !Directory.Exists(p))
					.ToList();
			_manager.AssignToFence(this, expected);
		}
		var created = ItemOps.Transfer(_hwnd, paths, folder, effect);
		if (IsDesktopFence && effect == DragDropEffects.Link)
		{
			_manager.AssignToFence(this, created);
		}
		ScheduleRefresh();
	}

	static string[]? GetDropPaths(IDataObject data)
	{
		try
		{
			return data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	#endregion
}
