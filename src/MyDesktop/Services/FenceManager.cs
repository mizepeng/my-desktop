using System.Windows;
using System.Windows.Interop;
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
	DesktopTakeover? _takeover;
	readonly Dictionary<string, DateTime> _missingMembers = new(StringComparer.OrdinalIgnoreCase);
	// 剪贴板里被剪切的文件，显示成半透明
	HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);
	// 是否处于隐藏状态（双击切换）；下面两个是按「双击桌面隐藏」的对象实际生效的结果
	bool _hidden;
	bool _iconsHidden;
	bool _fencesHidden;
	bool _shutdown;

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
	}

	public AppSettings Settings { get; }

	public DesktopOrganizer Organizer { get; }

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
			StateProvider = () => AppCommands.EncodeState(Settings.DoubleClickTarget, Settings.DoubleClickToHide),
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
			EnsureAllOnScreen();
			_takeover?.OnDisplayChanged();
		}, DispatcherPriority.Background);
		_tray.ThemeChanged += SystemTheme.ApplyToMenus;
		_tray.ClipboardChanged += () => _dispatcher.InvokeAsync(UpdateCutState, DispatcherPriority.Background);

		// 上次异常退出时桌面图标可能停留在隐藏状态，启动时先恢复
		if (!DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}

		ConvertLegacyFences();
		// 程序没运行时改过缩放比例的，先按新比例调整分区再打开
		if (AdaptLayoutToDpi())
		{
			SaveSoon();
		}
		foreach (var model in Settings.Fences.ToList())
		{
			OpenWindow(model);
		}
		EnsureAllOnScreen();
		// 读到资源管理器的桌面视图后即接管桌面图标（隐藏系统图标、由图标层画出散放图标）
		_takeover = new DesktopTakeover(this);
		UpdateCutState();
		ApplyMouseHookSettings();
		Organizer.ApplyWatchSetting();
		WatchElevationSettings();
		DesktopMenu.Apply(Settings.DesktopContextMenu);
		_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundCallback, 0, 0,
				WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		_reorderHook = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER, IntPtr.Zero, _reorderCallback, 0, 0,
				WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		_watchdogTimer.Start();
		Log.Info($"启动完成，共 {_windows.Count} 个分区");

		if (firstRun)
		{
			_dispatcher.InvokeAsync(ShowWelcome, DispatcherPriority.Background);
		}
	}

	public void Shutdown()
	{
		if (_shutdown)
		{
			return;
		}
		_shutdown = true;
		_watchdogTimer.Stop();
		_zOrderTimer.Stop();
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
		SaveNow();
		_mouse?.Dispose();
		_drawFrame?.Close();
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
		_windows.Remove(window);
		Settings.Fences.Remove(model);
		foreach (var rule in Settings.Rules.Where(r => r.FenceId == model.Id))
		{
			rule.FenceId = null;
		}
		window.CloseForReal();
		SaveSoon();
		_takeover?.RelayoutSoon();
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
		_dispatcher.InvokeAsync(() => OpenWindow(window.Model), DispatcherPriority.Background);
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

	#region 桌面分区成员

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
	/// 桌面上的文件改名后，分区成员与自定义顺序跟着换成新路径，图标留在原分区的原位置。
	/// </summary>
	public void OnItemRenamed(string oldPath, string newPath)
	{
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
				window.BeginRename(item);
				return true;
			}
		}
		return false;
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
		var (icons, fences) = Settings.DoubleClickTarget switch
		{
			HideTarget.All => (true, true),
			HideTarget.Icons => (true, false),
			HideTarget.Fences => (false, true),
		};
		SetHidden(_hidden && icons, _hidden && fences);
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
				_drawFrame ??= new DrawFrameWindow();
				_drawFrame.ShowAt(rect);
			};
			_mouse.DrawFinished += OnDrawFinished;
			_mouse.BlankPressed += additive =>
			{
				_takeover?.OnBlankPressed(additive);
				// 与资源管理器一致，单击桌面空白处取消选择，分区里选中的图标也一并取消
				if (!additive)
				{
					foreach (var window in _windows)
					{
						window.ClearSelection();
					}
				}
			};
			_mouse.BandUpdated += rect => _takeover?.OnBandUpdated(rect);
			_mouse.BandFinished += () => _takeover?.OnBandFinished();
			_mouse.KeyIntercepted += (key, shift) => _takeover?.HandleKey(key, shift);
			_mouse.CharIntercepted += character => _takeover?.TypeAhead(character.ToString());
		}
		_mouse.DoubleClickEnabled = Settings.DoubleClickToHide;
		_mouse.DrawEnabled = Settings.DrawToCreate;
		_mouse.TakeoverEnabled = takeover;
		_mouse.Start();
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

	public void RefreshAllAppearance()
	{
		foreach (var window in _windows)
		{
			window.ApplyAppearance();
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
	/// 清除各分区单独设置的颜色、不透明度和图标大小，统一跟随默认外观。
	/// </summary>
	public void ResetAllAppearance()
	{
		foreach (var model in Settings.Fences)
		{
			model.Color = null;
			model.Opacity = null;
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
		menu.Add("开机自动启动", () => AutoStart.SetEnabled(!autoStart), isChecked: autoStart);
		menu.AddSeparator();
		menu.Add("退出", App.Current.ExitApp);
		menu.Show(_tray.Handle, point);
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

	#endregion

	#region 内部实现

	/// <summary>
	/// 缩放比例变化后让各分区按新的比例显示：先调整位置和大小，再让窗口按新的 DPI 重新摆放；
	/// 改大小也没跟上 DPI 的窗口关掉重开。
	/// </summary>
	public void RefreshWindowsDpi()
	{
		if (AdaptLayoutToDpi())
		{
			SaveSoon();
		}
		foreach (var window in _windows.ToList())
		{
			window.RefreshDpi();
			if (window.HasStaleDpi())
			{
				Log.Warn($"分区「{window.Model.Title}」没跟上缩放比例变化，重新打开窗口");
				_windows.Remove(window);
				window.CloseForReal();
				OpenWindow(window.Model);
			}
		}
		_takeover?.RelayoutSoon();
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
		foreach (var window in _windows)
		{
			if (window.IsVisible && DesktopHost.GetInsertAfterAboveDesktop(window.Handle) != null)
			{
				window.PlaceAboveDesktop();
			}
		}
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
