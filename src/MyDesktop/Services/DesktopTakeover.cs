using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Services;

/// <summary>
/// 接管桌面图标：运行期间隐藏资源管理器的图标视图，没进分区的图标由各显示器上的散放图标层画出。
/// 排列、顺序、位置、间距和图标大小都取自资源管理器的桌面视图，在桌面右键菜单「查看」「排序方式」里的改动随之生效；
/// 在图标层上拖动的位置写回资源管理器，退出后系统桌面的排列与运行时一致。
/// 退出时恢复资源管理器的图标，主程序被强制结束时由守护进程恢复。
/// </summary>
internal sealed class DesktopTakeover : IDisposable
{
	const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
	const int OBJID_WINDOW = 0;

	/// <summary>
	/// 先显示图标层、等图标加载一会儿再隐藏资源管理器的图标，切换时桌面不会闪空。
	/// </summary>
	static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(400);

	/// <summary>
	/// 缩放比例变化后，最多等资源管理器这么久按新比例重新排列图标；期间每隔一会儿读一次视图看排好没有。
	/// </summary>
	static readonly TimeSpan DpiRelayoutTimeout = TimeSpan.FromSeconds(4);

	static readonly TimeSpan DpiRelayoutPoll = TimeSpan.FromMilliseconds(500);

	static readonly TimeSpan RenameWait = TimeSpan.FromSeconds(5);

	/// <summary>
	/// 按首字母定位时，两次按键间隔超过它就重新开始匹配。
	/// </summary>
	static readonly TimeSpan TypeAheadTimeout = TimeSpan.FromSeconds(1);

	/// <summary>
	/// 拖到桌面的文件要等复制完、出现在桌面视图里才能摆到松手的位置，最多等这么久。
	/// </summary>
	static readonly TimeSpan PlacementWait = TimeSpan.FromSeconds(15);

	readonly FenceManager _manager;
	readonly Dispatcher _dispatcher;
	readonly List<DesktopLayerWindow> _layers = [];
	readonly DispatcherTimer _layoutTimer;
	readonly WinEventProc _objectCallback;
	readonly WinEventProc _menuEndCallback;
	RegistryWatcher? _advancedWatcher;
	Process? _guard;
	IntPtr _objectHook;
	IntPtr _focusHook;
	IntPtr _menuEndHook;
	IntPtr _handedOffEdit;
	uint _explorerProcess;
	bool _active;
	bool _layersHidden;
	bool _disposed;
	bool _dpiRelayoutPending;
	// 散放图标格子左上角的屏幕位置（物理像素），拖动后据此算出新位置；_order 是它们的排列顺序（逐列从上到下）
	Dictionary<string, POINT> _placement = new(StringComparer.OrdinalIgnoreCase);
	List<string> _order = [];
	POINT? _dragStart;
	// 键盘定位的起点（最近点选或键盘选中的图标）与按首字母定位已输入的字符
	FenceItem? _anchor;
	string _typed = string.Empty;
	DateTime _typedAt;
	// 拖到桌面的文件出现后要摆到的位置
	(List<string> Keys, POINT Cursor, DateTime Until)? _pendingPlacement;
	int _hitRectCount = -1;
	string? _pendingRename;
	DateTime _pendingRenameUntil;
	// 一键整理动画里还没起飞的图标：已经归入分区，起飞前仍留在桌面原处显示
	readonly HashSet<string> _waitingToFly = new(StringComparer.OrdinalIgnoreCase);
	// 删除分区时正从分区飞回桌面的图标：已经排在桌面上，落地前先不显示
	readonly HashSet<string> _arriving = new(StringComparer.OrdinalIgnoreCase);

	public DesktopTakeover(FenceManager manager)
	{
		_manager = manager;
		_dispatcher = Dispatcher.CurrentDispatcher;
		_layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
		_layoutTimer.Tick += (_, _) =>
		{
			_layoutTimer.Stop();
			Relayout();
		};
		// 委托必须由字段持有，防止被 GC 回收
		_objectCallback = OnObjectEvent;
		_menuEndCallback = OnMenuEnded;
		Items = new DesktopItems(_dispatcher);
		Items.Changed += OnItemsChanged;
		Items.Renamed += _manager.OnItemRenamed;
		DesktopDrag.CurrentChanged += OnDragChanged;
		WatchAdvancedSettings();
		HookExplorer();
	}

	public DesktopItems Items { get; }

	/// <summary>
	/// 是否已隐藏资源管理器的图标、由图标层画出散放图标。读不到资源管理器的桌面视图时不接管。
	/// </summary>
	public bool IsActive => _active;

	/// <summary>
	/// 图标层当前是否应当显示：接管中、没有被「双击桌面隐藏」藏起来、用户也没有在系统里关闭「显示桌面图标」。
	/// </summary>
	public bool LayersVisible => _active && !_layersHidden && !_dpiRelayoutPending && !DesktopHost.IconsHiddenBySystem();

	public bool IsRenaming => _layers.Any(l => l.IsRenaming);

	/// <summary>
	/// 桌面上散放（没放进分区）的图标。
	/// </summary>
	public IEnumerable<FenceItem> LooseItems => _layers.SelectMany(l => l.Items);

	/// <summary>
	/// 这些图标此刻在桌面上的位置（物理像素）与图像，一键整理动画的起点；图标层没显示时为空。
	/// 取到的图标归入分区后仍留在桌面原处，直到 TakeOff 起飞。
	/// </summary>
	public Dictionary<string, (RECT Rect, ImageSource Icon)> CaptureIcons(IEnumerable<string> keys)
	{
		var result = new Dictionary<string, (RECT Rect, ImageSource Icon)>(StringComparer.OrdinalIgnoreCase);
		if (!LayersVisible)
		{
			return result;
		}
		foreach (var key in keys)
		{
			foreach (var layer in _layers)
			{
				if (layer.GetIcon(key) is { } icon)
				{
					result[key] = icon;
					_waitingToFly.Add(key);
					break;
				}
			}
		}
		return result;
	}

	/// <summary>
	/// 一键整理动画里图标起飞：桌面原处立即不再显示，随后从图标层移除。
	/// </summary>
	public void TakeOff(string key)
	{
		_waitingToFly.Remove(key);
		foreach (var layer in _layers)
		{
			if (layer.FindItem(key) is { } item)
			{
				item.IsInFlight = true;
			}
		}
		RelayoutSoon();
	}

	/// <summary>
	/// 删除分区时这些图标将飞回桌面：回到桌面上后先不显示，落地时再由 CompleteArrival 显示。
	/// </summary>
	public void ExpectArrivals(IEnumerable<string> keys) => _arriving.UnionWith(keys);

	public void CompleteArrival(string key)
	{
		_arriving.Remove(key);
		foreach (var layer in _layers)
		{
			if (layer.FindItem(key) is { } item)
			{
				item.IsInFlight = false;
			}
		}
	}

	/// <summary>
	/// 这些图标在资源管理器图标列表里的图像范围（屏幕物理像素），启动动画的起点：接管之前它们就显示在这些位置。
	/// 按图标层画图标的方式推算（格子里水平居中、贴着格子上沿），列表里没有的不在结果里。
	/// </summary>
	public Dictionary<string, RECT> ExplorerIconRects(IEnumerable<string> keys)
	{
		var result = new Dictionary<string, RECT>(StringComparer.OrdinalIgnoreCase);
		if (Items.Snapshot is not DesktopSnapshot snapshot)
		{
			return result;
		}
		var wanted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in snapshot.Items.Where(e => wanted.Contains(e.Key)))
		{
			var cell = ToCell(snapshot, entry.Position);
			var (_, scale) = GetMonitorWorkArea(MonitorFromPoint(cell, MONITOR_DEFAULTTONEAREST));
			int size = (int)Math.Round(snapshot.IconSize * scale);
			int left = cell.X + Math.Max(0, (snapshot.Spacing.X - size) / 2);
			int top = cell.Y + (int)Math.Round(4 / 3.0 * scale);
			result[entry.Key] = new RECT(left, top, left + size, top + size);
		}
		return result;
	}

	/// <summary>
	/// 图标在桌面上的位置（物理像素），删除分区动画的终点；图标层没显示或桌面上没有这个图标时返回 null。
	/// </summary>
	public RECT? GetIconRect(string key)
	{
		if (!LayersVisible)
		{
			return null;
		}
		foreach (var layer in _layers)
		{
			if (layer.GetIconRect(key) is RECT rect)
			{
				return rect;
			}
		}
		return null;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		DesktopDrag.CurrentChanged -= OnDragChanged;
		_layoutTimer.Stop();
		UnhookExplorer();
		_advancedWatcher?.Dispose();
		if (_active && !DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}
		_active = false;
		StopGuard();
		foreach (var layer in _layers)
		{
			layer.CloseForReal();
		}
		_layers.Clear();
		DesktopHost.SetLayers([], []);
		Items.Dispose();
	}

	#region 接管与恢复

	void OnItemsChanged()
	{
		if (_disposed)
		{
			return;
		}
		if (!_active && Items.Snapshot != null)
		{
			Activate();
		}
		_manager.OnDesktopItemsChanged();
		Relayout();
		TryPendingRename();
		PlacePending();
	}

	void Activate()
	{
		_active = true;
		EnsureLayers();
		Relayout();
		ApplyLayerVisibility(false);
		var timer = new DispatcherTimer { Interval = HideDelay };
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			if (_active && !_disposed)
			{
				// 启动后第一次接管时，分区里的图标从资源管理器图标列表里的原处飞进分区：先放好飞行的图标再藏起列表
				_manager.OnDesktopTakenOver();
				DesktopHost.SetIconsVisible(false);
			}
		};
		timer.Start();
		StartGuard();
		Items.ClearExplorerSelection();
		_manager.ApplyMouseHookSettings();
		Log.Info($"已接管桌面图标，共 {Items.Snapshot?.Items.Count} 个项目");
	}

	/// <summary>
	/// 由管理器定时调用：资源管理器重启后重新隐藏它的图标并重新挂钩，守护进程意外退出时重新启动。
	/// </summary>
	public void Watchdog()
	{
		if (_disposed)
		{
			return;
		}
		Items.CheckFolders();
		var desktop = DesktopHost.FindDesktopWindow();
		if (desktop != IntPtr.Zero)
		{
			GetWindowThreadProcessId(desktop, out uint process);
			if (process != _explorerProcess)
			{
				HookExplorer();
				Items.RefreshSoon();
			}
		}
		if (!_active)
		{
			return;
		}
		// 缩放比例变化后正在让资源管理器的图标列表短暂显示、重新排列，这时不要把它藏回去
		if (DesktopHost.AreIconsVisible() && !DesktopHost.IconsHiddenBySystem() && !_dpiRelayoutPending)
		{
			DesktopHost.SetIconsVisible(false);
			Items.RefreshSoon();
		}
		ApplyLayerVisibility(true);
		if (_guard is { HasExited: true })
		{
			StartGuard();
		}
	}

	/// <summary>
	/// 缩放比例变化：资源管理器隐藏着的图标列表不会按新比例重新排列（实测间距一直停在旧比例，显示出来才更新），
	/// 而且它处理缩放变化比图标层晚。先藏起图标层、让它的图标列表显示出来，反复读取视图，
	/// 等排列变了并且稳定下来（最多等 4 秒）再藏回去、重新接管；几个显示器的图标层同时收到通知时只做一次。
	/// </summary>
	public void OnDpiChanged()
	{
		if (!_active || _dpiRelayoutPending)
		{
			return;
		}
		if (DesktopHost.IconsHiddenBySystem())
		{
			_manager.RefreshWindowsDpi();
			OnDisplayChanged();
			return;
		}
		_dpiRelayoutPending = true;
		var before = Items.Snapshot?.Signature;
		string? last = null;
		var started = DateTime.UtcNow;
		ApplyLayerVisibility(false);
		DesktopHost.SetIconsVisible(true);
		var timer = new DispatcherTimer { Interval = DpiRelayoutPoll };
		timer.Tick += (_, _) =>
		{
			var current = Items.Snapshot?.Signature;
			bool settled = current != null && current != before && current == last;
			last = current;
			if (!_disposed && !settled && DateTime.UtcNow - started < DpiRelayoutTimeout)
			{
				Items.RefreshSoon();
				return;
			}
			timer.Stop();
			_dpiRelayoutPending = false;
			if (_disposed)
			{
				return;
			}
			Log.Info($"缩放比例变化，资源管理器重新排列图标用时 {(DateTime.UtcNow - started).TotalSeconds:0.0} 秒{(settled ? string.Empty : "（等待超时）")}");
			if (_active && !DesktopHost.IconsHiddenBySystem())
			{
				DesktopHost.SetIconsVisible(false);
			}
			// 这时各窗口的缩放通知都已处理完，分区窗口统一按新比例重新摆放
			_manager.RefreshWindowsDpi();
			OnDisplayChanged();
		};
		timer.Start();
		Items.RefreshSoon();
	}

	public void OnDisplayChanged()
	{
		if (!_active)
		{
			return;
		}
		EnsureLayers();
		ApplyLayerVisibility(false);
		Relayout();
		// 资源管理器会按新的分辨率重新排列图标，稍后再读一次
		Items.RefreshSoon();
	}

	public void KeepLayersAboveDesktop()
	{
		foreach (var layer in _layers)
		{
			if (layer.IsVisible && DesktopHost.GetInsertAfterAboveDesktop(layer.Handle) != null)
			{
				layer.PlaceAboveDesktop();
			}
		}
	}

	/// <summary>
	/// 「双击桌面隐藏」隐藏或显示散放图标。
	/// </summary>
	public void SetLayersHidden(bool hidden)
	{
		_layersHidden = hidden;
		ApplyLayerVisibility(true);
	}

	void ApplyLayerVisibility(bool fade)
	{
		bool visible = LayersVisible;
		foreach (var layer in _layers)
		{
			if (visible && (!layer.IsVisible || layer.Opacity < 1))
			{
				if (fade)
				{
					layer.FadeIn();
				}
				else
				{
					layer.ShowOnDesktop();
				}
			}
			else if (!visible && layer.IsVisible)
			{
				layer.ClearSelection();
				if (fade)
				{
					layer.FadeOut();
				}
				else
				{
					layer.Hide();
				}
			}
		}
		_dispatcher.InvokeAsync(UpdateHitRects, DispatcherPriority.Loaded);
	}

	void StartGuard()
	{
		StopGuard();
		try
		{
			_guard = DesktopGuard.Start();
		}
		catch (Exception ex)
		{
			Log.Warn("启动守护进程失败", ex);
		}
	}

	void StopGuard()
	{
		try
		{
			if (_guard is { HasExited: false })
			{
				_guard.Kill();
			}
		}
		catch (Exception ex)
		{
			Log.Warn("结束守护进程失败", ex);
		}
		_guard?.Dispose();
		_guard = null;
	}

	/// <summary>
	/// 「显示桌面图标」「隐藏的项目」「文件扩展名」等设置都记在这个键里，变化时更新图标层的显示与内容。
	/// </summary>
	void WatchAdvancedSettings()
	{
		try
		{
			var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
			if (key == null)
			{
				return;
			}
			_advancedWatcher = new RegistryWatcher(key);
			_advancedWatcher.Changed += () => _dispatcher.BeginInvoke(() =>
			{
				if (_disposed)
				{
					return;
				}
				ApplyLayerVisibility(true);
				Items.RefreshSoon();
			});
		}
		catch (Exception ex)
		{
			Log.Warn("监视资源管理器设置失败", ex);
		}
	}

	/// <summary>
	/// 监视资源管理器进程的几类事件：窗口创建、显示与获得焦点（图标视图被它自己重新显示、开始在隐藏的视图里改名），
	/// 以及菜单关闭（可能改了查看方式或排序）。隐藏视图里的改名框不会触发「显示」事件，要靠创建和获得焦点来发现。
	/// </summary>
	void HookExplorer()
	{
		UnhookExplorer();
		var desktop = DesktopHost.FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return;
		}
		GetWindowThreadProcessId(desktop, out _explorerProcess);
		_objectHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _objectCallback, _explorerProcess, 0, WINEVENT_OUTOFCONTEXT);
		_focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _objectCallback, _explorerProcess, 0, WINEVENT_OUTOFCONTEXT);
		_menuEndHook = SetWinEventHook(EVENT_SYSTEM_MENUPOPUPEND, EVENT_SYSTEM_MENUPOPUPEND, IntPtr.Zero, _menuEndCallback, _explorerProcess, 0,
				WINEVENT_OUTOFCONTEXT);
	}

	void UnhookExplorer()
	{
		foreach (var hook in new[] { _objectHook, _focusHook, _menuEndHook })
		{
			if (hook != IntPtr.Zero)
			{
				UnhookWinEvent(hook);
			}
		}
		_objectHook = IntPtr.Zero;
		_focusHook = IntPtr.Zero;
		_menuEndHook = IntPtr.Zero;
	}

	void OnObjectEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
	{
		// 由系统直接回调，异常必须就地捕获，否则进程会被终止
		try
		{
			if (!_active || _disposed || hwnd == IntPtr.Zero || eventType == EVENT_OBJECT_DESTROY)
			{
				return;
			}
			var folderView = DesktopHost.FindFolderView();
			if (eventType == EVENT_OBJECT_SHOW && objectId == OBJID_WINDOW && hwnd == folderView)
			{
				// 缩放比例变化后是我们自己让它显示出来重新排列的，排好之前不要藏回去
				if (_dpiRelayoutPending)
				{
					return;
				}
				// 资源管理器自己又显示了图标视图（例如重新勾选了「显示桌面图标」），立即重新隐藏
				if (!DesktopHost.IconsHiddenBySystem())
				{
					DesktopHost.SetIconsVisible(false);
				}
				ApplyLayerVisibility(true);
				Items.RefreshSoon();
			}
			else if (hwnd != _handedOffEdit && GetParent(hwnd) == folderView && GetClassName(hwnd) == "Edit")
			{
				// 同一个改名框会先后触发创建、获得焦点等多个事件，只接手一次
				_handedOffEdit = hwnd;
				HandOffRename(folderView);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("处理资源管理器窗口事件失败", ex);
		}
	}

	void OnMenuEnded(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
	{
		try
		{
			Items.RefreshSoon();
		}
		catch (Exception ex)
		{
			Log.Warn("处理菜单关闭事件失败", ex);
		}
	}

	/// <summary>
	/// 资源管理器在隐藏的图标视图里开始改名（桌面右键「新建」文件夹等）：取消它，改在本程序这边对同一项目改名。
	/// </summary>
	void HandOffRename(IntPtr folderView)
	{
		PostMessage(folderView, LVM_CANCELEDITLABEL, IntPtr.Zero, IntPtr.Zero);
		Items.QueryFocused(key =>
		{
			if (key != null)
			{
				RequestRename(key);
			}
		});
	}

	/// <summary>
	/// 等项目出现在图标层或分区中后开始改名（新建的项目要等资源管理器更新视图后才读得到）。
	/// </summary>
	public void RequestRename(string key)
	{
		_pendingRename = key;
		_pendingRenameUntil = DateTime.Now + RenameWait;
		Items.RefreshSoon();
		TryPendingRename();
	}

	void TryPendingRename()
	{
		if (_pendingRename is not string key)
		{
			return;
		}
		if (DateTime.Now > _pendingRenameUntil)
		{
			_pendingRename = null;
			return;
		}
		foreach (var layer in _layers)
		{
			if (layer.Find(key) is FenceItem item && !item.IsVirtual)
			{
				_pendingRename = null;
				layer.BeginRename(item);
				return;
			}
		}
		if (_manager.TryBeginRenameInFence(key))
		{
			_pendingRename = null;
		}
	}

	#endregion

	#region 图标层与排布

	/// <summary>
	/// 每个显示器一个图标层，覆盖其工作区。
	/// </summary>
	void EnsureLayers()
	{
		var monitors = new List<IntPtr>();
		EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
		{
			monitors.Add(monitor);
			return true;
		}, IntPtr.Zero);
		foreach (var layer in _layers.Where(l => !monitors.Contains(l.Monitor)).ToList())
		{
			layer.CloseForReal();
			_layers.Remove(layer);
		}
		foreach (var monitor in monitors)
		{
			var layer = _layers.FirstOrDefault(l => l.Monitor == monitor);
			if (layer == null)
			{
				layer = new DesktopLayerWindow(this, monitor);
				_layers.Add(layer);
			}
			var (work, scale) = GetMonitorWorkArea(monitor);
			layer.Place(work, scale);
		}
		DesktopHost.SetLayers(_layers.Select(l => l.Handle), []);
	}

	/// <summary>
	/// 分区移动、缩放、卷起或成员变化后重新排布散放图标（合并短时间内的多次请求）。
	/// </summary>
	public void RelayoutSoon()
	{
		_layoutTimer.Stop();
		_layoutTimer.Start();
	}

	/// <summary>
	/// 立即重新排布，删除分区动画要马上知道图标回到桌面的哪一格。
	/// </summary>
	public void RelayoutNow()
	{
		_layoutTimer.Stop();
		Relayout();
	}

	void Relayout()
	{
		var snapshot = Items.Snapshot;
		if (!_active || snapshot == null || _layers.Count == 0)
		{
			return;
		}
		var fenced = _manager.FencedKeys();
		fenced.ExceptWith(_waitingToFly);
		var loose = snapshot.Items.Where(e => !fenced.Contains(e.Key)).ToList();
		var placed = snapshot.AutoArrange
				? Arrange(snapshot, loose)
				: loose.Select(e => (Entry: e, Cell: ToCell(snapshot, e.Position))).ToList();
		var placement = new Dictionary<string, POINT>(StringComparer.OrdinalIgnoreCase);
		var groups = _layers.ToDictionary(l => l, _ => new List<(DesktopEntry Entry, POINT Cell)>());
		foreach (var item in placed)
		{
			placement.TryAdd(item.Entry.Key, item.Cell);
			var probe = new POINT(item.Cell.X + snapshot.Spacing.X / 2, item.Cell.Y + snapshot.Spacing.Y / 4);
			groups[LayerAt(probe)].Add(item);
		}
		_placement = placement;
		_order = placed.Select(p => p.Entry.Key).ToList();
		foreach (var (layer, items) in groups)
		{
			layer.SetItems(items, snapshot.Spacing, snapshot.IconSize);
			foreach (var item in layer.Items.Where(i => _arriving.Contains(i.FullPath)))
			{
				item.IsInFlight = true;
			}
		}
		ApplyCutState();
		_dispatcher.InvokeAsync(UpdateHitRects, DispatcherPriority.Loaded);
	}

	/// <summary>
	/// 自动排列：按资源管理器的顺序（逐列从上到下）去掉分区里的项目，再从资源管理器的首格起按列依次排开，
	/// 跳过被分区挡住的格子；间距与每列行数都与资源管理器一致。
	/// </summary>
	List<(DesktopEntry Entry, POINT Cell)> Arrange(DesktopSnapshot snapshot, List<DesktopEntry> loose)
	{
		var spacing = snapshot.Spacing;
		var list = snapshot.ListBounds;
		if (loose.Count == 0 || spacing.X <= 0 || spacing.Y <= 0)
		{
			return loose.Select(e => (e, new POINT(list.Left + e.Position.X, list.Top + e.Position.Y))).ToList();
		}
		// 首格是资源管理器排出的左上角，即使原本在那里的图标已放进分区，格子的位置也不变
		var origin = new POINT(list.Left + snapshot.Items.Min(e => e.Position.X), list.Top + snapshot.Items.Min(e => e.Position.Y));
		var (work, _) = GetMonitorWorkArea(MonitorFromPoint(origin, MONITOR_DEFAULTTONEAREST));
		int rows = Math.Max(1, (work.Bottom - origin.Y) / spacing.Y);
		var blocked = _manager.FenceLayoutBounds();
		// 分区边缘只压到格子一点点时不算挡住
		int insetX = spacing.X / 8;
		int insetY = spacing.Y / 8;
		int limit = loose.Count + rows * 100;
		int cell = 0;
		var result = new List<(DesktopEntry Entry, POINT Cell)>(loose.Count);
		foreach (var entry in loose.OrderBy(e => e.Position.X).ThenBy(e => e.Position.Y))
		{
			POINT point;
			do
			{
				point = new POINT(origin.X + cell / rows * spacing.X, origin.Y + cell % rows * spacing.Y);
				cell++;
			}
			while (cell < limit && blocked.Any(b => b.IntersectsWith(new RECT(point.X + insetX, point.Y + insetY, point.X + spacing.X - insetX, point.Y + spacing.Y - insetY))));
			result.Add((entry, point));
		}
		return result;
	}

	/// <summary>
	/// 把资源管理器报告的位置换算成格子左上角（屏幕物理像素）。自动排列时报告的就是格子左上角；
	/// 关闭自动排列后报告的是图标图像的左边缘、比图标顶端高约 2 DIP 处（实测），要减去图标在格子里水平居中的距离并往下移。
	/// </summary>
	static POINT ToCell(DesktopSnapshot snapshot, POINT position)
	{
		var list = snapshot.ListBounds;
		var point = new POINT(list.Left + position.X, list.Top + position.Y);
		if (snapshot.AutoArrange)
		{
			return point;
		}
		var (dx, dy) = FreeOffset(snapshot, point);
		return new POINT(point.X - dx, point.Y + dy);
	}

	/// <summary>
	/// 格子左上角（屏幕物理像素）换算回资源管理器的位置坐标，用于写回。
	/// </summary>
	static POINT ToPosition(DesktopSnapshot snapshot, POINT cell)
	{
		var list = snapshot.ListBounds;
		var (dx, dy) = snapshot.AutoArrange ? (0, 0) : FreeOffset(snapshot, cell);
		return new POINT(cell.X + dx - list.Left, cell.Y - dy - list.Top);
	}

	static (int Dx, int Dy) FreeOffset(DesktopSnapshot snapshot, POINT near)
	{
		var (_, scale) = GetMonitorWorkArea(MonitorFromPoint(near, MONITOR_DEFAULTTONEAREST));
		int iconPixels = (int)Math.Round(snapshot.IconSize * scale);
		return (Math.Max(0, (snapshot.Spacing.X - iconPixels) / 2), (int)Math.Round(2 * scale));
	}

	DesktopLayerWindow LayerAt(POINT point)
	{
		var layer = _layers.FirstOrDefault(l => l.WorkArea.Contains(point));
		if (layer != null)
		{
			return layer;
		}
		var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
		return _layers.FirstOrDefault(l => l.Monitor == monitor) ?? _layers[0];
	}

	void UpdateHitRects()
	{
		if (_disposed)
		{
			return;
		}
		var rects = _layers.SelectMany(l => l.GetIconRects()).ToList();
		DesktopHost.SetLayers(_layers.Select(l => l.Handle), rects);
		if (rects.Count != _hitRectCount)
		{
			_hitRectCount = rects.Count;
			Log.Info($"散放图标层显示 {rects.Count} 个图标");
		}
	}

	public void ReloadIcons()
	{
		foreach (var layer in _layers)
		{
			layer.ReloadIcons();
		}
	}

	/// <summary>
	/// 剪贴板里被剪切的图标半透明显示。
	/// </summary>
	public void ApplyCutState()
	{
		foreach (var item in _layers.SelectMany(l => l.Items))
		{
			item.IsCut = _manager.IsCut(item.FullPath);
		}
	}

	#endregion

	#region 选择、按键与文件操作

	/// <summary>
	/// 与系统桌面一样，多个显示器上的图标共用一个选择：在某一层上点击（不带 Ctrl/Shift）时取消其他层的选择。
	/// 点中的图标同时成为键盘定位的起点。
	/// </summary>
	public void OnLayerPressed(DesktopLayerWindow layer, bool additive, FenceItem? item = null)
	{
		if (item != null)
		{
			_anchor = item;
		}
		if (additive)
		{
			return;
		}
		// 单选桌面上的图标时，分区里的选择也一并取消
		_manager.OnSelectionScopeActivated(null);
		foreach (var other in _layers.Where(l => l != layer))
		{
			other.ClearSelection();
		}
	}

	/// <summary>
	/// 在桌面空白处按下左键：取消选择并准备框选。
	/// </summary>
	public void OnBlankPressed(bool additive)
	{
		foreach (var layer in _layers)
		{
			layer.BeginBand(additive);
		}
		Items.ClearExplorerSelection();
	}

	public void OnBandUpdated(RECT rect)
	{
		foreach (var layer in _layers.Where(l => l.IsVisible))
		{
			layer.UpdateBand(rect);
		}
	}

	/// <summary>
	/// 取消所有显示器上桌面图标的选择（操作分区时调用）。
	/// </summary>
	public void ClearSelection()
	{
		foreach (var layer in _layers)
		{
			layer.ClearSelection();
		}
	}

	/// <summary>
	/// 图标层上有图标被单选（键盘、新建后选中等，不只是鼠标点击）：取消分区里的选择。
	/// </summary>
	public void OnLayerSelected() => _manager.OnSelectionScopeActivated(null);

	/// <summary>
	/// 所有显示器上选中的桌面图标。
	/// </summary>
	public List<FenceItem> SelectedItems => _layers.SelectMany(l => l.SelectedItems).ToList();

	/// <summary>
	/// 整个桌面选中的图标，包括分区里一起选中的。
	/// </summary>
	public List<FenceItem> AllSelectedItems() => _manager.AllSelectedItems();

	/// <summary>
	/// 从桌面上发起右键菜单、属性、拖动时实际作用的图标，见 FenceManager.SelectionForShell。
	/// </summary>
	public (List<FenceItem> Items, bool Desktop) SelectionForShell() => _manager.SelectionForShell(null);

	public void OnBandFinished()
	{
		foreach (var layer in _layers)
		{
			layer.EndBand();
		}
	}

	/// <summary>
	/// 右键框选松开后，与资源管理器一样在鼠标处弹出框住的图标的右键菜单；没框住图标时返回 false，由调用方弹出桌面右键菜单。
	/// </summary>
	public bool ShowMenuForBand(POINT point)
	{
		var layer = _layers.FirstOrDefault(l => l.SelectedItems.Count > 0);
		if (layer == null)
		{
			return false;
		}
		var (items, desktop) = SelectionForShell();
		layer.ShowItemMenu(items, desktop, point, layer.SelectedItems[0]);
		return true;
	}

	/// <summary>
	/// 按键命令：来自获得焦点的图标层，或桌面在前台时被键盘钩子拦下的按键。
	/// </summary>
	/// <param name="shift">是否按着 Shift（方向键追加选择）。</param>
	public void HandleKey(DesktopKey key, bool shift = false)
	{
		var selected = _layers.SelectMany(l => l.SelectedItems).ToList();
		// 打开、删除、复制、剪切作用于整个桌面选中的图标，包括分区里一起选中的
		var all = _manager.AllSelectedItems();
		var owner = _layers.FirstOrDefault(l => l.SelectedItems.Count > 0) ?? _layers.FirstOrDefault();
		if (owner == null)
		{
			return;
		}
		Action action = key switch
		{
			DesktopKey.Open => () => Open(all, owner.Handle),
			DesktopKey.Delete => () => ItemOps.Delete(owner.Handle, all, false),
			DesktopKey.DeletePermanently => () => ItemOps.Delete(owner.Handle, all, true),
			DesktopKey.Rename => () => BeginRename(selected.FirstOrDefault(i => i.CanRename)),
			DesktopKey.SelectAll => SelectAllOnDesktop,
			DesktopKey.Copy => () => ItemOps.CopyToClipboard(all, false),
			DesktopKey.Cut => () => ItemOps.CopyToClipboard(all, true),
			DesktopKey.ContextMenu => () => ShowMenuForSelection(owner, selected),
			DesktopKey.Properties => () => ShowPropertiesForSelection(owner),
			DesktopKey.Left or DesktopKey.Up or DesktopKey.Right or DesktopKey.Down or DesktopKey.Home or DesktopKey.End => () => Navigate(key, shift),
		};
		action();
		if (key is not (DesktopKey.Left or DesktopKey.Up or DesktopKey.Right or DesktopKey.Down or DesktopKey.Home or DesktopKey.End))
		{
			Items.RefreshSoon();
		}
	}

	/// <summary>
	/// 方向键、Home、End 在桌面图标之间移动选择（跨显示器），按住 Shift 时追加选择。
	/// </summary>
	void Navigate(DesktopKey key, bool extend)
	{
		var all = OrderedItems();
		if (all.Count == 0)
		{
			return;
		}
		var spacing = Items.Snapshot?.Spacing ?? new POINT(1, 1);
		var current = CurrentItem(all);
		var target = key switch
		{
			DesktopKey.Home => all[0],
			DesktopKey.End => all[^1],
			_ => current is { } from ? Neighbor(all, from, key, spacing) : all[0],
		};
		if (target is { } found)
		{
			SelectItem(found, extend);
		}
	}

	/// <summary>
	/// 输入字母或数字时选中名称以它开头的图标：快速连续输入按整个前缀匹配，连按同一个字母依次跳到下一个。
	/// </summary>
	public void TypeAhead(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		var now = DateTime.Now;
		if (now - _typedAt > TypeAheadTimeout)
		{
			_typed = string.Empty;
		}
		_typedAt = now;
		_typed += text;
		var all = OrderedItems();
		if (all.Count == 0)
		{
			return;
		}
		bool repeated = _typed.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_typed[0]));
		var prefix = repeated ? _typed[..1] : _typed;
		int start = CurrentItem(all) is { } current ? all.FindIndex(i => i.Item == current.Item) : -1;
		// 新开始的输入或连按同一个字母从下一个图标找起，继续输入的前缀从当前图标找起
		int offset = repeated ? 1 : 0;
		for (int i = 0; i < all.Count; i++)
		{
			var candidate = all[((start < 0 ? 0 : start + offset) + i) % all.Count];
			if (candidate.Item.DisplayName.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
			{
				SelectItem(candidate, false);
				return;
			}
		}
	}

	/// <summary>
	/// 散放图标按排列顺序（逐列从上到下）及其所在的图标层、格子位置。
	/// </summary>
	List<(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)> OrderedItems()
	{
		var result = new List<(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)>(_order.Count);
		foreach (var key in _order)
		{
			foreach (var layer in _layers)
			{
				if (layer.Find(key) is FenceItem item)
				{
					result.Add((layer, item, layer.CellOrigin(item)));
					break;
				}
			}
		}
		return result;
	}

	/// <summary>
	/// 键盘定位的当前图标：最近点选或键盘选中的那个（仍被选中时），否则是排在最前的选中图标。
	/// </summary>
	(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)? CurrentItem(List<(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)> all)
	{
		var selected = all.Where(i => i.Layer.SelectedItems.Contains(i.Item)).ToList();
		if (selected.FirstOrDefault(i => i.Item == _anchor) is { Item: not null } anchor)
		{
			return anchor;
		}
		return selected.Count > 0 ? selected[0] : null;
	}

	/// <summary>
	/// 指定方向上最近的图标：上下要求在同一列；左右优先同一行，同一行没有时取该方向上最近的。
	/// </summary>
	static (DesktopLayerWindow Layer, FenceItem Item, POINT Cell)? Neighbor(List<(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)> all,
			(DesktopLayerWindow Layer, FenceItem Item, POINT Cell) from, DesktopKey key, POINT spacing)
	{
		(DesktopLayerWindow Layer, FenceItem Item, POINT Cell)? best = null;
		long bestScore = long.MaxValue;
		foreach (var candidate in all)
		{
			if (candidate.Item == from.Item)
			{
				continue;
			}
			long dx = candidate.Cell.X - from.Cell.X;
			long dy = candidate.Cell.Y - from.Cell.Y;
			long score;
			if (key is DesktopKey.Up or DesktopKey.Down)
			{
				if (Math.Sign(dy) != (key == DesktopKey.Down ? 1 : -1) || Math.Abs(dx) >= spacing.X / 2)
				{
					continue;
				}
				score = Math.Abs(dy);
			}
			else
			{
				if (Math.Sign(dx) != (key == DesktopKey.Right ? 1 : -1))
				{
					continue;
				}
				bool sameRow = Math.Abs(dy) < spacing.Y / 2;
				score = sameRow ? Math.Abs(dx) : 1_000_000 + dx * dx + dy * dy;
			}
			if (score < bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}
		return best;
	}

	void SelectItem((DesktopLayerWindow Layer, FenceItem Item, POINT Cell) target, bool extend)
	{
		if (!extend)
		{
			foreach (var layer in _layers.Where(l => l != target.Layer))
			{
				layer.ClearSelection();
			}
		}
		target.Layer.SelectFromKeyboard(target.Item, extend);
		_anchor = target.Item;
	}

	public void Open(List<FenceItem> items, IntPtr hwnd) => ItemOps.Open(hwnd, items, true);

	public void Paste(IntPtr hwnd)
	{
		ItemOps.Paste(hwnd, AppPaths.Desktop);
		Items.RefreshSoon();
	}

	public void OnRenamed(string oldPath, string newPath)
	{
		_manager.OnItemRenamed(oldPath, newPath);
		Items.RefreshSoon();
	}

	void BeginRename(FenceItem? item)
	{
		if (item != null && _layers.FirstOrDefault(l => l.Items.Contains(item)) is DesktopLayerWindow layer)
		{
			layer.BeginRename(item);
		}
	}

	/// <summary>
	/// 菜单键：在第一个选中的桌面图标处弹出整个桌面选中的图标的右键菜单（合不到一起时只对桌面上的，见 SelectionForShell）。
	/// </summary>
	void ShowMenuForSelection(DesktopLayerWindow owner, List<FenceItem> selected)
	{
		if (selected.Count == 0)
		{
			return;
		}
		var layer = _layers.FirstOrDefault(l => l.Items.Contains(selected[0])) ?? owner;
		var cell = layer.CellOrigin(selected[0]);
		var spacing = Items.Snapshot?.Spacing ?? new POINT(0, 0);
		var (items, desktop) = SelectionForShell();
		layer.ShowItemMenu(items, desktop, new POINT(cell.X + spacing.X / 2, cell.Y + spacing.Y / 2), selected[0]);
	}

	void ShowPropertiesForSelection(DesktopLayerWindow owner)
	{
		var (items, desktop) = SelectionForShell();
		ItemOps.ShowProperties(owner.Handle, items, desktop);
	}

	/// <summary>
	/// 全选只选桌面上的图标，分区里的选择取消，免得接着删除时连带分区里的图标。
	/// </summary>
	void SelectAllOnDesktop()
	{
		_manager.OnSelectionScopeActivated(null);
		_layers.ForEach(l => l.SelectAll());
	}

	#endregion

	#region 拖放

	void OnDragChanged()
	{
		bool capture = DesktopDrag.Current != null;
		foreach (var layer in _layers)
		{
			layer.SetCaptureDrops(capture);
		}
		if (!capture)
		{
			_dragStart = null;
		}
	}

	/// <summary>
	/// 从图标层拖动时记下按下鼠标的位置，松手时按位移算出各图标的新位置。
	/// </summary>
	public void OnLayerDragStarting(POINT pressPoint) => _dragStart = pressPoint;

	/// <summary>
	/// 图标层只在本程序发起拖动期间接收放置；外部拖来的文件只会落在图标上（空白处穿透给资源管理器），暂不处理。
	/// </summary>
	public DragDropEffects GetLayerDropEffect(DragEventArgs e)
	{
		var drag = DesktopDrag.Current;
		if (drag == null)
		{
			return DragDropEffects.None;
		}
		if (drag.FromDesktop)
		{
			return (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
		}
		return drag.Files.Count == 0 ? DragDropEffects.None : ItemOps.ChooseDropEffect(e, _manager.PrefersMove(drag.Files, AppPaths.Desktop));
	}

	/// <summary>
	/// 放到图标层上：桌面项目移出分区并移到松手的位置（写回资源管理器）；映射分区里的文件按资源管理器规则移动或复制到桌面。
	/// 返回回报给拖放源的效果：移动由本程序完成，回报 None，拖放源不再自行删除源文件。
	/// </summary>
	public DragDropEffects DropOnLayer(DragEventArgs e, DragDropEffects effect, IntPtr hwnd)
	{
		var drag = DesktopDrag.Current;
		if (drag == null || effect == DragDropEffects.None)
		{
			return DragDropEffects.None;
		}
		if (!drag.FromDesktop)
		{
			var paths = drag.Files.ToList();
			var cursor = NativeMethods.GetCursorPos();
			// 异步执行，避免拖放源界面卡住；新文件出现在桌面上后摆到松手的位置
			_dispatcher.InvokeAsync(() =>
			{
				var created = ItemOps.Transfer(hwnd, paths, AppPaths.Desktop, effect);
				if (created.Count > 0)
				{
					_pendingPlacement = (created, cursor, DateTime.Now + PlacementWait);
				}
				Items.RefreshSoon();
			});
			return effect == DragDropEffects.Move ? DragDropEffects.None : effect;
		}
		var keys = drag.Keys.Where(k => Items.Find(k) != null).ToList();
		if (keys.Count == 0)
		{
			return DragDropEffects.None;
		}
		// 从图标层拖动时也可能带着分区里一起选中的图标
		var fenced = _manager.FencedKeys();
		bool fromFence = drag.Source is FenceWindow || keys.Any(fenced.Contains);
		if (fromFence)
		{
			_manager.AssignToFence(null, keys);
		}
		MoveTo(keys, NativeMethods.GetCursorPos(), fromFence);
		return DragDropEffects.None;
	}

	/// <summary>
	/// 拖到桌面的文件全部出现在桌面视图里后，摆到松手的位置。
	/// </summary>
	void PlacePending()
	{
		if (_pendingPlacement is not { } pending)
		{
			return;
		}
		var (keys, cursor, until) = pending;
		if (DateTime.Now > until)
		{
			_pendingPlacement = null;
			return;
		}
		if (keys.Any(k => Items.Find(k) == null))
		{
			return;
		}
		_pendingPlacement = null;
		MoveTo(keys, cursor, true);
	}

	/// <summary>
	/// 把图标移到松手的位置：从图标层拖来的保持相对位置整体平移，从分区或别处拖来的从松手处往下依次排开。
	/// 自动排列开着时由资源管理器按位置重新排序。
	/// </summary>
	void MoveTo(List<string> keys, POINT cursor, bool fromFence)
	{
		if (Items.Snapshot is not DesktopSnapshot snapshot)
		{
			return;
		}
		var spacing = snapshot.Spacing;
		POINT? delta = !fromFence && _dragStart is POINT start ? new POINT(cursor.X - start.X, cursor.Y - start.Y) : null;
		// 在原地松手（没拖出拖动阈值）视为没有移动
		if (delta is POINT d && Math.Abs(d.X) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(d.Y) < GetSystemMetrics(SM_CYDRAG))
		{
			return;
		}
		var points = new List<POINT>(keys.Count);
		int stacked = 0;
		foreach (var key in keys)
		{
			POINT cell;
			if (delta is POINT offset && _placement.TryGetValue(key, out var origin))
			{
				cell = new POINT(origin.X + offset.X, origin.Y + offset.Y);
			}
			else
			{
				cell = new POINT(cursor.X - spacing.X / 2, cursor.Y - spacing.Y / 4 + stacked * spacing.Y);
				stacked++;
			}
			points.Add(ToPosition(snapshot, cell));
		}
		Items.Move(keys, points);
	}

	#endregion
}
