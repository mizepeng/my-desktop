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
/// 管理全部分区窗口与全局功能：托盘菜单、显示/隐藏、桌面双击、布局保存、Explorer 重启恢复。
/// </summary>
internal sealed class FenceManager
{
	const double DefaultWidthDip = 340;
	const double DefaultHeightDip = 240;

	static readonly HashSet<string> GeneratedFiles = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db" };

	readonly List<FenceWindow> _windows = [];
	readonly Dispatcher _dispatcher;
	readonly DispatcherTimer _saveTimer;
	readonly DispatcherTimer _watchdogTimer;
	readonly DispatcherTimer _zOrderTimer;
	readonly WinEventProc _foregroundCallback;
	IntPtr _foregroundHook;
	TrayIcon? _tray;
	DesktopDoubleClickWatcher? _doubleClick;
	SettingsWindow? _settingsWindow;
	bool _hidden;
	bool _shutdown;

	public FenceManager(AppSettings settings)
	{
		Settings = settings;
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
		// 委托必须由字段持有，防止被 GC 回收
		_foregroundCallback = OnForegroundChanged;
		Organizer = new DesktopOrganizer(this);
	}

	public AppSettings Settings { get; }

	public DesktopOrganizer Organizer { get; }

	public IReadOnlyList<FenceWindow> Windows => _windows;

	public bool IsHidden => _hidden;

	public string StorageRoot => string.IsNullOrWhiteSpace(Settings.StorageRoot) ? AppPaths.DefaultStorageRoot : Settings.StorageRoot;

	public bool AllLocked => _windows.Count > 0 && _windows.All(w => w.Model.Locked);

	public void Start(bool firstRun)
	{
		_tray = new TrayIcon("MyDesktop 桌面分区", App.ActivateMessageName);
		_tray.LeftClick += ShowSettings;
		_tray.RightClick += ShowTrayMenu;
		_tray.ActivateRequested += ShowSettings;
		_tray.TaskbarCreated += OnExplorerRestarted;
		_tray.DisplayChanged += () => _dispatcher.InvokeAsync(EnsureAllOnScreen, DispatcherPriority.Background);
		_tray.ThemeChanged += SystemTheme.ApplyToMenus;

		// 上次异常退出时桌面图标可能停留在隐藏状态，启动时先恢复
		if (!DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}

		foreach (var model in Settings.Fences.ToList())
		{
			OpenWindow(model);
		}
		EnsureAllOnScreen();
		ApplyDoubleClickSetting();
		Organizer.ApplyWatchSetting();
		_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundCallback, 0, 0,
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
		if (_foregroundHook != IntPtr.Zero)
		{
			UnhookWinEvent(_foregroundHook);
			_foregroundHook = IntPtr.Zero;
		}
		// 先关设置窗口，它关闭时会写回未保存的规则修改
		_settingsWindow?.Close();
		SaveNow();
		_doubleClick?.Dispose();
		Organizer.Dispose();
		if (_hidden && !DesktopHost.IconsHiddenBySystem())
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

	/// <param name="reveal">处于隐藏状态时是否恢复显示；后台自动整理时不打扰用户的隐藏状态。</param>
	public FenceWindow CreateFence(string? title = null, string? portalFolder = null, FenceWindow? near = null, bool editTitle = false, bool reveal = true)
	{
		if (reveal)
		{
			SetHidden(false);
		}
		title ??= UniqueTitle("新建分区");
		var model = new FenceSettings
		{
			Title = title,
			IsPortal = portalFolder != null,
			FolderPath = portalFolder ?? CreateManagedFolder(title),
		};
		var bounds = FindFreeSlot(near?.GetBounds());
		model.X = bounds.Left;
		model.Y = bounds.Top;
		model.Width = bounds.Width;
		model.Height = bounds.Height;
		Settings.Fences.Add(model);
		var window = OpenWindow(model);
		SaveSoon();
		if (editTitle)
		{
			window.BeginTitleEdit();
		}
		return window;
	}

	public void CreatePortalFence(FenceWindow? near = null)
	{
		var folder = PickFolder("选择要映射到分区的文件夹", null);
		if (folder == null)
		{
			return;
		}
		if (IsFolderUsed(folder, null))
		{
			MessageDialog.Show("新建映射分区", "这个文件夹已经有对应的分区了。", "确定");
			return;
		}
		var name = Path.GetFileName(PathUtil.Normalize(folder));
		CreateFence(UniqueTitle(string.IsNullOrEmpty(name) ? folder : name), folder, near);
	}

	public void ChangePortalFolder(FenceWindow window)
	{
		var folder = PickFolder("选择要映射的文件夹", window.Model.FolderPath);
		if (folder == null || PathUtil.AreEqual(folder, window.Model.FolderPath))
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

	/// <summary>
	/// 重命名分区；托管分区同时尝试重命名其文件夹，方便在资源管理器中对应查找。
	/// </summary>
	public void RenameFence(FenceWindow window, string title)
	{
		var model = window.Model;
		model.Title = title;
		if (!model.IsPortal && Directory.Exists(model.FolderPath))
		{
			var parent = Path.GetDirectoryName(PathUtil.Normalize(model.FolderPath));
			var target = parent == null ? null : Path.Combine(parent, PathUtil.SanitizeFileName(title));
			if (target != null && !Directory.Exists(target) && !File.Exists(target) && !IsFolderUsed(target, model))
			{
				window.StopWatcher();
				try
				{
					Directory.Move(model.FolderPath, target);
					model.FolderPath = target;
				}
				catch (Exception ex)
				{
					// 文件夹内有文件被占用时会失败，保留原文件夹名即可
					Log.Warn($"重命名分区文件夹失败：{model.FolderPath}", ex);
				}
			}
		}
		window.UpdateTitle();
		window.OnFolderChanged();
		SaveSoon();
	}

	public void DeleteFence(FenceWindow window)
	{
		var model = window.Model;
		if (model.IsPortal)
		{
			var message = $"确定删除映射分区「{model.Title}」吗？\n\n只删除分区本身，被映射的文件夹及其中的文件不受影响。";
			if (MessageDialog.Show("删除分区", message, "删除", "取消") != 0)
			{
				return;
			}
		}
		else
		{
			List<string> entries = [];
			if (Directory.Exists(model.FolderPath))
			{
				// 读不出内容时不能当作空文件夹处理，否则可能误删里面的文件
				if (ListEntries(model.FolderPath) is not { } listed)
				{
					MessageDialog.Show("删除分区", $"无法读取分区文件夹的内容，为避免误删文件，已取消删除。\n\n{model.FolderPath}", "确定");
					return;
				}
				entries = listed;
			}
			if (entries.Count > 0)
			{
				var message = $"分区「{model.Title}」中还有 {entries.Count} 个项目，要如何处理？\n\n选择保留时，文件会留在：\n{model.FolderPath}";
				int choice = MessageDialog.Show("删除分区", message, "移回桌面并删除", "保留文件，仅删除分区", "取消");
				if (choice == 0)
				{
					if (!ShellFileOps.Move(window.Handle, entries, AppPaths.Desktop, renameOnCollision: true))
					{
						MessageDialog.Show("删除分区", "部分文件没有移回桌面，分区已保留。", "确定");
						return;
					}
					TryDeleteEmptyFolder(model.FolderPath);
				}
				else if (choice != 1)
				{
					return;
				}
			}
			else
			{
				if (MessageDialog.Show("删除分区", $"确定删除分区「{model.Title}」吗？", "删除", "取消") != 0)
				{
					return;
				}
				TryDeleteEmptyFolder(model.FolderPath);
			}
		}
		_windows.Remove(window);
		Settings.Fences.Remove(model);
		foreach (var rule in Settings.Rules.Where(r => r.FenceId == model.Id))
		{
			rule.FenceId = null;
		}
		window.CloseForReal();
		SaveSoon();
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

	public void RestoreAllFilesToDesktop()
	{
		var managed = _windows.Where(w => !w.Model.IsPortal).ToList();
		var entries = managed.SelectMany(w => ListEntries(w.Model.FolderPath) ?? []).ToList();
		if (entries.Count == 0)
		{
			MessageDialog.Show("移回桌面", "分区中没有需要移回桌面的文件。", "确定");
			return;
		}
		var message = $"将把 {managed.Count} 个分区中的 {entries.Count} 个项目全部移回桌面（重名时自动改名），分区本身保留。\n\n文件夹映射分区不受影响。";
		if (MessageDialog.Show("移回桌面", message, "全部移回桌面", "取消") != 0)
		{
			return;
		}
		ShellFileOps.Move(OwnerHandle(), entries, AppPaths.Desktop, renameOnCollision: true);
	}

	/// <summary>
	/// 整理规则对应的分区：优先按记录的分区 Id，其次按同名托管分区，都没有就新建。
	/// </summary>
	public FenceWindow GetOrCreateRuleFence(OrganizeRule rule, bool reveal)
	{
		var window = (rule.FenceId is Guid id ? _windows.FirstOrDefault(w => w.Model.Id == id) : null)
				?? _windows.FirstOrDefault(w => !w.Model.IsPortal && w.Model.Title == rule.Name)
				?? CreateFence(UniqueTitle(rule.Name), reveal: reveal);
		rule.FenceId = window.Model.Id;
		SaveSoon();
		return window;
	}

	#endregion

	#region 全局状态

	public void SetHidden(bool hidden)
	{
		if (_hidden == hidden)
		{
			return;
		}
		_hidden = hidden;
		foreach (var window in _windows)
		{
			if (hidden)
			{
				window.Hide();
			}
			else
			{
				window.Show();
				window.PlaceAboveDesktop();
			}
		}
		if (!DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(!hidden);
		}
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

	public void ApplyDoubleClickSetting()
	{
		if (!Settings.DoubleClickToHide)
		{
			_doubleClick?.Stop();
			return;
		}
		if (_doubleClick == null)
		{
			_doubleClick = new DesktopDoubleClickWatcher(_dispatcher);
			_doubleClick.DoubleClicked += () => SetHidden(!_hidden);
		}
		_doubleClick.Start();
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
	/// 拖入分区时的默认动作：来自桌面或其他分区、或同一磁盘时移动，跨磁盘时复制（与资源管理器一致）。
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
					|| _windows.Any(w => PathUtil.AreEqual(w.Model.FolderPath, directory));
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
			var probe = new POINT(model.X + model.Width / 2, model.Y + 10);
			if (MonitorFromPoint(probe, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
			{
				continue;
			}
			var (work, _) = GetMonitorWorkArea(MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST));
			model.Width = Math.Min(model.Width, work.Width);
			model.X = Math.Clamp(model.X, work.Left, work.Right - model.Width);
			model.Y = Math.Clamp(model.Y, work.Top, Math.Max(work.Top, work.Bottom - 40));
			window.ApplyBounds();
			SaveSoon();
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
		menu.Add(_hidden ? "显示桌面图标和分区" : "隐藏桌面图标和分区", () => SetHidden(!_hidden));
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
				+ $"要现在按文件类型一键整理桌面吗？整理会把桌面上的文件移动到各分区对应的文件夹（位于「{StorageRoot}」），随时可以拖回桌面。\n\n"
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

	FenceWindow OpenWindow(FenceSettings model)
	{
		if (!model.IsPortal)
		{
			if (string.IsNullOrWhiteSpace(model.FolderPath))
			{
				model.FolderPath = CreateManagedFolder(model.Title);
			}
			else
			{
				TryCreateDirectory(model.FolderPath);
			}
		}
		var window = new FenceWindow(this, model);
		_windows.Add(window);
		window.ShowOnDesktop();
		if (_hidden)
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

	void KeepFencesAboveDesktop()
	{
		foreach (var window in _windows.Where(w => w.IsVisible))
		{
			window.PlaceAboveDesktop();
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
		if (_hidden && DesktopHost.AreIconsVisible())
		{
			DesktopHost.SetIconsVisible(false);
		}
		foreach (var window in _windows)
		{
			window.CheckFolder();
		}
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

	string CreateManagedFolder(string title)
	{
		var root = StorageRoot;
		var name = PathUtil.SanitizeFileName(title);
		var path = Path.Combine(root, name);
		// 磁盘上已存在的同名文件夹不是本程序为该分区创建的，不能接管（删除分区时会被搬空并删除）
		for (int i = 2; IsFolderUsed(path, null) || Directory.Exists(path) || File.Exists(path); i++)
		{
			path = Path.Combine(root, $"{name} ({i})");
		}
		TryCreateDirectory(path);
		return path;
	}

	bool IsFolderUsed(string folder, FenceSettings? except)
	{
		return Settings.Fences.Any(f => f != except && PathUtil.AreEqual(f.FolderPath, folder));
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

	IntPtr OwnerHandle()
	{
		return _settingsWindow != null ? new WindowInteropHelper(_settingsWindow).Handle : IntPtr.Zero;
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
	/// 只删除真正空的分区文件夹：里面最多只有 desktop.ini、Thumbs.db 这类系统生成的文件，
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

	static void TryCreateDirectory(string path)
	{
		try
		{
			Directory.CreateDirectory(path);
		}
		catch (Exception ex)
		{
			Log.Warn($"创建文件夹失败：{path}", ex);
		}
	}

	#endregion
}
