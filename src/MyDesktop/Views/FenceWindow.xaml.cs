using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
	const double ResizeBorderDip = 5;
	const double SnapDistanceDip = 12;
	const double FadeMilliseconds = 180;
	// 毛玻璃在拖动开始时淡出、松手或换壁纸后淡入的时长
	const double BlurFadeOutMilliseconds = 100;
	const double BlurFadeInMilliseconds = 300;
	const double MinWidthDip = 120;
	// 离屏幕边缘这么近算贴着这条边，决定自动卷起方向
	const double DockDistanceDip = 24;

	static readonly DropShadowEffect TextShadowEffect = CreateShadow();
	static readonly int[] CustomColors = new int[16];

	readonly FenceManager _manager;
	readonly ObservableCollection<FenceItem> _items = [];
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
	bool _menuOpen;

	// 移动/缩放跟踪：按鼠标相对起点的绝对位移计算目标位置
	POINT _dragCursorStart;
	RECT _dragRectStart;
	RECT? _dragLastRect;
	bool _dragFinished;
	bool _inSizeMove;
	bool _heightResized;
	bool _widthResized;

	// 标题栏当前摆在哪一侧，没变时不重排布局
	RollEdge? _titleEdge;

	// 列表鼠标交互
	Point _pressPoint;
	FenceItem? _pressedItem;
	bool _deferSelection;
	bool _rubberBand;
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

	public FenceWindow(FenceManager manager, FenceSettings model)
	{
		_manager = manager;
		Model = model;
		InitializeComponent();
		ItemsList.ItemsSource = _items;
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
			if (!_menuOpen && !IsMouseOver)
			{
				ShowTitleButtons(false);
			}
		};
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

	double IconDip => Model.View == FenceView.List ? Appearance.ListIconDip : EffectiveIconSize.ToDip();

	List<FenceItem> SelectedItems => ItemsList.SelectedItems.Cast<FenceItem>().ToList();

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
		Show();
		PlaceAboveDesktop();
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
		_refreshTimer.Stop();
		_collapseTimer.Stop();
		_buttonsTimer.Stop();
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
	public RECT GetLayoutBounds() => Model.RolledUp ? CollapsedRect() : ExpandedRect;

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
				if (Settings.SnapToEdges || Settings.SnapToGrid)
				{
					SnapSizing((int)wParam, lParam, grid);
					handled = true;
				}
				ShowGridHint(Marshal.PtrToStructure<RECT>(lParam), grid);
				return handled ? new IntPtr(1) : IntPtr.Zero;
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
	/// 与移动同理，被拖动的边按鼠标相对起点的绝对位移计算后再吸附。
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
		Marshal.StructureToPtr(rect, lParam, false);
		_dragLastRect = rect;
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
	/// 调整大小时在分区中央显示当前尺寸能完整容纳的列数 × 行数；没有图标可供测量时不显示。
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
		GridHintText.Text = Model.View == FenceView.List ? $"{rows} 行" : $"{columns} × {rows}";
		GridHint.Visibility = Visibility.Visible;
	}

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

	void FinishMoveSize()
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
		SaveBounds();
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
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		var rect = !Model.RolledUp ? ExpandedRect : IsCollapsed ? CollapsedRect() : TempExpandedRect();
		// 左右收起时窗口只有标题栏那么宽：先放开最小宽度再改大小，改完再按当前形态设回，免得被最小宽度撑开
		double minWidth = IsCollapsed && IsVerticalRoll ? TitleBarDip + 2 : MinWidthDip;
		MinWidth = Math.Min(MinWidth, minWidth);
		SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
		MinWidth = minWidth;
		UpdateRollVisuals();
	}

	int CollapsedHeight() => (int)Math.Round((TitleBarDip + 2) * ScaleFactor);

	RECT ExpandedRect => new(Model.X, Model.Y, Model.X + Model.Width, Model.Y + Model.Height);

	bool IsVerticalRoll => Model.RollEdge is RollEdge.Left or RollEdge.Right;

	/// <summary>
	/// 收起后剩下的那一条：沿收向的一侧，宽度（或高度）正好是标题栏。
	/// </summary>
	RECT CollapsedRect()
	{
		int thickness = CollapsedHeight();
		var r = ExpandedRect;
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
		_manager.OnFenceLayoutChanged();
		_manager.SaveSoon();
	}

	public void ToggleRollUp()
	{
		// 收向标题栏所在的一侧，卷起和固定展开时标题栏都不动
		Model.RolledUp = !Model.RolledUp;
		_tempExpanded = false;
		_collapseTimer.Stop();
		ApplyBounds();
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
		ShowTitleText(TitleEditor.Visibility != Visibility.Visible);
	}

	/// <summary>
	/// 标题栏竖放时显示竖排标题，否则显示横排标题；改名期间都不显示。
	/// </summary>
	void ShowTitleText(bool show)
	{
		bool vertical = _titleEdge is RollEdge.Left or RollEdge.Right;
		TitleText.Visibility = vertical ? Visibility.Collapsed : show ? Visibility.Visible : Visibility.Hidden;
		VerticalTitleText.Visibility = vertical && show ? Visibility.Visible : Visibility.Collapsed;
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
		Resources["FenceSelected"] = Frozen(light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
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
		bool list = Model.View == FenceView.List;
		double iconDip = EffectiveIconSize.ToDip();
		Resources["IconSize"] = iconDip;
		Resources["CellWidth"] = iconDip + 44;
		ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(list ? "ListPanel" : "IconsPanel");
		ItemsList.ItemTemplate = (DataTemplate)FindResource(list ? "ListTemplate" : "IconTemplate");
		ItemsList.ItemContainerStyle = (Style)FindResource(list ? "FenceListItemContainer" : "FenceItemContainer");
		foreach (var item in _items)
		{
			RequestIcon(item);
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
		var badges = new List<string>();
		if (Model.IsPortal)
		{
			badges.Add("");
		}
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
			ApplyBounds();
		}
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
		ApplyBounds();
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
		try
		{
			foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
			{
				if (!Settings.ShowHiddenFiles && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
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
	/// 在分区里按下鼠标（任意键，含空白处）即开始操作这个分区，取消桌面和其他分区的选择。
	/// </summary>
	void ItemsList_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _manager.OnSelectionScopeActivated(this);

	/// <summary>
	/// 分区里有图标被选中（键盘、全选、拖入后选中等，不只是鼠标点击）：同样取消别处的选择。
	/// </summary>
	void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (e.AddedItems.Count > 0)
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
		_pressPoint = e.GetPosition(ContentHost);
		_pressedItem = item;
		_deferSelection = false;

		if (item == null)
		{
			BeginRubberBand();
			e.Handled = true;
			return;
		}
		if (e.ClickCount >= 2)
		{
			OpenItems(ItemsList.SelectedItems.Contains(item) ? SelectedItems : [item]);
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
			if (e.LeftButton == MouseButtonState.Pressed)
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
		StartDragOut(SelectedItems, item);
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
			ItemsList.UnselectAll();
			ItemsList.SelectedItem = _pressedItem;
		}
		_deferSelection = false;
		_pressedItem = null;
	}

	void ItemsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource))
		{
			return;
		}
		CommitAllRenames();
		var item = ItemFromSource(e.OriginalSource);
		var point = NativeMethods.GetCursorPos();
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
			ShowItemMenu(SelectedItems, point);
		}
		e.Handled = true;
	}

	void BeginRubberBand()
	{
		if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
		{
			ItemsList.UnselectAll();
		}
		_rubberBase = ItemsList.SelectedItems.Cast<FenceItem>().ToHashSet();
		_rubberBand = true;
		ItemsList.Focus();
		ItemsList.CaptureMouse();
	}

	void UpdateRubberBand(Point current)
	{
		if (SelectionRect.Visibility != Visibility.Visible)
		{
			if (!ExceedsDragThreshold(current))
			{
				return;
			}
			SelectionRect.Visibility = Visibility.Visible;
		}
		var rect = new Rect(_pressPoint, current);
		Canvas.SetLeft(SelectionRect, rect.X);
		Canvas.SetTop(SelectionRect, rect.Y);
		SelectionRect.Width = rect.Width;
		SelectionRect.Height = rect.Height;
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
		SelectionRect.Visibility = Visibility.Collapsed;
		ItemsList.ReleaseMouseCapture();
	}

	bool ExceedsDragThreshold(Point position)
	{
		return Math.Abs(position.X - _pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
				|| Math.Abs(position.Y - _pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
	}

	/// <param name="anchor">鼠标按下的那一项，拖动预览图按它生成。</param>
	void StartDragOut(List<FenceItem> items, FenceItem anchor)
	{
		if (items.Count == 0)
		{
			return;
		}
		Mouse.Capture(null);
		var image = ItemOps.CreateDragImage(ItemsList.ItemContainerGenerator.ContainerFromItem(anchor) as ListBoxItem, items.Count, ScaleFactor);
		ItemOps.DragOut(_hwnd, items, IsDesktopFence, this, image);
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
		if (Model.Locked)
		{
			return;
		}
		try
		{
			DragMove();
		}
		catch (InvalidOperationException)
		{
			// 鼠标已松开时 DragMove 会抛异常，忽略即可
		}
		e.Handled = true;
	}

	void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (TitleEditor.IsVisible)
		{
			return;
		}
		ShowFenceMenu(NativeMethods.GetCursorPos());
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

	void ShowItemMenu(List<FenceItem> items, POINT point)
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
			if (IsDesktopFence)
			{
				extras.Add(("移出分区", () => _manager.AssignToFence(null, paths)));
			}
			else
			{
				extras.Add(("移回桌面", () => MoveToDesktop(paths)));
			}
			ItemOps.ShowContextMenu(_hwnd, items, IsDesktopFence, point, extras, verb =>
			{
				// Shell 自己不会处理重命名（需要视图配合），改为在分区内联编辑
				if (!string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (items[0].CanRename)
				{
					BeginRename(items[0]);
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
				OpenItems(SelectedItems);
				break;
			case Key.Back when _subFolder != null:
			case Key.Left when (modifiers & ModifierKeys.Alt) != 0 && _subFolder != null:
				NavigateUp();
				break;
			case Key.Delete:
				DeleteItems(SelectedItems, shift);
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
			case Key.A when ctrl:
				ItemsList.SelectAll();
				break;
			case Key.C when ctrl:
				CopyToClipboard(SelectedItems, false);
				break;
			case Key.X when ctrl:
				CopyToClipboard(SelectedItems, true);
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
				ApplyBounds();
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
		e.Handled = true;
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
		bool list = Model.View == FenceView.List;
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
