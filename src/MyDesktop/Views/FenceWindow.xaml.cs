using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MyDesktop.Views;

/// <summary>
/// 桌面上的一个分区窗口：展示一个文件夹的内容，挂在桌面层，支持拖放、右键菜单、卷起、吸附等。
/// </summary>
internal partial class FenceWindow : Window
{
	const double TitleBarDip = 30;
	const double ResizeBorderDip = 5;
	const double SnapDistanceDip = 12;
	const int ErrorCancelled = 1223;

	static readonly DropShadowEffect TextShadowEffect = CreateShadow();
	static readonly int[] CustomColors = new int[16];

	readonly FenceManager _manager;
	readonly ObservableCollection<FenceItem> _items = [];
	readonly DispatcherTimer _refreshTimer;
	readonly DispatcherTimer _hoverTimer;
	IntPtr _hwnd;
	FileSystemWatcher? _watcher;
	Brush? _frameBorder;
	bool _folderExisted;
	bool _allowClose;
	bool _tempExpanded;
	bool _hoverExpand;
	bool _menuOpen;

	// 移动/缩放跟踪：按鼠标相对起点的绝对位移计算目标位置
	POINT _dragCursorStart;
	RECT _dragRectStart;
	RECT? _dragLastRect;
	bool _dragFinished;

	// 列表鼠标交互
	Point _pressPoint;
	FenceItem? _pressedItem;
	bool _deferSelection;
	bool _rubberBand;
	HashSet<FenceItem> _rubberBase = [];

	// 拖入
	string[]? _dropPaths;
	bool _dragInside;
	int _dragVersion;
	IDropTargetHelper? _dropHelper;

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
		_hoverTimer = new DispatcherTimer();
		_hoverTimer.Tick += HoverTimer_Tick;
	}

	public FenceSettings Model { get; }

	public IntPtr Handle => _hwnd;

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
		_hoverTimer.Stop();
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
				KeepAboveDesktop(hwnd, lParam);
				break;
			}
			case WM_ENTERSIZEMOVE:
			{
				_dragCursorStart = NativeMethods.GetCursorPos();
				_dragRectStart = GetBounds();
				_dragLastRect = null;
				_dragFinished = false;
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
				if (Settings.SnapToEdges)
				{
					SnapSizing((int)wParam, lParam);
					handled = true;
					return new IntPtr(1);
				}
				break;
			}
			case WM_EXITSIZEMOVE:
			{
				_dragFinished = true;
				// 等系统的拖动循环完全结束后再收尾
				Dispatcher.InvokeAsync(FinishMoveSize, DispatcherPriority.Input);
				break;
			}
		}
		return IntPtr.Zero;
	}

	/// <summary>
	/// 任何层级变化（被点击激活、Show 等）都改写为「紧贴桌面窗口之上」：
	/// 平时位于所有应用窗口之下，Win+D 桌面被提到最前时依然可见。
	/// </summary>
	static void KeepAboveDesktop(IntPtr hwnd, IntPtr lParam)
	{
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((pos.flags & SWP_NOZORDER) != 0)
		{
			return;
		}
		if (DesktopHost.GetInsertAfterAboveDesktop(hwnd) is IntPtr insertAfter)
		{
			pos.hwndInsertAfter = insertAfter;
		}
		else
		{
			pos.flags |= SWP_NOZORDER;
		}
		Marshal.StructureToPtr(pos, lParam, false);
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
		bool left = x < rect.Left + border;
		bool right = x >= rect.Right - border;
		bool top = !IsCollapsed && y < rect.Top + border;
		bool bottom = !IsCollapsed && y >= rect.Bottom - border;
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
	void SnapSizing(int edge, IntPtr lParam)
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
		var lines = CollectSnapLines(rect);
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
		Model.X = rect.Left;
		Model.Y = rect.Top;
		Model.Width = rect.Width;
		// 卷起状态下（含悬停临时展开，高度可能被屏幕底部裁剪）不覆盖用户设定的展开高度
		if (!Model.RolledUp)
		{
			Model.Height = rect.Height;
		}
		_manager.SaveSoon();
	}

	#endregion

	#region 尺寸、卷起、外观

	public void ApplyBounds()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		int height = IsCollapsed ? CollapsedHeight() : Model.Height;
		if (_tempExpanded)
		{
			// 悬停临时展开时不超出屏幕底部
			var rect = new RECT(Model.X, Model.Y, Model.X + Model.Width, Model.Y + Model.Height);
			var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
			height = Math.Max(CollapsedHeight() * 3, Math.Min(height, work.Bottom - Model.Y));
		}
		SetWindowPos(_hwnd, IntPtr.Zero, Model.X, Model.Y, Model.Width, height, SWP_NOZORDER | SWP_NOACTIVATE);
		UpdateRollVisuals();
	}

	int CollapsedHeight() => (int)Math.Round((TitleBarDip + 2) * ScaleFactor);

	public void ToggleRollUp()
	{
		Model.RolledUp = !Model.RolledUp;
		_tempExpanded = false;
		_hoverTimer.Stop();
		ApplyBounds();
		_manager.SaveSoon();
	}

	void UpdateRollVisuals()
	{
		bool collapsed = IsCollapsed;
		RollButton.Content = Model.RolledUp ? "" : "";
		RollButton.ToolTip = Model.RolledUp ? "固定展开" : "卷起";
		double radius = Math.Max(0, Settings.CornerRadius - 1);
		TitleBar.CornerRadius = collapsed ? new CornerRadius(radius) : new CornerRadius(radius, radius, 0, 0);
		ContentHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
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
		TitleBar.Background = Frozen(light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x30, 0, 0, 0));
		Resources["FenceForeground"] = Frozen(light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White);
		Resources["FenceHover"] = Frozen(light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
		Resources["FenceSelected"] = Frozen(light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
		Resources["FenceSelectedBorder"] = Frozen(light ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
		Resources["TextShadow"] = Settings.TextShadow && !light ? TextShadowEffect : null;
		UpdateRollVisuals();
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
		TitleText.Text = Model.Title;
		TitleText.ToolTip = Model.FolderPath;
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

	#region 悬停展开

	protected override void OnMouseEnter(MouseEventArgs e)
	{
		base.OnMouseEnter(e);
		TitleButtons.Opacity = 1;
		if (Model.RolledUp && !_tempExpanded && Settings.ExpandOnHover)
		{
			StartHoverTimer(true, 300);
		}
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		base.OnMouseLeave(e);
		if (!_menuOpen)
		{
			TitleButtons.Opacity = 0;
		}
		if (_tempExpanded)
		{
			StartHoverTimer(false, 600);
		}
		else
		{
			_hoverTimer.Stop();
		}
	}

	void StartHoverTimer(bool expand, int delayMilliseconds)
	{
		_hoverExpand = expand;
		_hoverTimer.Stop();
		_hoverTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
		_hoverTimer.Start();
	}

	void HoverTimer_Tick(object? sender, EventArgs e)
	{
		_hoverTimer.Stop();
		bool inside = GetBounds().Contains(NativeMethods.GetCursorPos());
		if (_hoverExpand)
		{
			if (inside && Model.RolledUp && !_tempExpanded)
			{
				_tempExpanded = true;
				ApplyBounds();
			}
			return;
		}
		if (!_tempExpanded)
		{
			return;
		}
		// 鼠标仍在窗口内、菜单打开、拖放或重命名进行中时保持展开，稍后再检查
		if (inside || _menuOpen || _dragInside || IsEditing())
		{
			StartHoverTimer(false, 600);
			return;
		}
		_tempExpanded = false;
		ApplyBounds();
	}

	bool IsEditing() => TitleEditor.IsVisible || _items.Any(i => i.IsRenaming);

	#endregion

	#region 内容加载

	public void OnFolderChanged()
	{
		if (_allowClose)
		{
			return;
		}
		StartWatcher();
		RefreshItems();
	}

	/// <summary>
	/// 文件夹被外部删除或恢复时刷新（由管理器定时调用）。
	/// </summary>
	public void CheckFolder()
	{
		if (Directory.Exists(Model.FolderPath) != _folderExisted)
		{
			OnFolderChanged();
		}
	}

	public void ScheduleRefresh()
	{
		if (_allowClose)
		{
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
		var folder = Model.FolderPath;
		if (!Model.IsPortal && !Directory.Exists(folder))
		{
			// 托管分区的文件夹被外部删掉时自动重建
			try
			{
				Directory.CreateDirectory(folder);
			}
			catch (Exception ex)
			{
				Log.Warn($"重建分区文件夹失败：{folder}", ex);
			}
		}
		_folderExisted = Directory.Exists(folder);
		var existing = _items.ToDictionary(i => i.FullPath, StringComparer.OrdinalIgnoreCase);
		var fresh = new List<FenceItem>();
		if (_folderExisted)
		{
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
		}
		Sort(fresh);
		SyncItems(fresh);
		foreach (var item in fresh)
		{
			RequestIcon(item);
		}
		EmptyHint.Text = _folderExisted ? "拖放文件到这里" : $"文件夹不存在\n{folder}";
		EmptyHint.Visibility = fresh.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	void Sort(List<FenceItem> items)
	{
		Comparison<FenceItem> byField = Model.SortBy switch
		{
			SortField.Name => (a, b) => StrCmpLogicalW(a.DisplayName, b.DisplayName),
			SortField.Type => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
			SortField.Size => (a, b) => a.Size.CompareTo(b.Size),
			SortField.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
		};
		int direction = Model.SortDescending ? -1 : 1;
		items.Sort((a, b) =>
		{
			// 与资源管理器一致：文件夹始终排在文件前面
			if (a.IsFolder != b.IsFolder)
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
		});
	}

	void StartWatcher()
	{
		StopWatcher();
		if (!Directory.Exists(Model.FolderPath))
		{
			return;
		}
		try
		{
			var watcher = new FileSystemWatcher(Model.FolderPath)
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
			Log.Warn($"监视分区文件夹失败：{Model.FolderPath}", ex);
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
		StartDragOut(SelectedItems);
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

	void StartDragOut(List<FenceItem> items)
	{
		if (items.Count == 0)
		{
			return;
		}
		Mouse.Capture(null);
		try
		{
			ShellFileOps.DoDragDrop(_hwnd, items.Select(i => i.FullPath).ToList());
		}
		catch (Exception ex)
		{
			Log.Warn("拖出文件失败", ex);
		}
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

	static bool IsInside<T>(object source) where T : DependencyObject
	{
		var current = source as DependencyObject;
		while (current != null)
		{
			if (current is T)
			{
				return true;
			}
			current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
		}
		return false;
	}

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
		TitleText.Visibility = Visibility.Hidden;
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
		TitleText.Visibility = Visibility.Visible;
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
		bool folderExists = Directory.Exists(Model.FolderPath);
		_menuOpen = true;
		TitleButtons.Opacity = 1;
		try
		{
			using var menu = new NativeMenu();
			menu.Add("新建分区", () => _manager.CreateFence(near: this, editTitle: true));
			menu.Add("新建文件夹映射分区…", () => _manager.CreatePortalFence(this));
			menu.AddSeparator();
			menu.Add("粘贴\tCtrl+V", PasteFromClipboard, enabled: folderExists && ClipboardHasFiles());
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
				sub.Add("递增", () => SetSort(Model.SortBy, false), isChecked: !Model.SortDescending, radio: true);
				sub.Add("递减", () => SetSort(Model.SortBy, true), isChecked: Model.SortDescending, radio: true);
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
			menu.AddSeparator();
			menu.Add("重命名分区", BeginTitleEdit);
			menu.Add(Model.RolledUp ? "展开分区" : "卷起分区", ToggleRollUp);
			menu.Add("锁定分区", ToggleLock, isChecked: Model.Locked);
			menu.Add("在资源管理器中打开", OpenFolderInExplorer, enabled: folderExists);
			if (Model.IsPortal)
			{
				menu.Add("更换映射文件夹…", () => _manager.ChangePortalFolder(this));
			}
			menu.AddSeparator();
			menu.Add("删除分区…", () => _manager.DeleteFence(this));
			menu.Show(_hwnd, point);
		}
		finally
		{
			_menuOpen = false;
			if (!IsMouseOver)
			{
				TitleButtons.Opacity = 0;
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
			if (!PathUtil.AreEqual(Model.FolderPath, AppPaths.Desktop))
			{
				extras.Add(("移回桌面", () => MoveToDesktop(paths)));
			}
			ShellContextMenu.Show(_hwnd, paths, point, extras, verb =>
			{
				// Shell 自己不会处理重命名（需要视图配合），改为在分区内联编辑
				if (!string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				BeginRename(items[0]);
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
		foreach (var item in items)
		{
			try
			{
				var startInfo = new ProcessStartInfo(item.FullPath)
				{
					UseShellExecute = true,
					WorkingDirectory = Path.GetDirectoryName(item.FullPath) ?? string.Empty,
				};
				Process.Start(startInfo)?.Dispose();
			}
			catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
			{
				// 用户在 UAC 等提示中取消
			}
			catch (Exception ex)
			{
				Log.Warn($"打开失败：{item.FullPath}", ex);
				MessageDialog.Show("打开失败", $"无法打开「{item.DisplayName}」：\n{ex.Message}", "确定");
			}
		}
	}

	void OpenFolderInExplorer()
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Model.FolderPath}\"") { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			Log.Warn($"打开文件夹失败：{Model.FolderPath}", ex);
		}
	}

	void MoveToDesktop(IEnumerable<string> paths)
	{
		ShellFileOps.Move(_hwnd, paths, AppPaths.Desktop);
		ScheduleRefresh();
	}

	void DeleteItems(List<FenceItem> items, bool permanent)
	{
		if (items.Count == 0)
		{
			return;
		}
		ShellFileOps.Delete(_hwnd, items.Select(i => i.FullPath), !permanent);
		ScheduleRefresh();
	}

	void NewFolder()
	{
		var path = PathUtil.UniquePath(Model.FolderPath, "新建文件夹");
		try
		{
			Directory.CreateDirectory(path);
		}
		catch (Exception ex)
		{
			MessageDialog.Show("新建文件夹", $"创建失败：{ex.Message}", "确定");
			return;
		}
		RefreshItems();
		var item = _items.FirstOrDefault(i => PathUtil.AreEqual(i.FullPath, path));
		if (item != null)
		{
			BeginRename(item);
		}
	}

	void CopyToClipboard(List<FenceItem> items, bool cut)
	{
		if (items.Count == 0)
		{
			return;
		}
		try
		{
			var files = new StringCollection();
			files.AddRange(items.Select(i => i.FullPath).ToArray());
			var data = new DataObject();
			data.SetFileDropList(files);
			// 资源管理器的约定：剪切为 DROPEFFECT_MOVE(2)，复制为 COPY|LINK(5)
			data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
			Clipboard.SetDataObject(data, true);
		}
		catch (Exception ex)
		{
			Log.Warn("写入剪贴板失败", ex);
		}
	}

	void PasteFromClipboard()
	{
		try
		{
			if (!Clipboard.ContainsFileDropList())
			{
				return;
			}
			var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
			bool cut = Clipboard.GetData("Preferred DropEffect") is MemoryStream stream
					&& stream.Length >= 4
					&& (BitConverter.ToInt32(stream.ToArray(), 0) & 2) != 0;
			if (!cut)
			{
				ShellFileOps.Copy(_hwnd, files, Model.FolderPath);
			}
			else if (ShellFileOps.Move(_hwnd, files, Model.FolderPath))
			{
				Clipboard.Clear();
			}
		}
		catch (Exception ex)
		{
			Log.Warn("粘贴失败", ex);
		}
		ScheduleRefresh();
	}

	static bool ClipboardHasFiles()
	{
		try
		{
			return Clipboard.ContainsFileDropList();
		}
		catch (Exception)
		{
			return false;
		}
	}

	#endregion

	#region 重命名

	public void BeginRename(FenceItem item)
	{
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
		var newName = item.RenameText.Trim();
		if (newName.Length == 0 || newName == item.DisplayName)
		{
			return;
		}
		var target = newName;
		var extension = Path.GetExtension(item.FileName);
		// 显示名省略了扩展名（快捷方式，或系统设置为隐藏已知扩展名）时保留原扩展名
		if (!item.IsFolder && extension.Length > 0 && !item.DisplayName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
		{
			target += extension;
		}
		if (target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			MessageDialog.Show("重命名", "文件名不能包含下列任何字符：\n\\ / : * ? \" < > |", "确定");
			return;
		}
		var directory = Path.GetDirectoryName(item.FullPath);
		if (directory != null)
		{
			ShellFileOps.Rename(_hwnd, item.FullPath, Path.Combine(directory, target));
		}
		ScheduleRefresh();
	}

	TextBox? FindRenameBox(FenceItem item)
	{
		return ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container ? FindChild<TextBox>(container, "RenameBox") : null;
	}

	static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if (child is T match && match.Name == name)
			{
				return match;
			}
			var nested = FindChild<T>(child, name);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	/// <summary>
	/// 分区平时不抢焦点，需要键盘输入（重命名）时才主动激活。
	/// </summary>
	void ActivateForInput()
	{
		if (!IsActive)
		{
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
			_dropPaths = GetDropPaths(e.Data);
			SetDropHighlight(true);
			if (Model.RolledUp && !_tempExpanded)
			{
				_tempExpanded = true;
				ApplyBounds();
			}
		}
		e.Effects = ComputeDropEffect(e);
		if (firstEnter)
		{
			HelperDragEnter(e);
		}
		else
		{
			HelperDragOver(e);
		}
		e.Handled = true;
	}

	protected override void OnDragOver(DragEventArgs e)
	{
		base.OnDragOver(e);
		_dragVersion++;
		e.Effects = ComputeDropEffect(e);
		HelperDragOver(e);
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
				EndDragInside(true);
			}
		}, DispatcherPriority.Input);
		e.Handled = true;
	}

	protected override void OnDrop(DragEventArgs e)
	{
		base.OnDrop(e);
		_dragVersion++;
		var effect = ComputeDropEffect(e);
		var paths = _dropPaths;
		HelperDrop(e, effect);
		EndDragInside(false);
		e.Handled = true;
		if (paths == null || effect == DragDropEffects.None)
		{
			e.Effects = DragDropEffects.None;
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
		_dropPaths = null;
		SetDropHighlight(false);
		if (notifyHelper)
		{
			HelperDragLeave();
		}
		if (_tempExpanded)
		{
			StartHoverTimer(false, 800);
		}
	}

	/// <summary>
	/// 默认动作与资源管理器一致：同盘或来自桌面/其他分区时移动，跨盘复制；Ctrl 复制、Shift 移动、Alt 创建快捷方式。
	/// </summary>
	DragDropEffects ComputeDropEffect(DragEventArgs e)
	{
		var paths = _dropPaths;
		var folder = Model.FolderPath;
		if (paths == null || paths.Length == 0 || !Directory.Exists(folder))
		{
			return DragDropEffects.None;
		}
		// 在同一分区内拖动，或把文件夹拖进它自己（及其子目录）对应的分区，都不处理
		if (paths.All(p => PathUtil.AreEqual(Path.GetDirectoryName(p), folder))
				|| paths.Any(p => PathUtil.AreEqual(p, folder) || PathUtil.IsUnder(folder, p)))
		{
			return DragDropEffects.None;
		}
		var keys = e.KeyStates;
		bool ctrl = keys.HasFlag(DragDropKeyStates.ControlKey);
		bool shift = keys.HasFlag(DragDropKeyStates.ShiftKey);
		bool alt = keys.HasFlag(DragDropKeyStates.AltKey);
		DragDropEffects wanted;
		if (alt || (ctrl && shift))
		{
			wanted = DragDropEffects.Link;
		}
		else if (ctrl)
		{
			wanted = DragDropEffects.Copy;
		}
		else if (shift)
		{
			wanted = DragDropEffects.Move;
		}
		else
		{
			wanted = _manager.PrefersMove(paths, folder) ? DragDropEffects.Move : DragDropEffects.Copy;
		}
		if ((e.AllowedEffects & wanted) != 0)
		{
			return wanted;
		}
		foreach (var fallback in new[] { DragDropEffects.Move, DragDropEffects.Copy, DragDropEffects.Link })
		{
			if ((e.AllowedEffects & fallback) != 0)
			{
				return fallback;
			}
		}
		return DragDropEffects.None;
	}

	void PerformDrop(string[] paths, DragDropEffects effect)
	{
		try
		{
			if (effect == DragDropEffects.Move)
			{
				ShellFileOps.Move(_hwnd, paths, Model.FolderPath);
			}
			else if (effect == DragDropEffects.Copy)
			{
				ShellFileOps.Copy(_hwnd, paths, Model.FolderPath);
			}
			else if (effect == DragDropEffects.Link)
			{
				ShellFileOps.CreateShortcuts(paths, Model.FolderPath);
			}
		}
		catch (Exception ex)
		{
			Log.Error("拖放文件失败", ex);
			MessageDialog.Show("拖放失败", ex.Message, "确定");
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

	/// <summary>
	/// 以下 Helper 方法驱动系统的拖放预览图（与资源管理器一样显示文件缩略图和"移动到 xx"提示）。
	/// </summary>
	void HelperDragEnter(DragEventArgs e)
	{
		try
		{
			_dropHelper ??= (IDropTargetHelper)new DragDropHelper();
			if (e.Data is ComIDataObject data)
			{
				var point = NativeMethods.GetCursorPos();
				_dropHelper.DragEnter(_hwnd, data, ref point, (int)e.Effects);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("初始化拖放预览失败", ex);
			_dropHelper = null;
		}
	}

	void HelperDragOver(DragEventArgs e)
	{
		if (_dropHelper == null)
		{
			return;
		}
		var point = NativeMethods.GetCursorPos();
		_dropHelper.DragOver(ref point, (int)e.Effects);
	}

	void HelperDragLeave() => _dropHelper?.DragLeave();

	void HelperDrop(DragEventArgs e, DragDropEffects effect)
	{
		if (_dropHelper == null || e.Data is not ComIDataObject data)
		{
			return;
		}
		var point = NativeMethods.GetCursorPos();
		_dropHelper.Drop(data, ref point, (int)effect);
	}

	#endregion
}
