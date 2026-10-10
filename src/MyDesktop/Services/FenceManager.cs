using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Services;

/// <summary>
/// 管理全部分区窗口与全局功能：托盘菜单、显示/隐藏、桌面双击、布局保存、Explorer 重启恢复，以及桌面分区的成员归属。
/// </summary>
internal sealed class FenceManager
{
	const double DefaultWidthDip = 340;
	const double DefaultHeightDip = 240;

	/// <summary>
	/// 成员对应的文件消失这么久之后才移出分区：改名时旧路径会短暂消失，要等改名通知把成员换成新路径。
	/// </summary>
	static readonly TimeSpan MissingMemberGrace = TimeSpan.FromSeconds(10);

	static readonly HashSet<string> GeneratedFiles = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db" };

	readonly List<FenceWindow> _windows = [];
	readonly Dispatcher _dispatcher;
	readonly DispatcherTimer _saveTimer;
	readonly DispatcherTimer _watchdogTimer;
	readonly DispatcherTimer _zOrderTimer;
	readonly DispatcherTimer _unliftTimer;
	readonly DispatcherTimer _iconReloadTimer;
	readonly List<RegistryWatcher> _registryWatchers = [];
	readonly WinEventProc _foregroundCallback;
	readonly WinEventProc _reorderCallback;
	readonly IntPtr _rootWindow = GetDesktopWindow();
	IntPtr _foregroundHook;
	IntPtr _reorderHook;
	TrayIcon? _tray;
	DesktopMouseWatcher? _mouse;
	DrawFrameWindow? _drawFrame;
	SettingsWindow? _settingsWindow;
	SearchWindow? _searchWindow;
	DesktopTakeover? _takeover;
	readonly Dictionary<string, DateTime> _missingMembers = new(StringComparer.OrdinalIgnoreCase);
	// 剪贴板里被剪切的文件，显示成半透明
	HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);
	// 是否处于隐藏状态（双击切换）；下面两个是按「双击桌面隐藏」的对象实际生效的结果
	bool _hidden;
	bool _iconsHidden;
	bool _fencesHidden;
	bool _shutdown;
	// 启动后第一次接管桌面图标之前：分区里的图标先不显示，接管那一刻从资源管理器桌面上的原处飞进分区
	bool _flyInAtStartup;
	// 正在切换标签的标签组（见 ActivateTabAsync）
	readonly HashSet<FenceGroup> _switchingGroups = [];

	public FenceManager(AppSettings settings)
	{
		Settings = settings;
		ShellIconLoader.ShowShortcutArrows = settings.ShowShortcutArrows;
		_dispatcher = Dispatcher.CurrentDispatcher;
		_saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		_saveTimer.Tick += (_, _) => SaveNow();
		_watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
		_watchdogTimer.Tick += (_, _) => Watchdog();
		_zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
		_zOrderTimer.Tick += (_, _) =>
		{
			_zOrderTimer.Stop();
			KeepFencesAboveDesktop();
		};
		// 桌面被激活后多久撤销分区的临时置顶：资源管理器提起桌面通常在几十毫秒内完成，留足余量
		_unliftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
		_unliftTimer.Tick += (_, _) =>
		{
			_unliftTimer.Stop();
			DesktopHost.Unlift(_windows.Select(w => w.Handle));
			KeepFencesAboveDesktop();
		};
		_iconReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_iconReloadTimer.Tick += (_, _) =>
		{
			_iconReloadTimer.Stop();
			ReloadIcons();
		};
		// 委托必须由字段持有，防止被 GC 回收
		_foregroundCallback = OnForegroundChanged;
		_reorderCallback = OnZOrderChanged;
		Organizer = new DesktopOrganizer(this);
		Updater = new Updater(this);
	}

	public AppSettings Settings { get; }

	public DesktopOrganizer Organizer { get; }

	public Updater Updater { get; }

	/// <summary>
	/// 分区毛玻璃背景用的模糊壁纸。
	/// </summary>
	public WallpaperBlur Wallpaper { get; } = new();

	public IReadOnlyList<FenceWindow> Windows => _windows;

	public bool FencesHidden => _fencesHidden;

	/// <summary>
	/// 桌面上的全部项目（资源管理器桌面视图）；启动前为 null。
	/// </summary>
	public DesktopItems? DesktopItems => _takeover?.Items;

	/// <summary>
	/// 正在重命名分区或桌面上的项目（自动整理会等改完名再处理）。
	/// </summary>
	public bool IsRenaming => _takeover?.IsRenaming == true || _windows.Any(w => w.IsRenaming);

	public bool AllLocked => _windows.Count > 0 && _windows.All(w => w.Model.Locked);

	public void Start(bool firstRun)
	{
		_tray = new TrayIcon("MyDesktop 桌面分区", AppCommands.MessageName, AppCommands.StateMessageName)
		{
			StateProvider = () => AppCommands.EncodeState(Settings.DoubleClickTarget, Settings.DoubleClickToHide, Settings.DesktopContextMenu),
		};
		_tray.LeftClick += ShowSettings;
		_tray.RightClick += ShowTrayMenu;
		_tray.CommandReceived += code =>
		{
			if (Enum.IsDefined((AppCommand)code))
			{
				ExecuteCommand((AppCommand)code);
			}
		};
		_tray.TaskbarCreated += OnExplorerRestarted;
		_tray.DisplayChanged += () => _dispatcher.InvokeAsync(() =>
		{
			ApplyDisplayLayout();
			EnsureAllOnScreen();
			_takeover?.OnDisplayChanged();
			Wallpaper.Refresh();
		}, DispatcherPriority.Background);
		_tray.ThemeChanged += SystemTheme.ApplyToMenus;
		_tray.ClipboardChanged += () => _dispatcher.InvokeAsync(UpdateCutState, DispatcherPriority.Background);
		_tray.WallpaperChanged += Wallpaper.Refresh;
		_tray.HotkeyPressed += ToggleSearch;
		if (!ApplySearchHotkey())
		{
			_dispatcher.InvokeAsync(() => _tray?.ShowBalloon($"快捷键 {Settings.SearchHotkey} 已被其他程序占用", "搜索桌面图标的快捷键没有生效，可以在 MyDesktop 设置里换一个。"),
					DispatcherPriority.Background);
		}
		Wallpaper.Changed += RefreshAllAppearance;
		Wallpaper.Start();
		NotifyIfUpgraded(firstRun);
		// 旧版本和安装包写的开机自启 Run 项换成计划任务，不耽误接管桌面
		_dispatcher.InvokeAsync(AutoStart.MigrateRunValue, DispatcherPriority.Background);

		// 上次异常退出时桌面图标可能停留在隐藏状态，启动时先恢复
		if (!DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}

		ConvertLegacyFences();
		// 程序没运行时接上或拔掉过显示器的，先换成当前显示器组合下的布局
		if (SwitchDisplayLayout())
		{
			SaveSoon();
		}
		// 程序没运行时改过缩放比例的，先按新比例调整分区再打开
		if (AdaptLayoutToDpi())
		{
			SaveSoon();
		}
		if (NormalizeGroups())
		{
			SaveSoon();
		}
		// 打开分区之前就监视前台切换和层级变化：安装程序关闭等让桌面窗口提到最前的变化，要是发生在打开分区的那一会儿，
		// 分区会被压在桌面下面没人纠正，要等看门狗或点一下桌面才露出来
		_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundCallback, 0, 0,
				WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		_reorderHook = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER, IntPtr.Zero, _reorderCallback, 0, 0,
				WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		ApplyStartupHidden();
		// 系统关掉了「显示桌面图标」时桌面上没有图标可以飞；启动时就藏起了图标或分区的也不播
		_flyInAtStartup = !DesktopHost.IconsHiddenBySystem() && !_iconsHidden && !_fencesHidden;
		foreach (var model in Settings.Fences.ToList())
		{
			OpenWindow(model);
		}
		RefreshAllTabs();
		EnsureAllOnScreen();
		// 要播启动动画时先等桌面显示出来、分区里的图标加载好，再接管桌面图标
		if (_flyInAtStartup)
		{
			_ = StartTakeoverWhenReadyAsync();
		}
		else
		{
			StartTakeover();
		}
		UpdateCutState();
		ApplyMouseHookSettings();
		Organizer.ApplyWatchSetting();
		WatchElevationSettings();
		DesktopMenu.Apply(Settings.DesktopContextMenu);
		_watchdogTimer.Start();
		// 启动这一阵子别的程序（比如刚装完点「完成」的安装程序）还在关闭、切换前台，稍后再校正一次层级
		_zOrderTimer.Stop();
		_zOrderTimer.Start();
		// 启动一分钟、各种加载和动画都结束后记一次内存，排查占用时对照
		var memoryLog = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
		memoryLog.Tick += (_, _) =>
		{
			memoryLog.Stop();
			LogMemory();
		};
		memoryLog.Start();
		// 用 --data 启动的测试实例不自动检查更新，免得和正式实例重复提示
		if (!AppPaths.IsCustomDataDir)
		{
			Updater.Start();
		}
		Log.Info($"启动完成，共 {_windows.Count} 个分区");

		if (firstRun)
		{
			_dispatcher.InvokeAsync(ShowWelcome, DispatcherPriority.Background);
		}
	}

	void LogMemory()
	{
		using var process = Process.GetCurrentProcess();
		var gc = GC.GetGCMemoryInfo();
		Log.Info($"内存：私有 {process.PrivateMemorySize64 / 1048576} MB，工作集 {process.WorkingSet64 / 1048576} MB，"
				+ $"托管堆 {GC.GetTotalMemory(false) / 1048576} MB（已提交 {gc.TotalCommittedBytes / 1048576} MB），"
				+ $"分区窗口 {_windows.Count} 个，硬件加速{(Settings.HardwareAcceleration ? "开" : "关")}");
	}

	/// <summary>
	/// 接管桌面图标：读到资源管理器的桌面视图后隐藏系统图标、由图标层画出散放图标。
	/// </summary>
	void StartTakeover()
	{
		if (_shutdown)
		{
			return;
		}
		_takeover = new DesktopTakeover(this);
		// 接管之前就藏起了桌面图标（启动时隐藏，或等桌面显示出来期间双击了桌面），图标层接着藏着
		if (_iconsHidden)
		{
			_takeover.SetLayersHidden(true);
		}
		UpdateCutState();
		ApplyMouseHookSettings();
		if (!_flyInAtStartup)
		{
			return;
		}
		// 迟迟接管不了（读不到资源管理器的桌面视图）时不再等，分区里的图标直接显示
		var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
		timeout.Tick += (_, _) =>
		{
			timeout.Stop();
			if (_flyInAtStartup)
			{
				_flyInAtStartup = false;
				Log.Warn("30 秒内没能接管桌面图标，分区里的图标不播启动动画");
				foreach (var window in _windows)
				{
					window.KeepArrivals([]);
				}
			}
		};
		timeout.Start();
	}

	/// <summary>
	/// 让用户看得到启动动画：开机自启时程序可能在登录界面还没退出时就运行了，先等登录界面（LogonUI）退出、桌面淡入，
	/// 然后才接管桌面，在那之前系统桌面上的图标照常显示。
	/// </summary>
	async Task StartTakeoverWhenReadyAsync()
	{
		var started = DateTime.UtcNow;
		if (IsLogonScreenShowing())
		{
			Log.Info("登录界面还没退出，等桌面显示出来再接管");
			while (IsLogonScreenShowing() && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
			{
				await Task.Delay(500);
			}
			// 登录界面退出后桌面还要淡入一会儿
			await Task.Delay(1500);
		}
		StartTakeover();
	}

	/// <summary>
	/// 本会话的登录界面（欢迎、锁屏画面）是否还在显示：登录完成、桌面显示出来后 LogonUI 进程就退出了。
	/// </summary>
	static bool IsLogonScreenShowing()
	{
		using var current = Process.GetCurrentProcess();
		var processes = Process.GetProcessesByName("LogonUI");
		try
		{
			return processes.Any(p => p.SessionId == current.SessionId);
		}
		finally
		{
			foreach (var process in processes)
			{
				process.Dispose();
			}
		}
	}

	/// <summary>
	/// 启动后第一次接管、藏起资源管理器的图标列表之前，等分区里的图标图像加载好（最多 3 秒）：分区读到桌面视图才有图标，
	/// 接管时才开始加载，图像还没出来的图标飞不起来、只能直接显示。
	/// </summary>
	public async Task WaitForFenceIconsAsync()
	{
		var started = DateTime.UtcNow;
		while (_flyInAtStartup && !FenceIconsLoaded() && DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
		{
			await Task.Delay(100);
		}
	}

	bool FenceIconsLoaded() => _windows.Where(w => !w.Model.IsPortal).All(w => w.Items.All(i => i.Icon != null));

	public void Shutdown()
	{
		if (_shutdown)
		{
			return;
		}
		_shutdown = true;
		_watchdogTimer.Stop();
		Updater.Stop();
		Wallpaper.Stop();
		Wallpaper.Changed -= RefreshAllAppearance;
		_zOrderTimer.Stop();
		_unliftTimer.Stop();
		_iconReloadTimer.Stop();
		_registryWatchers.ForEach(w => w.Dispose());
		foreach (var hook in new[] { _foregroundHook, _reorderHook })
		{
			if (hook != IntPtr.Zero)
			{
				UnhookWinEvent(hook);
			}
		}
		_foregroundHook = IntPtr.Zero;
		_reorderHook = IntPtr.Zero;
		// 先关设置窗口，它关闭时会写回未保存的规则修改
		_settingsWindow?.Close();
		_searchWindow?.Close();
		SaveNow();
		_mouse?.Dispose();
		_drawFrame?.Dispose();
		Organizer.Dispose();
		// 退出接管时恢复资源管理器的桌面图标
		_takeover?.Dispose();
		if (_iconsHidden && !DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}
		foreach (var window in _windows.ToList())
		{
			window.CloseForReal();
		}
		_tray?.Dispose();
	}

	#region 分区增删改

	/// <param name="bounds">指定位置与大小（屏幕物理像素）；为空时自动找空位。</param>
	/// <param name="reveal">处于隐藏状态时是否恢复显示；后台自动整理时不打扰用户的隐藏状态。</param>
	public FenceWindow CreateFence(string? title = null, string? portalFolder = null, FenceWindow? near = null, bool editTitle = false, bool reveal = true,
			RECT? bounds = null)
	{
		// 新建的分区要让用户看到，分区正被隐藏时退出隐藏状态
		if (reveal && _fencesHidden)
		{
			ToggleHidden();
		}
		title ??= UniqueTitle("新建分区");
		var model = new FenceSettings
		{
			Title = title,
			IsPortal = portalFolder != null,
			FolderPath = portalFolder ?? string.Empty,
		};
		var rect = bounds ?? FindFreeSlot(near?.GetBounds());
		// 画框或在鼠标处新建时压住了别的分区，挪到最近的空位
		if (bounds != null && FindFreeSpot(null, rect) is RECT spot)
		{
			rect = spot;
		}
		model.LayoutDpi = GetDpiForRect(rect);
		model.X = rect.Left;
		model.Y = rect.Top;
		model.Width = rect.Width;
		model.Height = rect.Height;
		Settings.Fences.Add(model);
		var window = OpenWindow(model);
		SaveSoon();
		_takeover?.RelayoutSoon();
		if (editTitle)
		{
			window.BeginTitleEdit();
		}
		return window;
	}

	public void CreatePortalFence(FenceWindow? near = null, RECT? bounds = null)
	{
		var folder = PickFolder("选择要映射到分区的文件夹", null);
		if (folder == null || !CheckPortalFolder("新建映射分区", folder))
		{
			return;
		}
		if (IsFolderUsed(folder, null))
		{
			MessageDialog.Show("新建映射分区", "这个文件夹已经有对应的分区了。", "确定");
			return;
		}
		var name = Path.GetFileName(PathUtil.Normalize(folder));
		CreateFence(UniqueTitle(string.IsNullOrEmpty(name) ? folder : name), folder, near, bounds: bounds);
	}

	public void ChangePortalFolder(FenceWindow window)
	{
		var folder = PickFolder("选择要映射的文件夹", window.Model.FolderPath);
		if (folder == null || PathUtil.AreEqual(folder, window.Model.FolderPath) || !CheckPortalFolder("更换映射文件夹", folder))
		{
			return;
		}
		if (IsFolderUsed(folder, window.Model))
		{
			MessageDialog.Show("更换映射文件夹", "这个文件夹已经有对应的分区了。", "确定");
			return;
		}
		window.Model.FolderPath = folder;
		window.OnFolderChanged();
		SaveSoon();
	}

	public void RenameFence(FenceWindow window, string title)
	{
		window.Model.Title = title;
		window.UpdateTitle();
		if (GroupOf(window.Model) is FenceGroup group)
		{
			RefreshTabs(group);
		}
		SaveSoon();
	}

	public void DeleteFence(FenceWindow window)
	{
		var model = window.Model;
		var message = model.IsPortal
				? $"确定删除映射分区「{model.Title}」吗？\n\n只删除分区本身，被映射的文件夹及其中的文件不受影响。"
				: $"确定删除分区「{model.Title}」吗？\n\n分区中的图标会回到桌面上，文件不受影响。";
		if (MessageDialog.Show("删除分区", message, "删除", "取消") != 0)
		{
			return;
		}
		SettingsBackup.Create(Settings, BackupReason.DeleteFence);
		// 桌面分区里的图标飞回桌面：起点要在关闭分区之前取
		var homing = !model.IsPortal && window.IsVisible && _takeover?.LayersVisible == true ? LiftIcons(window) : [];
		LeaveGroup(window);
		_windows.Remove(window);
		Settings.Fences.Remove(model);
		foreach (var rule in Settings.Rules.Where(r => r.FenceId == model.Id))
		{
			rule.FenceId = null;
		}
		// 直接关掉活动的分区，系统会把前台交给 Z 序里的下一个窗口，可能是别的程序不可见的窗口；
		// 它以管理员身份运行时（如 PixPin），本程序注入的输入会被系统丢弃，桌面右键重放失效。先把前台交给桌面，和点一下桌面一样
		if (window.IsActive)
		{
			SetForegroundWindow(DesktopHost.FindDesktopWindow());
		}
		window.CloseForReal();
		SaveSoon();
		if (homing.Count > 0)
		{
			_ = FlyHomeAsync(homing);
		}
		else
		{
			_takeover?.RelayoutSoon();
		}
	}

	/// <summary>
	/// 删除分区动画里飞回桌面的一个图标；From 为空表示在分区里看不到它（要滚动才看得到、分区卷起了），从分区中央淡入起飞。
	/// </summary>
	sealed record HomingIcon(string Key, FlyingIcon Icon, RECT? From, RECT Fence);

	/// <summary>
	/// 分区关闭之前，在分区里每个图标的位置放上飞行的图标接替它；这些图标回到桌面上后，落地前先不显示。
	/// </summary>
	List<HomingIcon> LiftIcons(FenceWindow window)
	{
		var fence = window.GetBounds();
		var insertAfter = FlightInsertAfter();
		var icons = new List<HomingIcon>();
		foreach (var item in window.Items)
		{
			if (item.Icon is not { } image)
			{
				continue;
			}
			var from = window.GetIconRect(item.FullPath);
			var flying = new FlyingIcon(image, insertAfter);
			// 看不到的图标先藏在分区中央，起飞时淡入
			flying.Place(from ?? FlyingIcon.CenteredIn(fence, 1, 1), from == null ? 0 : 1);
			flying.Show();
			icons.Add(new HomingIcon(item.FullPath, flying, from, fence));
		}
		_takeover?.ExpectArrivals(icons.Select(i => i.Key));
		return icons;
	}

	/// <summary>
	/// 删除分区动画：分区关掉后图标停在原处，再依次飞回桌面上的格子，落地后在桌面上显示。
	/// </summary>
	async Task FlyHomeAsync(List<HomingIcon> icons)
	{
		// 重排出错也要接着让每个图标落地（不飞），否则它们会一直藏着
		try
		{
			_takeover?.RelayoutNow();
			// 等图标层排好版，才知道每个图标落在哪一格
			await Dispatcher.Yield(DispatcherPriority.Loaded);
		}
		catch (Exception ex)
		{
			Log.Warn("删除分区后重排桌面图标出错", ex);
		}
		var flights = icons.Select((icon, i) => FlyHomeAsync(icon, FlyingIcon.Stagger * i)).ToList();
		try
		{
			await Task.WhenAll(flights);
		}
		catch (Exception ex)
		{
			// 只是动画出错，分区已经删除，每个图标结束时都已显示出来
			Log.Warn("删除分区的动画出错", ex);
		}
	}

	async Task FlyHomeAsync(HomingIcon icon, TimeSpan delay)
	{
		try
		{
			await Task.Delay(delay);
			// 落点按此刻的排布取；桌面上没有它（文件刚被删掉）或图标层没显示时不飞
			if (_takeover?.GetIconRect(icon.Key) is RECT target)
			{
				var from = icon.From ?? FlyingIcon.CenteredIn(icon.Fence, target.Width, target.Height);
				await icon.Icon.FlyAsync(from, target, fadeIn: icon.From == null, fadeOut: false);
			}
		}
		finally
		{
			icon.Icon.Close();
			_takeover?.CompleteArrival(icon.Key);
		}
	}

	/// <summary>
	/// 窗口被意外销毁（例如 Explorer 崩溃牵连）时重新创建。
	/// </summary>
	public void OnWindowLost(FenceWindow window)
	{
		if (_shutdown || !_windows.Remove(window))
		{
			return;
		}
		Log.Warn($"分区窗口意外关闭，正在重建：{window.Model.Title}");
		_dispatcher.InvokeAsync(() =>
		{
			OpenWindow(window.Model);
			RefreshAllTabs();
		}, DispatcherPriority.Background);
	}

	/// <summary>
	/// 整理规则对应的分区：优先按记录的分区 Id，其次按同名桌面分区，都没有就新建。
	/// </summary>
	public FenceWindow GetOrCreateRuleFence(OrganizeRule rule, bool reveal)
	{
		var window = (rule.FenceId is Guid id ? _windows.FirstOrDefault(w => w.Model.Id == id && !w.Model.IsPortal) : null)
				?? _windows.FirstOrDefault(w => !w.Model.IsPortal && w.Model.Title == rule.Name)
				?? CreateFence(UniqueTitle(rule.Name), reveal: reveal);
		rule.FenceId = window.Model.Id;
		SaveSoon();
		return window;
	}

	#endregion

	#region 标签页

	FenceGroup? GroupOf(FenceSettings model) => Settings.Groups.FirstOrDefault(g => g.Members.Contains(model.Id));

	FenceWindow? WindowOf(Guid id) => _windows.FirstOrDefault(w => w.Model.Id == id);

	/// <summary>
	/// 分区所在标签组的全部分区窗口，按标签顺序；不在组里时只有它自己。
	/// </summary>
	public List<FenceWindow> TabsOf(FenceWindow window)
	{
		return GroupOf(window.Model) is FenceGroup group ? group.Members.Select(WindowOf).OfType<FenceWindow>().ToList() : [window];
	}

	/// <summary>
	/// 是不是标签组里当前没显示的标签：窗口照常加载内容，只是不显示。
	/// </summary>
	public bool IsHiddenTab(FenceWindow window) => GroupOf(window.Model) is FenceGroup group && group.Active != window.Model.Id;

	void RefreshTabs(FenceGroup group)
	{
		foreach (var id in group.Members)
		{
			WindowOf(id)?.UpdateTabs();
		}
	}

	public void RefreshAllTabs()
	{
		foreach (var group in Settings.Groups)
		{
			RefreshTabs(group);
		}
	}

	/// <summary>
	/// 位置、大小、缩放和显示器布局记忆、卷起和锁定：标签组内各分区保持一致的那部分设置。
	/// </summary>
	static void CopyFrame(FenceSettings from, FenceSettings to)
	{
		(to.X, to.Y, to.Width, to.Height, to.LayoutDpi) = (from.X, from.Y, from.Width, from.Height, from.LayoutDpi);
		(to.RolledUp, to.RollDirection, to.RollEdge, to.Locked) = (from.RolledUp, from.RollDirection, from.RollEdge, from.Locked);
		to.BoundsByDpi = new Dictionary<int, FenceBounds>(from.BoundsByDpi);
		to.LayoutByDisplay = new Dictionary<string, FenceLayout>(from.LayoutByDisplay);
	}

	/// <summary>
	/// 标签组里显示着的分区移动、调整大小、卷起或锁定后，同组的其他分区跟着一致（它们不显示，切过去时就在原处）。
	/// </summary>
	public void SyncGroup(FenceWindow source)
	{
		foreach (var window in TabsOf(source).Where(w => w != source))
		{
			CopyFrame(source.Model, window.Model);
			window.ApplyBounds();
			window.ApplyLockState();
		}
	}

	/// <summary>
	/// 启动时整理标签组：去掉已不存在或重复归组的分区，不足两个的组解散，组内各分区的框架设置按当前标签统一。
	/// </summary>
	/// <returns>配置被改动时返回 true。</returns>
	bool NormalizeGroups()
	{
		bool changed = false;
		var fences = Settings.Fences.ToDictionary(f => f.Id);
		var grouped = new HashSet<Guid>();
		foreach (var group in Settings.Groups.ToList())
		{
			var members = new List<Guid>();
			foreach (var id in group.Members)
			{
				if (fences.ContainsKey(id) && grouped.Add(id))
				{
					members.Add(id);
				}
			}
			changed |= members.Count != group.Members.Count;
			group.Members = members;
			if (members.Count < 2)
			{
				Settings.Groups.Remove(group);
				changed = true;
				continue;
			}
			if (!members.Contains(group.Active))
			{
				group.Active = members[0];
				changed = true;
			}
			foreach (var id in members.Where(id => id != group.Active))
			{
				CopyFrame(fences[group.Active], fences[id]);
			}
		}
		return changed;
	}

	/// <summary>
	/// 拖动分区时鼠标所在的、可以合并进去的分区标题栏：别的、显示着的、没锁定的分区，同组的不算。
	/// </summary>
	public FenceWindow? MergeTargetAt(FenceWindow dragged, POINT cursor)
	{
		var group = GroupOf(dragged.Model);
		return _windows.FirstOrDefault(w => w != dragged && w.IsVisible && !w.Model.Locked
				&& (group == null || !group.Members.Contains(w.Model.Id))
				&& w.TitleBarRect().Contains(cursor));
	}

	/// <summary>
	/// 把拖过来的分区（连同它所在的整组标签）合并进目标分区的标签组，排在最后并切到拖过来的那个：
	/// 它们套上目标的位置、大小、卷起和锁定，被拖的窗口滑进目标的位置，目标随后隐藏。
	/// </summary>
	public async Task MergeAsync(FenceWindow dragged, FenceWindow target)
	{
		var incoming = TabsOf(dragged);
		if (GroupOf(dragged.Model) is FenceGroup old)
		{
			Settings.Groups.Remove(old);
		}
		if (GroupOf(target.Model) is not FenceGroup group)
		{
			group = new FenceGroup { Members = [target.Model.Id] };
			Settings.Groups.Add(group);
		}
		group.Members.AddRange(incoming.Select(w => w.Model.Id));
		group.Active = dragged.Model.Id;
		foreach (var window in incoming)
		{
			CopyFrame(target.Model, window.Model);
			if (window != dragged)
			{
				window.ApplyBounds();
			}
		}
		Log.Info($"分区「{dragged.Model.Title}」合并到「{target.Model.Title}」的标签组");
		SaveSoon();
		await dragged.SlideTo(target.GetBounds());
		dragged.ApplyBounds();
		dragged.ApplyLockState();
		target.ClearSelection();
		target.Hide();
		RefreshTabs(group);
		BringToFront(dragged);
		_takeover?.RelayoutSoon();
	}

	/// <summary>
	/// 切换到组里的另一个标签：新标签先藏着在当前标签的正下方显示、画好，再在同一次屏幕刷新里换手，切换时不闪。
	/// </summary>
	/// <param name="activate">让新标签成为活动窗口；原来的标签是活动窗口时总会这样，免得隐藏它后前台落到别的程序上。</param>
	public async Task ActivateTabAsync(FenceWindow target, bool activate)
	{
		if (GroupOf(target.Model) is not FenceGroup group || group.Active == target.Model.Id)
		{
			return;
		}
		var current = WindowOf(group.Active);
		group.Active = target.Model.Id;
		// 不刷新标签条：各分区的标签条都把自己那个标签标为当前，换了当前标签也不用变；
		// 重建标签条时鼠标停着的标签会丢掉悬停底色，切换前闪一下（实测）
		SaveSoon();
		// 分区整体隐藏着时只换记录，重新显示时显示新标签；正在切换时由那一次接着换到最后选的标签
		if (current == null || _fencesHidden || !_switchingGroups.Add(group))
		{
			return;
		}
		try
		{
			await SwitchTabAsync(current, target, activate);
			// 切换要等几帧，期间又选了别的标签（比如在标签上快速滚动滚轮）：接着换过去
			while (!_fencesHidden && WindowOf(group.Active) is { } latest && latest != target)
			{
				current = target;
				target = latest;
				await SwitchTabAsync(current, target, activate);
			}
		}
		finally
		{
			_switchingGroups.Remove(group);
		}
	}

	async Task SwitchTabAsync(FenceWindow current, FenceWindow target, bool activate)
	{
		CopyFrame(current.Model, target.Model);
		await target.ShowInPlaceOf(current);
		target.TakeOverFrom(current);
		if (activate || current.IsActive)
		{
			target.Activate();
		}
		// 不显示的标签不留选择，免得删除、剪切等操作带上看不见的图标
		current.ClearSelection();
		current.Hide();
		current.SetCloaked(false);
		BringToFront(target);
	}

	/// <summary>
	/// 调整标签顺序：把分区的标签移到第 index 个。
	/// </summary>
	public void MoveTab(FenceWindow window, int index)
	{
		if (GroupOf(window.Model) is not FenceGroup group)
		{
			return;
		}
		int from = group.Members.IndexOf(window.Model.Id);
		index = Math.Clamp(index, 0, group.Members.Count - 1);
		if (from == index)
		{
			return;
		}
		group.Members.RemoveAt(from);
		group.Members.Insert(index, window.Model.Id);
		RefreshTabs(group);
		SaveSoon();
	}

	/// <summary>
	/// 把一个标签拆出来成为独立的分区：拖出去时放在松手处（标题栏中间对着鼠标），用菜单拆时放在组的右边，都避开别的分区。
	/// </summary>
	public void DetachTab(FenceWindow window, POINT? cursor)
	{
		if (GroupOf(window.Model) is null)
		{
			return;
		}
		LeaveGroup(window);
		var model = window.Model;
		if (cursor is POINT point)
		{
			var bounds = window.DetachedBoundsAt(point);
			(model.X, model.Y) = (bounds.Left, bounds.Top);
		}
		else
		{
			var (work, scale) = GetMonitorWorkArea(MonitorFromPoint(new POINT(model.X + model.Width / 2, model.Y), MONITOR_DEFAULTTONEAREST));
			model.X = Math.Clamp(model.X + model.Width + (int)Math.Round(Settings.SnapGap * scale), work.Left, Math.Max(work.Left, work.Right - model.Width));
		}
		var layout = window.GetLayoutBounds();
		if (FindFreeSpot(window, layout) is RECT spot)
		{
			model.X += spot.Left - layout.Left;
			model.Y += spot.Top - layout.Top;
		}
		model.LayoutDpi = GetDpiForRect(window.GetLayoutBounds());
		window.ApplyBounds();
		window.UpdateTabs();
		if (!_fencesHidden && !window.IsVisible)
		{
			window.Show();
		}
		BringToFront(window);
		Log.Info($"分区「{model.Title}」拆出标签组");
		SaveSoon();
		_takeover?.RelayoutSoon();
	}

	/// <summary>
	/// 分区离开所在的标签组（拆出或删除）：它正显示着时先在原处显示相邻的标签；组里只剩一个分区时解散，剩下的那个照常单独显示。
	/// </summary>
	void LeaveGroup(FenceWindow window)
	{
		if (GroupOf(window.Model) is not FenceGroup group)
		{
			return;
		}
		int index = group.Members.IndexOf(window.Model.Id);
		group.Members.RemoveAt(index);
		if (group.Active == window.Model.Id)
		{
			group.Active = group.Members[Math.Min(index, group.Members.Count - 1)];
			if (WindowOf(group.Active) is FenceWindow next && !_fencesHidden)
			{
				CopyFrame(window.Model, next.Model);
				next.ApplyBounds();
				next.Show();
				BringToFront(next);
			}
		}
		if (group.Members.Count < 2)
		{
			Settings.Groups.Remove(group);
		}
		RefreshTabs(group);
		SaveSoon();
	}

	#endregion

	#region 桌面分区成员

	/// <summary>
	/// 这些桌面图标此刻在桌面上的位置（物理像素）与图像，一键整理动画的起点；没接管桌面图标或图标层没显示时为空。
	/// </summary>
	public Dictionary<string, (RECT Rect, ImageSource Icon)> CaptureDesktopIcons(IEnumerable<string> keys)
	{
		return _takeover?.CaptureIcons(keys) ?? new Dictionary<string, (RECT Rect, ImageSource Icon)>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 一键整理动画里图标起飞，桌面原处不再显示。
	/// </summary>
	public void TakeOff(string key) => _takeover?.TakeOff(key);

	/// <summary>
	/// 这些图标在资源管理器图标列表里的位置（物理像素），启动动画的起点；没接管桌面图标时为空。
	/// </summary>
	public Dictionary<string, RECT> ExplorerIconRects(IEnumerable<string> keys)
	{
		return _takeover?.ExplorerIconRects(keys) ?? new Dictionary<string, RECT>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 接管了桌面图标、正要藏起资源管理器的图标列表：启动后的第一次接管，让分区里的图标从列表里的原处飞进分区。
	/// </summary>
	public void OnDesktopTakenOver()
	{
		if (!_flyInAtStartup)
		{
			return;
		}
		_flyInAtStartup = false;
		_ = Organizer.FlyInAtStartupAsync();
	}

	/// <summary>
	/// 所有桌面分区的成员，不在其中的桌面项目由散放图标层显示。
	/// </summary>
	public HashSet<string> FencedKeys()
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var model in Settings.Fences.Where(f => !f.IsPortal))
		{
			keys.UnionWith(model.Members);
		}
		return keys;
	}

	/// <summary>
	/// 把桌面项目归入某个桌面分区（同一项目只属于一个分区）；target 为 null 时移出分区、回到桌面上。只改记录，不动文件。
	/// </summary>
	public void AssignToFence(FenceWindow? target, IEnumerable<string> keys)
	{
		var list = keys.ToList();
		if (list.Count == 0)
		{
			return;
		}
		var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var window in _windows.Where(w => !w.Model.IsPortal && w != target))
		{
			int removed = window.Model.Members.RemoveAll(set.Contains);
			window.Model.CustomOrder.RemoveAll(set.Contains);
			if (removed > 0)
			{
				window.RefreshItems();
			}
		}
		target?.AddMembers(list);
		SaveSoon();
		_takeover?.RelayoutSoon();
	}

	/// <summary>
	/// 桌面上的文件改名后，分区成员、自定义顺序和打开记录跟着换成新路径，图标留在原分区的原位置。
	/// </summary>
	public void OnItemRenamed(string oldPath, string newPath)
	{
		UsageStats.Rename(oldPath, newPath);
		foreach (var model in Settings.Fences.Where(f => !f.IsPortal))
		{
			int index = model.Members.FindIndex(k => string.Equals(k, oldPath, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				continue;
			}
			model.Members[index] = newPath;
			int order = model.CustomOrder.FindIndex(k => string.Equals(k, oldPath, StringComparison.OrdinalIgnoreCase));
			if (order >= 0)
			{
				model.CustomOrder[order] = newPath;
			}
			SaveSoon();
		}
	}

	/// <summary>
	/// 分区里的图标和桌面上的图标同属一个桌面，共用一组选择，规则与系统桌面一致：单击图标只选中它，
	/// 按住 Ctrl/Shift 单击可以跨分区和桌面追加，点空白处全部取消。这里在单选时取消其他地方的选择。
	/// </summary>
	/// <param name="fence">正在操作的分区；为 null 表示正在操作桌面上的图标。</param>
	public void OnSelectionScopeActivated(FenceWindow? fence)
	{
		foreach (var window in _windows.Where(w => w != fence))
		{
			window.ClearSelection();
		}
		if (fence != null)
		{
			_takeover?.ClearSelection();
		}
	}

	/// <summary>
	/// 整个桌面选中的图标（各分区里的和桌面上的）。打开、删除、复制、剪切都作用于它们全部。
	/// </summary>
	public List<FenceItem> AllSelectedItems()
	{
		var items = _windows.SelectMany(w => w.SelectedItems).ToList();
		items.AddRange(_takeover?.SelectedItems ?? []);
		return items;
	}

	/// <summary>
	/// 右键菜单、属性、拖动要把选中的图标合成一组 Shell 项：都是桌面上的图标（散放图标和桌面分区里的），或都在同一个映射分区里时合在一起；
	/// 混有映射分区里的文件、合不到一起时，只保留发起处自己的选择并取消别处的，所见即所得。
	/// </summary>
	/// <param name="origin">发起操作的分区；为 null 表示桌面上的图标层。</param>
	/// <returns>实际作用的图标，以及它们是不是桌面上的图标。</returns>
	public (List<FenceItem> Items, bool Desktop) SelectionForShell(FenceWindow? origin)
	{
		var portals = _windows.Where(w => w.Model.IsPortal && w.HasSelection).ToList();
		bool desktopSelected = _takeover?.SelectedItems.Count > 0 || _windows.Any(w => !w.Model.IsPortal && w.HasSelection);
		if (portals.Count == 0)
		{
			return (AllSelectedItems(), true);
		}
		if (portals.Count == 1 && !desktopSelected)
		{
			return (portals[0].SelectedItems, false);
		}
		OnSelectionScopeActivated(origin);
		return origin == null ? (_takeover?.SelectedItems ?? [], true) : (origin.SelectedItems, !origin.Model.IsPortal);
	}

	public bool IsCut(string path) => _cutPaths.Contains(path);

	void UpdateCutState()
	{
		_cutPaths = ItemOps.GetCutPaths();
		foreach (var window in _windows)
		{
			window.ApplyCutState();
		}
		_takeover?.ApplyCutState();
	}

	/// <summary>
	/// 桌面视图变化：刷新桌面分区，并清理文件已被删除的成员。
	/// </summary>
	public void OnDesktopItemsChanged()
	{
		PruneMembers();
		foreach (var window in _windows.Where(w => !w.Model.IsPortal))
		{
			window.RefreshItems();
		}
	}

	/// <summary>
	/// 文件被删除或移走的成员在宽限期后移出分区，免得以后桌面上出现同名文件时被自动拉进分区。
	/// 系统图标（此电脑等）只是可能被设置为不显示，一律保留。返回成员是否有变化。
	/// </summary>
	bool PruneMembers()
	{
		var snapshot = _takeover?.Items.Snapshot;
		if (snapshot == null)
		{
			return false;
		}
		var now = DateTime.Now;
		bool changed = RemapMovedMembers(snapshot);
		foreach (var model in Settings.Fences.Where(f => !f.IsPortal))
		{
			changed |= model.Members.RemoveAll(key =>
			{
				if (snapshot.ByKey.ContainsKey(key) || !Path.IsPathFullyQualified(key) || File.Exists(key) || Directory.Exists(key))
				{
					_missingMembers.Remove(key);
					return false;
				}
				if (!_missingMembers.TryGetValue(key, out var since))
				{
					_missingMembers[key] = now;
					return false;
				}
				return now - since > MissingMemberGrace;
			}) > 0;
		}
		if (changed)
		{
			SaveSoon();
		}
		return changed;
	}

	/// <summary>
	/// 桌面文件夹被迁移（如 OneDrive 的「备份桌面」）后，成员里指向旧位置的路径换成新位置上的同名项目。
	/// 只处理父目录已不是当前桌面文件夹的成员；仍在当前桌面里却找不到的是被删除了，交给清理。
	/// </summary>
	bool RemapMovedMembers(DesktopSnapshot snapshot)
	{
		string[] desktops = [AppPaths.Desktop, AppPaths.CommonDesktop];
		var fenced = FencedKeys();
		// 同名的只认唯一一个，有歧义时不动
		var byName = snapshot.Items.Where(e => e.IsFileSystem && !fenced.Contains(e.Key))
				.GroupBy(e => Path.GetFileName(e.Key), StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Count() == 1)
				.ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
		bool changed = false;
		foreach (var model in Settings.Fences.Where(f => !f.IsPortal))
		{
			for (int i = 0; i < model.Members.Count; i++)
			{
				var key = model.Members[i];
				if (snapshot.ByKey.ContainsKey(key)
						|| !Path.IsPathFullyQualified(key)
						|| Path.GetDirectoryName(key) is not string parent
						|| desktops.Any(d => PathUtil.AreEqual(d, parent))
						|| File.Exists(key)
						|| Directory.Exists(key)
						|| !byName.Remove(Path.GetFileName(key), out var moved))
				{
					continue;
				}
				model.Members[i] = moved;
				int order = model.CustomOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
				if (order >= 0)
				{
					model.CustomOrder[order] = moved;
				}
				changed = true;
				Log.Info($"桌面文件夹已迁移，分区成员改为新位置：{key} -> {moved}");
			}
		}
		return changed;
	}

	public bool TryBeginRenameInFence(string key)
	{
		foreach (var window in _windows)
		{
			if (window.FindItem(key) is FenceItem item && !item.IsVirtual)
			{
				_ = BeginRenameAsync(window, item);
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// 图标在标签组里没显示的标签上时，先切到那个标签再改名。
	/// </summary>
	async Task BeginRenameAsync(FenceWindow window, FenceItem item)
	{
		await ActivateTabAsync(window, true);
		window.BeginRename(item);
	}

	/// <summary>
	/// 等项目出现在桌面视图中后开始改名（刚新建的项目要等资源管理器更新视图）。
	/// </summary>
	public void RequestRename(string key) => _takeover?.RequestRename(key);

	public void RefreshDesktopSoon() => _takeover?.Items.RefreshSoon();

	/// <summary>
	/// 散放图标排布时要避开的分区范围。
	/// </summary>
	public List<RECT> FenceLayoutBounds() => _windows.Select(w => w.GetLayoutBounds()).ToList();

	/// <summary>
	/// 分区移动、缩放或卷起后，散放图标重新避让。
	/// </summary>
	public void OnFenceLayoutChanged() => _takeover?.RelayoutSoon();

	/// <summary>
	/// 旧版本的托管分区把文件移进了「桌面分区」下的文件夹。现在文件留在桌面，不再需要这些文件夹：
	/// 文件夹里还有东西的转成映射分区（文件原地不动，照常显示），空的转成桌面分区并删除空文件夹。整个过程不移动任何文件。
	/// </summary>
	void ConvertLegacyFences()
	{
		var legacy = Settings.Fences.Where(f => !f.IsPortal && !string.IsNullOrWhiteSpace(f.FolderPath)).ToList();
		foreach (var model in legacy)
		{
			var folder = model.FolderPath;
			// 读不出内容时按有内容处理，不能删
			var entries = Directory.Exists(folder) ? ListEntries(folder) : [];
			if (entries == null || entries.Count > 0)
			{
				model.IsPortal = true;
				Log.Info($"旧托管分区「{model.Title}」转为映射分区：{folder}");
				continue;
			}
			model.FolderPath = string.Empty;
			TryDeleteEmptyFolder(folder);
			// 存放这些文件夹的根目录（默认是与桌面同级的「桌面分区」）也已用不上，空了就一并删除
			if (Path.GetDirectoryName(PathUtil.Normalize(folder)) is string root)
			{
				TryDeleteEmptyFolder(root);
			}
			Log.Info($"旧托管分区「{model.Title}」为空，转为桌面分区");
		}
		if (legacy.Count > 0)
		{
			SaveSoon();
		}
	}

	#endregion

	#region 全局状态

	/// <summary>
	/// 双击桌面空白处：在隐藏与显示之间切换，隐藏的对象由「双击桌面隐藏」决定。
	/// </summary>
	public void ToggleHidden()
	{
		_hidden = !_hidden;
		ApplyHidden();
	}

	/// <summary>
	/// 更换「双击桌面隐藏」的对象；正处于隐藏状态时立即按新对象生效，不会留下无法恢复的隐藏。
	/// </summary>
	public void SetDoubleClickTarget(HideTarget target)
	{
		Settings.DoubleClickTarget = target;
		SaveSoon();
		ApplyHidden();
		_settingsWindow?.RefreshDoubleClickTarget();
	}

	void ApplyHidden()
	{
		var (icons, fences) = Settings.DoubleClickTarget.Parts();
		SetHidden(_hidden && icons, _hidden && fences);
	}

	/// <summary>
	/// 按「启动时」设置隐藏图标或分区，在打开分区窗口、接管桌面之前调用。隐藏的对象可以和双击隐藏的不同，
	/// 双击一次全部显示出来，之后照常按「双击桌面隐藏」的对象切换。
	/// </summary>
	void ApplyStartupHidden()
	{
		// 隐藏后只能双击桌面恢复
		if (!Settings.DoubleClickToHide)
		{
			return;
		}
		HideTarget? target = Settings.StartupVisibility switch
		{
			StartupVisibility.Show => null,
			StartupVisibility.Hide => Settings.StartupHideTarget,
			StartupVisibility.Restore => Settings.LastHidden,
		};
		if (target is not HideTarget hidden)
		{
			return;
		}
		var (icons, fences) = hidden.Parts();
		_hidden = true;
		SetHidden(icons, fences);
	}

	void SetHidden(bool iconsHidden, bool fencesHidden)
	{
		// 先切换桌面图标（接管时淡入淡出图标层，否则直接显示/隐藏资源管理器的图标），再让分区淡入淡出，两者同时开始
		if (_iconsHidden != iconsHidden)
		{
			_iconsHidden = iconsHidden;
			if (_takeover?.IsActive == true)
			{
				_takeover.SetLayersHidden(iconsHidden);
			}
			else if (!DesktopHost.IconsHiddenBySystem())
			{
				DesktopHost.SetIconsVisible(!iconsHidden);
			}
		}
		if (_fencesHidden != fencesHidden)
		{
			_fencesHidden = fencesHidden;
			foreach (var window in _windows)
			{
				if (fencesHidden)
				{
					window.FadeOut();
				}
				else
				{
					window.FadeIn();
				}
			}
		}
		// 记下来，下次启动「和上次退出时一样」时恢复
		HideTarget? last = (iconsHidden, fencesHidden) switch
		{
			(true, true) => HideTarget.All,
			(true, false) => HideTarget.Icons,
			(false, true) => HideTarget.Fences,
			(false, false) => null,
		};
		if (Settings.LastHidden != last)
		{
			Settings.LastHidden = last;
			SaveSoon();
		}
	}

	/// <summary>
	/// 执行来自桌面右键菜单、托盘或第二个实例的命令。
	/// </summary>
	public void ExecuteCommand(AppCommand command)
	{
		Action action = command switch
		{
			AppCommand.ShowSettings => ShowSettings,
			AppCommand.NewFence => () => CreateFence(editTitle: true, bounds: BoundsAtCursor()),
			// 先按菜单弹出处的鼠标位置定好范围，选文件夹期间鼠标会移走
			AppCommand.NewPortalFence => () => CreatePortalFence(bounds: BoundsAtCursor()),
			AppCommand.Organize => Organizer.OrganizeInteractive,
			AppCommand.ToggleHidden => ToggleHidden,
			AppCommand.DoubleClickHidesAll => () => SetDoubleClickTarget(HideTarget.All),
			AppCommand.DoubleClickHidesIcons => () => SetDoubleClickTarget(HideTarget.Icons),
			AppCommand.DoubleClickHidesFences => () => SetDoubleClickTarget(HideTarget.Fences),
			AppCommand.Exit => App.Current.ExitApp,
		};
		action();
	}

	/// <summary>
	/// 以鼠标位置为左上角的默认大小分区范围，桌面右键菜单新建分区时用。
	/// </summary>
	RECT BoundsAtCursor()
	{
		var cursor = NativeMethods.GetCursorPos();
		var (work, scale) = GetMonitorWorkArea(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
		int width = (int)(DefaultWidthDip * scale);
		int height = (int)(DefaultHeightDip * scale);
		int left = Math.Clamp(cursor.X, work.Left, Math.Max(work.Left, work.Right - width));
		int top = Math.Clamp(cursor.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
		return new RECT(left, top, left + width, top + height);
	}

	public void SetAllLocked(bool locked)
	{
		foreach (var window in _windows)
		{
			window.Model.Locked = locked;
			window.ApplyLockState();
		}
		SaveSoon();
	}

	/// <summary>
	/// 「双击桌面隐藏」「右键画框新建分区」和接管桌面图标后的框选、键盘保护共用一套钩子，都用不上时卸载。
	/// </summary>
	public void ApplyMouseHookSettings()
	{
		// 双击是唯一的显示/隐藏入口，关掉它时先恢复显示，免得图标或分区一直藏着
		if (!Settings.DoubleClickToHide && _hidden)
		{
			ToggleHidden();
		}
		bool takeover = _takeover?.IsActive == true;
		if (!Settings.DoubleClickToHide && !Settings.DrawToCreate && !takeover)
		{
			_mouse?.Stop();
			return;
		}
		if (_mouse == null)
		{
			_mouse = new DesktopMouseWatcher(_dispatcher);
			_mouse.DoubleClicked += ToggleHidden;
			_mouse.DrawUpdated += rect =>
			{
				_drawFrame ??= DrawFrameWindow.CreateFrame();
				_drawFrame.ShowAt(rect);
			};
			_mouse.DrawFinished += OnDrawFinished;
			_mouse.BlankPressed += additive =>
			{
				_takeover?.OnBlankPressed(additive);
				// 点桌面空白处取消所有选择；按着 Ctrl/Shift 时是追加框选，保留分区里的选择
				if (!additive)
				{
					OnSelectionScopeActivated(null);
				}
			};
			_mouse.BandUpdated += rect => _takeover?.OnBandUpdated(rect);
			_mouse.BandFinished += () => _takeover?.OnBandFinished();
			_mouse.BandMenuRequested += point =>
			{
				if (_takeover?.ShowMenuForBand(point) != true)
				{
					_mouse?.ReplayRightClickLater();
				}
			};
			_mouse.DesktopActivationNeeded += ActivateDesktopKeepingFences;
			_mouse.KeyIntercepted += (key, shift) => _takeover?.HandleKey(key, shift);
			_mouse.CharIntercepted += character => _takeover?.TypeAhead(character.ToString());
		}
		_mouse.DoubleClickEnabled = Settings.DoubleClickToHide;
		_mouse.DrawEnabled = Settings.DrawToCreate;
		_mouse.TakeoverEnabled = takeover;
		_mouse.Start();
	}

	/// <summary>
	/// 由本程序激活桌面：先把图标层和分区临时置顶，桌面被提到最前时它们仍在上面，稍后再放回桌面之上。
	/// 只在本程序的窗口在前台时才会走到这里，有权切换前台。
	/// </summary>
	void ActivateDesktopKeepingFences()
	{
		DesktopHost.Lift(_windows.Select(w => w.Handle));
		SetForegroundWindow(DesktopHost.FindDesktopWindow());
		_unliftTimer.Stop();
		_unliftTimer.Start();
	}

	void OnDrawFinished(RECT rect)
	{
		_drawFrame?.Hide();
		// 空矩形表示画得太小、按普通右键处理了
		if (rect.Width == 0 || rect.Height == 0)
		{
			return;
		}
		var (_, scale) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
		int minWidth = (int)(160 * scale);
		int minHeight = (int)(100 * scale);
		var bounds = new RECT(rect.Left, rect.Top, rect.Left + Math.Max(rect.Width, minWidth), rect.Top + Math.Max(rect.Height, minHeight));
		CreateFence(editTitle: true, bounds: bounds);
	}

	/// <summary>
	/// 快捷方式属性「兼容性」页的「以管理员身份运行此程序」记在这两个键里（当前用户 / 所有用户），
	/// 改动不会触碰快捷方式文件，要单独监视才能及时更新图标上的管理员盾牌。
	/// </summary>
	void WatchElevationSettings()
	{
		const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
		var keys = new[] { Registry.CurrentUser.CreateSubKey(layers, false), Registry.LocalMachine.OpenSubKey(layers) };
		foreach (var key in keys.OfType<RegistryKey>())
		{
			var watcher = new RegistryWatcher(key);
			watcher.Changed += () => _dispatcher.BeginInvoke(() =>
			{
				_iconReloadTimer.Stop();
				_iconReloadTimer.Start();
			});
			_registryWatchers.Add(watcher);
		}
	}

	/// <summary>
	/// 设置里开关快捷方式小箭头后，按新设置重新加载全部图标。
	/// </summary>
	public void ApplyShortcutArrows()
	{
		ShellIconLoader.ShowShortcutArrows = Settings.ShowShortcutArrows;
		ReloadIcons();
	}

	void ReloadIcons()
	{
		ShellIconLoader.ClearCache();
		foreach (var window in _windows)
		{
			window.ReloadIcons();
		}
		_takeover?.ReloadIcons();
	}

	public void ApplyCollapseDelay()
	{
		foreach (var window in _windows)
		{
			window.ApplyCollapseDelay();
		}
	}

	/// <summary>
	/// 设置里换了字体或字号：分区和桌面图标层一起换。
	/// </summary>
	public void ApplyFonts()
	{
		foreach (var window in _windows)
		{
			window.ApplyFonts();
		}
		_takeover?.ApplyFonts();
	}

	public void RefreshAllAppearance()
	{
		foreach (var window in _windows)
		{
			window.ApplyAppearance();
		}
	}

	/// <summary>
	/// 硬件加速开关：之后创建的窗口按进程设置，已经打开的窗口逐个改，不用重启。
	/// </summary>
	public static void ApplyRenderMode(bool hardware)
	{
		var mode = hardware ? RenderMode.Default : RenderMode.SoftwareOnly;
		RenderOptions.ProcessRenderMode = mode;
		foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
		{
			if (source.CompositionTarget is HwndTarget target)
			{
				target.RenderMode = mode;
			}
		}
	}

	public void RefreshAllViewMode()
	{
		foreach (var window in _windows)
		{
			window.ApplyViewMode();
		}
	}

	public void RefreshAllItems()
	{
		foreach (var window in _windows)
		{
			window.RefreshItems();
		}
	}

	/// <summary>
	/// 清除各分区单独设置的颜色、不透明度、毛玻璃、边框和图标大小，统一跟随默认外观。
	/// </summary>
	public void ResetAllAppearance()
	{
		foreach (var model in Settings.Fences)
		{
			model.Color = null;
			model.Opacity = null;
			model.Blur = null;
			model.Border = null;
			model.IconSize = null;
		}
		RefreshAllAppearance();
		RefreshAllViewMode();
		SaveSoon();
	}

	/// <summary>
	/// 拖入分区时的默认动作：来自桌面或映射分区、或同一磁盘时移动，跨磁盘时复制（与资源管理器一致）。
	/// </summary>
	public bool PrefersMove(IEnumerable<string> paths, string targetFolder)
	{
		foreach (var path in paths)
		{
			var directory = Path.GetDirectoryName(path);
			if (directory == null)
			{
				return false;
			}
			bool fromDesktopOrFence = PathUtil.AreEqual(directory, AppPaths.Desktop)
					|| PathUtil.AreEqual(directory, AppPaths.CommonDesktop)
					|| _windows.Any(w => w.Model.IsPortal && PathUtil.AreEqual(w.Model.FolderPath, directory));
			if (!fromDesktopOrFence && !PathUtil.SameVolume(path, targetFolder))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 用备份或导出的配置文件替换当前的全部分区和设置，程序随即重新启动；替换前先把当前配置备份一份，恢复错了还能撤回。
	/// </summary>
	public void RestoreSettings(string file)
	{
		AppSettings restored;
		try
		{
			// 先读进内存：要恢复的可能是最旧的那份备份，接下来新建备份时会被清理掉
			restored = SettingsStore.ReadFrom(file);
		}
		catch (Exception ex)
		{
			Log.Warn($"读取配置文件失败：{file}", ex);
			MessageDialog.Show("恢复配置", $"无法读取这个配置文件：{ex.Message}", "确定");
			return;
		}
		var message = "将用这份配置替换当前的全部分区和设置，程序会重新启动。\n\n当前配置会先自动备份一份，恢复错了可以再恢复回来。";
		if (MessageDialog.Show("恢复配置", message, "恢复并重启", "取消") != 0)
		{
			return;
		}
		SettingsBackup.Create(Settings, BackupReason.Restore);
		Log.Info($"恢复配置：{file}");
		App.Current.RestartWithSettings(restored);
	}

	/// <summary>
	/// 分辨率或显示器变化后，把跑到屏幕外的分区拉回可见区域。
	/// </summary>
	public void EnsureAllOnScreen()
	{
		foreach (var window in _windows)
		{
			var model = window.Model;
			// 卷起的分区看收在边上的那一条（向下卷起时在展开范围的底部）
			var visible = window.GetLayoutBounds();
			var probe = new POINT(visible.Left + visible.Width / 2, visible.Top + Math.Min(10, visible.Height / 2));
			if (MonitorFromPoint(probe, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
			{
				continue;
			}
			var (work, _) = GetMonitorWorkArea(MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST));
			model.Width = Math.Min(model.Width, work.Width);
			model.X = Math.Clamp(model.X, work.Left, work.Right - model.Width);
			bool rolledDown = model.RolledUp && model.RollEdge == RollEdge.Bottom;
			if (rolledDown)
			{
				model.Height = Math.Min(model.Height, work.Height);
			}
			model.Y = Math.Clamp(model.Y, work.Top, Math.Max(work.Top, rolledDown ? work.Bottom - model.Height : work.Bottom - 40));
			window.ApplyBounds();
			SaveSoon();
			_takeover?.RelayoutSoon();
		}
	}

	public void SaveSoon()
	{
		_saveTimer.Stop();
		_saveTimer.Start();
	}

	public void SaveNow()
	{
		_saveTimer.Stop();
		try
		{
			SettingsStore.Save(Settings);
		}
		catch (Exception ex)
		{
			Log.Error("保存配置失败", ex);
		}
	}

	#endregion

	#region 界面入口

	public void ShowSettings()
	{
		if (ComponentDispatcher.IsThreadModal)
		{
			return;
		}
		if (_settingsWindow == null)
		{
			_settingsWindow = new SettingsWindow(this);
			_settingsWindow.Closed += (_, _) => _settingsWindow = null;
			_settingsWindow.Show();
		}
		if (_settingsWindow.WindowState == WindowState.Minimized)
		{
			_settingsWindow.WindowState = WindowState.Normal;
		}
		_settingsWindow.Activate();
	}

	void ShowTrayMenu(POINT point)
	{
		if (_tray == null || ComponentDispatcher.IsThreadModal)
		{
			return;
		}
		bool autoStart = AutoStart.IsEnabled();
		bool allLocked = AllLocked;
		using var menu = new NativeMenu();
		menu.Add("新建分区", () => CreateFence(editTitle: true));
		menu.Add("新建文件夹映射分区…", () => CreatePortalFence());
		menu.Add("一键整理桌面…", Organizer.OrganizeInteractive);
		menu.AddSeparator();
		menu.AddSubMenu("双击桌面隐藏", sub =>
		{
			foreach (var target in Enum.GetValues<HideTarget>())
			{
				sub.Add(target.DisplayName(), () => SetDoubleClickTarget(target), isChecked: Settings.DoubleClickTarget == target, radio: true);
			}
		}, enabled: Settings.DoubleClickToHide);
		menu.Add("锁定所有分区", () => SetAllLocked(!allLocked), isChecked: allLocked, enabled: _windows.Count > 0);
		menu.AddSeparator();
		menu.Add("设置…", ShowSettings, isDefault: true);
		menu.Add("开机自动启动", () => SetAutoStart(!autoStart), isChecked: autoStart);
		menu.Add("检查更新…", () => _ = Updater.CheckAsync(true));
		menu.AddSeparator();
		menu.Add("退出", App.Current.ExitApp);
		menu.Show(_tray.Handle, point);
	}

	/// <summary>
	/// 开关开机自启，失败时提示（计划任务由系统服务管理，可能被组策略等限制）；返回是否成功。
	/// </summary>
	public bool SetAutoStart(bool enabled)
	{
		try
		{
			AutoStart.SetEnabled(enabled);
			return true;
		}
		catch (Exception ex)
		{
			Log.Warn("设置开机自启失败", ex);
			MessageDialog.Show("开机自动启动", $"设置失败：{ex.Message}", "确定");
			return false;
		}
	}

	/// <summary>
	/// 按设置注册搜索桌面图标的全局快捷键；返回是否成功（没设快捷键也算成功），被其他程序占用时失败。
	/// </summary>
	public bool ApplySearchHotkey()
	{
		if (_tray == null || _tray.SetHotkey(Hotkey.Parse(Settings.SearchHotkey)))
		{
			return true;
		}
		Log.Warn($"搜索快捷键 {Settings.SearchHotkey} 注册失败，可能已被其他程序占用");
		return false;
	}

	/// <summary>
	/// 在设置里录入新快捷键期间先注销原来的，免得按下它时弹出搜索框；录完由 ApplySearchHotkey 重新注册。
	/// </summary>
	public void SuspendSearchHotkey() => _tray?.SetHotkey(null);

	void ToggleSearch()
	{
		_searchWindow ??= new SearchWindow(item => ItemOps.Open(_tray?.Handle ?? IntPtr.Zero, [item], true));
		if (_searchWindow.IsVisible)
		{
			_searchWindow.Dismiss();
			return;
		}
		_searchWindow.Popup(SearchEntries());
	}

	/// <summary>
	/// 可以搜索的图标：各桌面分区里的和桌面上散放的；映射分区显示的是文件夹内容，不算桌面图标。
	/// </summary>
	List<SearchWindow.Entry> SearchEntries()
	{
		var entries = new List<SearchWindow.Entry>();
		foreach (var window in _windows.Where(w => !w.Model.IsPortal))
		{
			entries.AddRange(window.Items.Select(item => new SearchWindow.Entry(item, $"分区：{window.Model.Title}")));
		}
		if (_takeover != null)
		{
			entries.AddRange(_takeover.LooseItems.Select(item => new SearchWindow.Entry(item, "桌面")));
		}
		return entries;
	}

	void ShowWelcome()
	{
		var message = "MyDesktop 用分区把桌面上的图标分门别类地收纳起来。\n\n"
				+ "要现在按文件类型一键整理桌面吗？整理只是把图标归入各个分区，文件仍留在桌面文件夹里，不会被移动；随时可以把图标拖回桌面。\n\n"
				+ "小提示：双击桌面空白处可以隐藏/显示所有图标和分区；右键托盘图标可以新建分区或打开设置。";
		int choice = MessageDialog.Show("欢迎使用 MyDesktop", message, "一键整理桌面", "先创建一个空分区", "稍后");
		if (choice == 0)
		{
			Organizer.OrganizeInteractive();
		}
		else if (choice == 1)
		{
			CreateFence(editTitle: true);
		}
		else
		{
			_tray?.ShowBalloon("MyDesktop 已在后台运行", "右键托盘图标可以新建分区或打开设置。");
		}
	}

	/// <summary>
	/// 升级后第一次启动时（不论自动更新还是手动安装）在通知区域提示一次已更新到的版本；第一次安装、重装同一版本不提示。
	/// 旧版本没有记录上次运行的版本，有配置文件就算是从旧版本升级上来的。
	/// </summary>
	void NotifyIfUpgraded(bool firstRun)
	{
		var current = Updater.CurrentVersion;
		bool upgraded = Version.TryParse(Settings.LastRunVersion, out var last) ? last < current : !firstRun;
		if (Settings.LastRunVersion != current.ToString(3))
		{
			Settings.LastRunVersion = current.ToString(3);
			SaveSoon();
		}
		if (upgraded)
		{
			_dispatcher.InvokeAsync(() => _tray?.ShowBalloon($"MyDesktop 已更新到 {current.ToString(3)}", "新版本已安装完成，分区和设置保持不变。"), DispatcherPriority.Background);
		}
	}

	/// <summary>
	/// 在通知区弹出通知（系统显示为 Windows 通知）；clicked 是用户点击这条通知时要做的事。
	/// </summary>
	public void ShowNotification(string title, string text, Action clicked) => _tray?.ShowBalloon(title, text, clicked);

	#endregion

	#region 内部实现

	/// <summary>
	/// 缩放比例变化后让各分区按新的比例显示：先调整位置和大小，再让窗口按新的 DPI 重新摆放；
	/// 改大小也没跟上 DPI 的窗口关掉重开。
	/// </summary>
	public void RefreshWindowsDpi()
	{
		// 缩放比例的变化可能伴随着显示器增减，先换成新显示器组合下的布局
		if (SwitchDisplayLayout())
		{
			SaveSoon();
		}
		if (AdaptLayoutToDpi())
		{
			SaveSoon();
		}
		foreach (var window in _windows.ToList())
		{
			window.RefreshDpi();
			// 标签组里没显示的标签收不到缩放变化，等切过去显示时再按新比例摆放，不用重开
			if (!IsHiddenTab(window) && window.HasStaleDpi())
			{
				Log.Warn($"分区「{window.Model.Title}」没跟上缩放比例变化，重新打开窗口");
				_windows.Remove(window);
				window.CloseForReal();
				OpenWindow(window.Model);
			}
		}
		RefreshAllTabs();
		_takeover?.RelayoutSoon();
	}

	/// <summary>
	/// 显示器组合变了（接上或拔掉显示器、改分辨率）时换成新组合下的布局，并按新位置摆放分区窗口。
	/// </summary>
	void ApplyDisplayLayout()
	{
		if (!SwitchDisplayLayout())
		{
			return;
		}
		foreach (var window in _windows)
		{
			// 移到 DPI 不同的显示器上时 WPF 会按新 DPI 缩放窗口，第二次把尺寸校正回保存的物理像素
			window.ApplyBounds();
			window.ApplyBounds();
		}
		SaveSoon();
		_takeover?.RelayoutSoon();
	}

	/// <summary>
	/// 显示器组合变了：分区现在的布局记到原来的组合下，新组合以前用过的恢复当时的布局（如重新接上外接显示器时分区回到原处），
	/// 没用过的保持不动，跑到屏幕外的随后由 EnsureAllOnScreen 拉回来。
	/// </summary>
	/// <returns>显示器组合变了时返回 true。</returns>
	bool SwitchDisplayLayout()
	{
		var key = CurrentDisplayKey();
		var old = Settings.DisplayKey;
		// 显示器切换过程中可能短暂枚举不到显示器，不当作新组合
		if (key.Length == 0 || key == old)
		{
			return false;
		}
		Settings.DisplayKey = key;
		// 旧版本配置没记录显示器组合，按当前的看待
		if (old == null)
		{
			return true;
		}
		Log.Info($"显示器组合变化：{old} → {key}");
		foreach (var model in Settings.Fences)
		{
			model.LayoutByDisplay[old] = new FenceLayout(model.X, model.Y, model.Width, model.Height, model.LayoutDpi, model.RollEdge);
			if (model.LayoutByDisplay.TryGetValue(key, out var saved))
			{
				(model.X, model.Y, model.Width, model.Height) = (saved.X, saved.Y, saved.Width, saved.Height);
				(model.LayoutDpi, model.RollEdge) = (saved.Dpi, saved.RollEdge);
			}
		}
		return true;
	}

	/// <summary>
	/// 当前的显示器组合：各显示器的范围（物理像素）按位置排序后拼成的字符串，显示器增减、改分辨率或调整排列时都会变。
	/// </summary>
	static string CurrentDisplayKey()
	{
		var monitors = new List<RECT>();
		EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
		{
			monitors.Add(rect);
			return true;
		}, IntPtr.Zero);
		return string.Join(";", monitors.OrderBy(r => r.Left).ThenBy(r => r.Top).Select(r => $"{r.Left},{r.Top},{r.Right},{r.Bottom}"));
	}

	/// <summary>
	/// 分区所在显示器的缩放比例与保存位置大小时不同（包括程序没运行时改的），调整分区的位置和大小，让里面的图标排布和原来一样：
	/// 切回以前用过的缩放比例时恢复当时的布局；第一次到某个缩放比例时按新旧 DPI 之比缩放，
	/// 彼此相邻的分区作为一组整体缩放，贴着的屏幕边保持贴边，放大后屏幕放不下时缩到刚好放下。
	/// </summary>
	/// <returns>有分区的配置被改动时返回 true。</returns>
	bool AdaptLayoutToDpi()
	{
		bool changed = false;
		var toScale = new List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)>();
		foreach (var model in Settings.Fences)
		{
			var rect = new RECT(model.X, model.Y, model.X + model.Width, model.Y + model.Height);
			int dpi = GetDpiForRect(rect);
			if (model.LayoutDpi == dpi)
			{
				continue;
			}
			changed = true;
			int oldDpi = model.LayoutDpi;
			model.LayoutDpi = dpi;
			// 旧版本配置没记录缩放比例，按当前的看待
			if (oldDpi == 0)
			{
				continue;
			}
			model.BoundsByDpi[oldDpi] = new FenceBounds(model.X, model.Y, model.Width, model.Height);
			if (model.BoundsByDpi.TryGetValue(dpi, out var saved))
			{
				(model.X, model.Y, model.Width, model.Height) = (saved.X, saved.Y, saved.Width, saved.Height);
			}
			else
			{
				toScale.Add((model, rect, oldDpi, dpi));
			}
		}
		foreach (var group in GroupAdjacent(toScale))
		{
			ScaleGroup(group);
		}
		return changed;
	}

	/// <summary>
	/// 把彼此相邻（间隔不超过吸附间距）的分区分成一组，一组分区整体缩放才不会互相重叠或拉开距离。
	/// </summary>
	List<List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)>> GroupAdjacent(List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)> items)
	{
		var groups = new List<List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)>>();
		foreach (var item in items)
		{
			int near = (int)Math.Ceiling((Settings.SnapGap + 8) * item.OldDpi / 96.0);
			var touching = groups.Where(g => g.Any(other => Touches(item.Old, other.Old, near))).ToList();
			var merged = new List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)> { item };
			foreach (var group in touching)
			{
				merged.AddRange(group);
				groups.Remove(group);
			}
			groups.Add(merged);
		}
		return groups;
	}

	static bool Touches(RECT a, RECT b, int near)
	{
		return a.Left < b.Right + near && b.Left < a.Right + near && a.Top < b.Bottom + near && b.Top < a.Bottom + near;
	}

	/// <summary>
	/// 以整组所贴的屏幕边（不贴边时为中心）为基点按新旧 DPI 之比缩放，再整体挪回工作区内。
	/// </summary>
	static void ScaleGroup(List<(FenceSettings Model, RECT Old, int OldDpi, int NewDpi)> group)
	{
		var box = group[0].Old;
		foreach (var (_, old, _, _) in group)
		{
			box = new RECT(Math.Min(box.Left, old.Left), Math.Min(box.Top, old.Top), Math.Max(box.Right, old.Right), Math.Max(box.Bottom, old.Bottom));
		}
		var (work, _) = GetMonitorWorkArea(MonitorFromRect(ref box, MONITOR_DEFAULTTONEAREST));
		double factor = (double)group[0].NewDpi / group[0].OldDpi;
		// 放大后整组超出屏幕的，只放大到刚好放下
		if (factor > 1)
		{
			factor = Math.Max(1, Math.Min(factor, Math.Min((double)work.Width / box.Width, (double)work.Height / box.Height)));
		}
		int edge = (int)Math.Ceiling(24 * group[0].OldDpi / 96.0);
		double anchorX = Math.Abs(box.Left - work.Left) <= edge ? box.Left
				: Math.Abs(box.Right - work.Right) <= edge ? box.Right : (box.Left + box.Right) / 2.0;
		double anchorY = Math.Abs(box.Top - work.Top) <= edge ? box.Top
				: Math.Abs(box.Bottom - work.Bottom) <= edge ? box.Bottom : (box.Top + box.Bottom) / 2.0;
		int Scale(int value, double anchor) => (int)Math.Round(anchor + (value - anchor) * factor);
		var scaled = group.Select(item => new RECT(Scale(item.Old.Left, anchorX), Scale(item.Old.Top, anchorY), Scale(item.Old.Right, anchorX), Scale(item.Old.Bottom, anchorY))).ToList();
		int left = scaled.Min(r => r.Left), top = scaled.Min(r => r.Top), right = scaled.Max(r => r.Right), bottom = scaled.Max(r => r.Bottom);
		int dx = left < work.Left ? work.Left - left : right > work.Right ? Math.Max(work.Left - left, work.Right - right) : 0;
		int dy = top < work.Top ? work.Top - top : bottom > work.Bottom ? Math.Max(work.Top - top, work.Bottom - bottom) : 0;
		for (int i = 0; i < group.Count; i++)
		{
			var model = group[i].Model;
			var rect = scaled[i];
			(model.X, model.Y, model.Width, model.Height) = (rect.Left + dx, rect.Top + dy, rect.Width, rect.Height);
		}
	}

	FenceWindow OpenWindow(FenceSettings model)
	{
		var window = new FenceWindow(this, model);
		_windows.Add(window);
		if (_flyInAtStartup && !model.IsPortal)
		{
			window.ExpectArrivals(model.Members);
		}
		window.ShowOnDesktop();
		if (_fencesHidden)
		{
			window.Hide();
		}
		return window;
	}

	/// <summary>
	/// Win+D 会把桌面窗口提到最前，切回其他窗口时桌面又回到底层；前台每次切换后都把分区摆回桌面正上方。
	/// </summary>
	void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
	{
		// 由系统直接回调，异常不会进入 WPF 的未处理异常流程，必须就地捕获，否则进程会被终止
		try
		{
			KeepFencesAboveDesktop();
			// Explorer 调整层级可能晚于前台切换，稍后再校正一次
			_zOrderTimer.Stop();
			_zOrderTimer.Start();
		}
		catch (Exception ex)
		{
			Log.Warn("处理前台切换失败", ex);
		}
	}

	/// <summary>
	/// 顶层窗口层级变化时立即响应：「显示桌面」把桌面提到最前的那一刻就把分区放回其上，
	/// 比等前台切换事件更早，避免能看到分区"后弹出来"。事件的 hwnd 为桌面根窗口时才是顶层窗口的层级变化。
	/// </summary>
	void OnZOrderChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
	{
		if (hwnd != _rootWindow)
		{
			return;
		}
		try
		{
			KeepFencesAboveDesktop();
		}
		catch (Exception ex)
		{
			Log.Warn("处理层级变化失败", ex);
		}
	}

	void KeepFencesAboveDesktop()
	{
		// 先摆好紧贴桌面的散放图标层，分区再压在它们之上
		_takeover?.KeepLayersAboveDesktop();
		var misplaced = _windows.Where(w => w.IsVisible && DesktopHost.GetInsertAfterAboveDesktop(w.Handle) != null).ToList();
		if (misplaced.Count == 0)
		{
			return;
		}
		// 每个都插到紧贴底座的位置，按从上到下的次序插，分区之间的上下次序才不变
		var order = DesktopHost.TopToBottom(misplaced.Select(w => w.Handle));
		foreach (var window in misplaced.OrderBy(w => order.IndexOf(w.Handle)))
		{
			window.PlaceAboveDesktop();
		}
	}

	/// <summary>
	/// 把分区提到其他分区之上：点击、拖动、悬停展开或固定展开时，正在操作的分区盖住旁边的分区。
	/// </summary>
	/// <summary>
	/// 飞行的图标（整理、启动、删除分区的动画）在层级上排在哪个窗口之后：紧贴在最上面的分区之上、其他程序的窗口之下，
	/// 和桌面同一层，桌面被别的窗口挡住时动画也被挡住。
	/// </summary>
	public IntPtr FlightInsertAfter() => DesktopHost.InsertAfterAbove(_windows.Where(w => w.IsVisible).Select(w => w.Handle));

	public void BringToFront(FenceWindow window)
	{
		DesktopHost.BringAboveFences(window.Handle, _windows.Select(w => w.Handle).ToHashSet());
	}

	/// <summary>
	/// 分区之间不重叠：rect（物理像素）压住了别的分区（卷起的只算收起的那一条）时，在所在显示器的工作区里找最近的空位，
	/// 与相邻分区留出吸附间距；没压住时原样返回，放不下时返回 null。
	/// </summary>
	/// <param name="window">正在摆放的分区，不跟自己比；新建分区时为 null。</param>
	public RECT? FindFreeSpot(FenceWindow? window, RECT rect)
	{
		// 同组的标签叠在同一个位置，不算别的分区
		var group = window != null ? GroupOf(window.Model) : null;
		var others = _windows.Where(w => w != window && group?.Members.Contains(w.Model.Id) != true).Select(w => w.GetLayoutBounds()).ToList();
		if (!others.Any(o => o.IntersectsWith(rect)))
		{
			return rect;
		}
		var (work, scale) = GetMonitorWorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
		int gap = (int)Math.Round(Settings.SnapGap * scale);
		int width = rect.Width;
		int height = rect.Height;
		int maxLeft = Math.Max(work.Left, work.Right - width);
		int maxTop = Math.Max(work.Top, work.Bottom - height);
		// 候选位置：贴着各个分区的四边（留出间距）、贴着工作区边缘，以及原位置收进工作区
		var lefts = new List<int> { Math.Clamp(rect.Left, work.Left, maxLeft), work.Left, maxLeft };
		var tops = new List<int> { Math.Clamp(rect.Top, work.Top, maxTop), work.Top, maxTop };
		foreach (var o in others)
		{
			lefts.Add(o.Right + gap);
			lefts.Add(o.Left - gap - width);
			tops.Add(o.Bottom + gap);
			tops.Add(o.Top - gap - height);
		}
		RECT? best = null;
		long bestDistance = long.MaxValue;
		foreach (int left in lefts.Where(x => x >= work.Left && x <= maxLeft).Distinct())
		{
			foreach (int top in tops.Where(y => y >= work.Top && y <= maxTop).Distinct())
			{
				long dx = left - rect.Left;
				long dy = top - rect.Top;
				var candidate = new RECT(left, top, left + width, top + height);
				if (dx * dx + dy * dy < bestDistance && !others.Any(o => o.IntersectsWith(candidate)))
				{
					best = candidate;
					bestDistance = dx * dx + dy * dy;
				}
			}
		}
		return best;
	}

	void Watchdog()
	{
		if (DesktopHost.FindDesktopWindow() == IntPtr.Zero)
		{
			return;
		}
		// 兜底校正层级（Explorer 重启、桌面图标视图被挪到 WorkerW 等情况）
		KeepFencesAboveDesktop();
		_takeover?.Watchdog();
		if (_takeover?.IsActive != true && _iconsHidden && DesktopHost.AreIconsVisible())
		{
			DesktopHost.SetIconsVisible(false);
		}
		foreach (var window in _windows)
		{
			window.CheckFolder();
		}
		if (PruneMembers())
		{
			foreach (var window in _windows.Where(w => !w.Model.IsPortal))
			{
				window.RefreshItems();
			}
			_takeover?.RelayoutSoon();
		}
		// 桌面文件夹被迁移后，自动整理改为监视新位置
		Organizer.ApplyWatchSetting();
	}

	void OnExplorerRestarted()
	{
		Log.Info("检测到 Explorer 重启，稍后校正分区层级");
		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			Watchdog();
		};
		timer.Start();
	}

	bool IsFolderUsed(string folder, FenceSettings? except)
	{
		return Settings.Fences.Any(f => f != except && f.IsPortal && PathUtil.AreEqual(f.FolderPath, folder));
	}

	/// <summary>
	/// 桌面文件夹本身的图标已由桌面分区和散放图标层显示，再映射一次会重复出现。
	/// </summary>
	static bool CheckPortalFolder(string title, string folder)
	{
		if (!PathUtil.AreEqual(folder, AppPaths.Desktop) && !PathUtil.AreEqual(folder, AppPaths.CommonDesktop))
		{
			return true;
		}
		MessageDialog.Show(title, "桌面上的图标请直接拖进普通分区收纳，不需要映射桌面文件夹。", "确定");
		return false;
	}

	string UniqueTitle(string baseName)
	{
		var title = baseName;
		for (int i = 2; Settings.Fences.Any(f => f.Title == title); i++)
		{
			title = $"{baseName} {i}";
		}
		return title;
	}

	/// <summary>
	/// 按列优先在显示器工作区中找一块不与现有分区重叠的位置（物理像素）。
	/// </summary>
	RECT FindFreeSlot(RECT? near)
	{
		var monitor = near is { } n
				? MonitorFromPoint(new POINT((n.Left + n.Right) / 2, (n.Top + n.Bottom) / 2), MONITOR_DEFAULTTONEAREST)
				: MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
		var (work, scale) = GetMonitorWorkArea(monitor);
		int width = (int)(DefaultWidthDip * scale);
		int height = (int)(DefaultHeightDip * scale);
		int gap = (int)(16 * scale);
		// 左侧让出一列，避免盖住回收站等留在桌面上的系统图标
		int left = work.Left + (int)(110 * scale);
		int top = work.Top + gap;
		var occupied = _windows.Select(w => w.GetBounds().Inflate(gap - 1)).ToList();
		for (int x = left; x + width <= work.Right - gap; x += gap)
		{
			for (int y = top; y + height <= work.Bottom - gap; y += gap)
			{
				var rect = new RECT(x, y, x + width, y + height);
				if (!occupied.Any(o => o.IntersectsWith(rect)))
				{
					return rect;
				}
			}
		}
		int cx = (work.Left + work.Right - width) / 2;
		int cy = (work.Top + work.Bottom - height) / 2;
		return new RECT(cx, cy, cx + width, cy + height);
	}

	static string? PickFolder(string title, string? initialDirectory)
	{
		var dialog = new OpenFolderDialog { Title = title };
		if (initialDirectory != null && Directory.Exists(initialDirectory))
		{
			dialog.InitialDirectory = initialDirectory;
		}
		return dialog.ShowDialog() == true ? dialog.FolderName : null;
	}

	/// <summary>
	/// 列出文件夹中的项目，跳过 desktop.ini 这类「隐藏 + 系统」文件；读取失败返回 null，调用方不能把它当成空文件夹。
	/// </summary>
	static List<string>? ListEntries(string folder)
	{
		const FileAttributes systemHidden = FileAttributes.Hidden | FileAttributes.System;
		try
		{
			return new DirectoryInfo(folder).EnumerateFileSystemInfos()
					.Where(i => (i.Attributes & systemHidden) != systemHidden)
					.Select(i => i.FullName)
					.ToList();
		}
		catch (Exception ex)
		{
			Log.Warn($"读取文件夹失败：{folder}", ex);
			return null;
		}
	}

	/// <summary>
	/// 只删除真正空的旧分区文件夹：里面最多只有 desktop.ini、Thumbs.db 这类系统生成的文件，
	/// 并且非递归删除，还有任何其他内容时一律保留。
	/// </summary>
	static void TryDeleteEmptyFolder(string folder)
	{
		try
		{
			if (!Directory.Exists(folder))
			{
				return;
			}
			var entries = new DirectoryInfo(folder).GetFileSystemInfos();
			if (entries.Any(e => e is not FileInfo || !GeneratedFiles.Contains(e.Name)))
			{
				return;
			}
			foreach (var entry in entries)
			{
				entry.Attributes = FileAttributes.Normal;
				entry.Delete();
			}
			Directory.Delete(folder, false);
		}
		catch (Exception ex)
		{
			Log.Warn($"删除分区文件夹失败，已保留：{folder}", ex);
		}
	}

	#endregion
}
