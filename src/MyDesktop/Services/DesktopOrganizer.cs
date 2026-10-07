using System.Windows.Media;
using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Views;

namespace MyDesktop.Services;

/// <summary>
/// 桌面整理：一键按规则把桌面图标归入分区；可选监视桌面，把新出现的文件自动归类。
/// 只记录归属，文件始终留在桌面文件夹里，整理瞬间完成，也不受跨盘、大文件影响。
/// </summary>
internal sealed class DesktopOrganizer : IDisposable
{
	static readonly HashSet<string> DownloadingExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload",
	};

	/// <summary>
	/// 只自动整理这段时间内新建的项目：从别处移到桌面的旧文件（同盘移动会保留创建时间）不会被归类。
	/// </summary>
	static readonly TimeSpan NewItemWindow = TimeSpan.FromMinutes(10);

	/// <summary>
	/// 每 4 秒检查一次，最多等约 2 分钟内容稳定，仍在变化的（如大文件解压）放弃自动整理。
	/// </summary>
	const int MaxChecks = 30;

	/// <summary>
	/// 一键整理动画里各分区依次开始的间隔，同一分区的图标再按 FlyingIcon.Stagger 错开起飞。
	/// </summary>
	static readonly TimeSpan GroupStagger = TimeSpan.FromMilliseconds(260);

	sealed class PendingItem
	{
		public int Checks;
		public string? Signature;
	}

	readonly FenceManager _manager;
	readonly Dispatcher _dispatcher;
	readonly DispatcherTimer _timer;
	readonly Dictionary<string, PendingItem> _pending = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> _downloaded = new(StringComparer.OrdinalIgnoreCase);
	FileSystemWatcher? _watcher;

	public DesktopOrganizer(FenceManager manager)
	{
		_manager = manager;
		_dispatcher = Dispatcher.CurrentDispatcher;
		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
		_timer.Tick += (_, _) => ProcessPending();
	}

	public void OrganizeInteractive()
	{
		var plan = BuildPlan(EnumerateDesktop());
		if (plan.Count == 0)
		{
			MessageDialog.Show("一键整理桌面", "桌面上没有需要整理的图标。", "确定");
			return;
		}
		int total = plan.Sum(g => g.Paths.Count);
		var summary = string.Join("\n", plan.Select(g => $"    · {g.Rule.Name}：{g.Paths.Count} 个"));
		var message = $"将把桌面上的 {total} 个图标归入 {plan.Count} 个分区：\n\n{summary}\n\n"
				+ "只是归类显示，文件仍留在桌面文件夹里，不会被移动；随时可以把图标拖回桌面。";
		if (MessageDialog.Show("一键整理桌面", message, "开始整理", "取消") != 0)
		{
			return;
		}
		SettingsBackup.Create(_manager.Settings, BackupReason.Organize);
		_ = ExecuteAnimatedAsync(plan, true);
	}

	/// <summary>
	/// 整理（一键整理和自动整理都是）：按规则顺序逐个分区归入（分区依次出现），图标从桌面上的原处沿弧线飞进分区里的格子，落定后才显示。
	/// 每个分区轮到时才归入，在那之前它的图标一直留在桌面上原处。
	/// </summary>
	/// <param name="interactive">一键整理：分区隐藏着时恢复显示，出错时提示；自动整理只记日志，不打扰用户的隐藏状态。</param>
	async Task ExecuteAnimatedAsync(List<(OrganizeRule Rule, List<string> Paths)> plan, bool interactive)
	{
		var flights = new List<(FenceWindow Window, Task Flight)>();
		try
		{
			foreach (var (rule, paths) in plan)
			{
				// 起点要在归入分区之前取：归入后图标就从桌面上消失了
				var sources = _manager.CaptureDesktopIcons(paths);
				var window = _manager.GetOrCreateRuleFence(rule, reveal: interactive);
				// 归入的是标签组里没显示的标签：等同组别的标签的图标都落定了再切过去，让用户看到图标飞进这个标签，
				// 免得飞向上一个标签的图标飞到一半被切走
				if (interactive && _manager.IsHiddenTab(window))
				{
					var tabs = _manager.TabsOf(window);
					await WhenLanded(flights.Where(f => tabs.Contains(f.Window)).Select(f => f.Flight));
					await _manager.ActivateTabAsync(window, false);
				}
				window.ExpectArrivals(sources.Keys);
				// 先排好飞行再归入：即使归入出错，飞行结束时也会把藏起来的图标显示出来
				var delay = TimeSpan.Zero;
				foreach (var (key, source) in sources)
				{
					flights.Add((window, FlyAsync(window, key, source, delay)));
					delay += FlyingIcon.Stagger;
				}
				_manager.AssignToFence(window, paths);
				await Task.Delay(GroupStagger);
			}
		}
		catch (Exception ex)
		{
			Log.Error(interactive ? "一键整理出错" : "自动整理出错", ex);
			if (interactive)
			{
				MessageDialog.Show("一键整理桌面", $"整理时出错：{ex.Message}", "确定");
			}
		}
		await WhenLanded(flights.Select(f => f.Flight));
	}

	/// <summary>
	/// 等这些飞行都结束。只是动画出错时不影响整理，飞行结束时图标都已显示出来，记日志即可。
	/// </summary>
	static async Task WhenLanded(IEnumerable<Task> flights)
	{
		try
		{
			await Task.WhenAll(flights);
		}
		catch (Exception ex)
		{
			Log.Warn("整理的动画出错", ex);
		}
	}

	/// <summary>
	/// 启动（包括恢复配置后重启）时接管桌面的那一刻，分区里的图标从资源管理器桌面上的原处飞进分区，节奏与整理相同。
	/// 资源管理器的图标列表马上要藏起来，所以先在每个图标的原处放上飞行的图标接替它，再按分区依次起飞，落定后在分区里显示；
	/// 列表里没有它（拿不到起点）或图像还没加载出来的图标不飞，直接显示。
	/// </summary>
	public async Task FlyInAtStartupAsync()
	{
		var flights = new List<Task>();
		var windows = _manager.Windows.Where(w => !w.Model.IsPortal).ToList();
		var insertAfter = _manager.FlightInsertAfter();
		try
		{
			var groupDelay = TimeSpan.Zero;
			foreach (var window in windows)
			{
				var sources = _manager.ExplorerIconRects(window.Model.Members);
				var keys = new List<string>();
				var delay = groupDelay;
				foreach (var item in window.Items.ToList())
				{
					if (item.Icon is not ImageSource icon || !sources.TryGetValue(item.FullPath, out var source))
					{
						continue;
					}
					var flying = new FlyingIcon(icon, insertAfter);
					flying.Place(source, 1);
					flying.Show();
					flights.Add(FlyInAsync(window, item.FullPath, flying, source, delay));
					keys.Add(item.FullPath);
					delay += FlyingIcon.Stagger;
				}
				window.KeepArrivals(keys);
				if (keys.Count > 0)
				{
					groupDelay += GroupStagger;
				}
			}
			Log.Info($"启动动画：{flights.Count} 个图标飞进分区");
		}
		catch (Exception ex)
		{
			// 准备动画出错时不能让分区里的图标一直藏着，全部直接显示
			Log.Error("准备启动动画出错", ex);
			foreach (var window in windows)
			{
				window.KeepArrivals([]);
			}
		}
		try
		{
			await Task.WhenAll(flights);
		}
		catch (Exception ex)
		{
			// 只是动画出错，飞行结束时图标都已显示出来
			Log.Warn("启动动画出错", ex);
		}
	}

	/// <summary>
	/// 启动动画里一个图标从资源管理器桌面上的原处飞进分区；分区里看不到它的格子时飞向分区中央并淡出。
	/// </summary>
	static async Task FlyInAsync(FenceWindow window, string key, FlyingIcon flying, RECT source, TimeSpan delay)
	{
		try
		{
			await Task.Delay(delay);
			var target = window.GetIconRect(key);
			var to = target ?? FlyingIcon.CenteredIn(window.GetBounds(), source.Width / 2, source.Height / 2);
			await flying.FlyAsync(source, to, fadeIn: false, fadeOut: target == null);
		}
		finally
		{
			flying.Close();
			window.CompleteArrival(key);
		}
	}

	/// <summary>
	/// 一个图标从桌面飞进分区；分区里看不到它的格子时（分区小了要滚动、卷起了）飞向分区中央并淡出。
	/// </summary>
	async Task FlyAsync(FenceWindow window, string key, (RECT Rect, ImageSource Icon) source, TimeSpan delay)
	{
		FlyingIcon? flying = null;
		try
		{
			await Task.Delay(delay);
			// 等分区排好版，才知道图标落在哪一格
			await Dispatcher.Yield(DispatcherPriority.Loaded);
			var target = window.GetIconRect(key);
			var to = target ?? FlyingIcon.CenteredIn(window.GetBounds(), source.Rect.Width / 2, source.Rect.Height / 2);
			flying = new FlyingIcon(source.Icon, _manager.FlightInsertAfter());
			flying.Place(source.Rect, 1);
			// 桌面原处的图标与飞行的图标同时换手
			_manager.TakeOff(key);
			flying.Show();
			await flying.FlyAsync(source.Rect, to, fadeIn: false, fadeOut: target == null);
		}
		finally
		{
			flying?.Close();
			// 中途出错时也要把图标从桌面上拿走、在分区里显示出来
			_manager.TakeOff(key);
			window.CompleteArrival(key);
		}
	}

	public void ApplyWatchSetting()
	{
		// 桌面文件夹被迁移（如 OneDrive 备份桌面）后改为监视新位置
		if (_watcher != null && !PathUtil.AreEqual(_watcher.Path, AppPaths.Desktop))
		{
			_watcher.Dispose();
			_watcher = null;
		}
		bool enabled = _manager.Settings.AutoOrganize;
		if (enabled && _watcher == null)
		{
			try
			{
				_watcher = new FileSystemWatcher(AppPaths.Desktop)
				{
					IncludeSubdirectories = false,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
				};
				_watcher.Created += (_, e) => Enqueue(e.FullPath, false);
				_watcher.Renamed += (_, e) => Enqueue(e.FullPath, DownloadingExtensions.Contains(Path.GetExtension(e.OldFullPath)));
				_watcher.EnableRaisingEvents = true;
			}
			catch (Exception ex)
			{
				Log.Warn("监视桌面文件夹失败", ex);
				_watcher?.Dispose();
				_watcher = null;
			}
		}
		else if (!enabled && _watcher != null)
		{
			_watcher.Dispose();
			_watcher = null;
			_pending.Clear();
			_downloaded.Clear();
			_timer.Stop();
		}
	}

	public void Dispose()
	{
		_watcher?.Dispose();
		_watcher = null;
		_timer.Stop();
	}

	void Enqueue(string path, bool justDownloaded)
	{
		_dispatcher.BeginInvoke(() =>
		{
			_pending[path] = new PendingItem();
			if (justDownloaded)
			{
				_downloaded.Add(path);
			}
			_timer.Stop();
			_timer.Start();
		});
	}

	void ProcessPending()
	{
		_timer.Stop();
		if (!_manager.Settings.AutoOrganize)
		{
			_pending.Clear();
			return;
		}
		// 用户正在重命名（比如刚新建的文件夹），等改完名再处理
		if (_manager.IsRenaming)
		{
			_timer.Start();
			return;
		}
		// 已有归属的（比如直接拖进了某个分区）不再自动归类
		var fenced = _manager.FencedKeys();
		var ready = new List<string>();
		foreach (var (path, item) in _pending.ToList())
		{
			var info = GetInfo(path);
			if (info == null || fenced.Contains(path) || !IsCandidate(info) || !IsNewItem(info))
			{
				_pending.Remove(path);
				_downloaded.Remove(path);
				continue;
			}
			// 下载、解压仍在进行时内容会继续变化：连续两次检查内容一致且文件未被占用才归类
			var signature = Signature(info);
			bool stable = signature != null && signature == item.Signature && !(info is FileInfo file && IsLocked(file));
			item.Signature = signature;
			if (!stable)
			{
				if (++item.Checks >= MaxChecks)
				{
					_pending.Remove(path);
					_downloaded.Remove(path);
				}
				continue;
			}
			_pending.Remove(path);
			_downloaded.Remove(path);
			ready.Add(path);
		}
		if (_pending.Count > 0)
		{
			_timer.Start();
		}
		if (ready.Count > 0)
		{
			Log.Info($"自动整理 {ready.Count} 个新项目");
			_ = ExecuteAnimatedAsync(BuildPlan(ready), false);
		}
	}

	bool IsNewItem(FileSystemInfo info)
	{
		return _downloaded.Contains(info.FullName) || DateTime.Now - info.CreationTime < NewItemWindow;
	}

	/// <summary>
	/// 内容指纹：文件取大小与修改时间；文件夹取其下所有条目的数量、最新修改时间与总大小。
	/// </summary>
	static string? Signature(FileSystemInfo info)
	{
		try
		{
			if (info is FileInfo file)
			{
				return $"{file.Length}|{file.LastWriteTimeUtc.Ticks}";
			}
			long count = 0;
			long latest = info.LastWriteTimeUtc.Ticks;
			long size = 0;
			foreach (var entry in new DirectoryInfo(info.FullName).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
			{
				count++;
				latest = Math.Max(latest, entry.LastWriteTimeUtc.Ticks);
				if (entry is FileInfo child)
				{
					size += child.Length;
				}
			}
			return $"{count}|{latest}|{size}";
		}
		catch (Exception)
		{
			return null;
		}
	}

	List<(OrganizeRule Rule, List<string> Paths)> BuildPlan(IEnumerable<string> candidates)
	{
		var rules = _manager.Settings.Rules;
		var groups = new Dictionary<OrganizeRule, List<string>>();
		foreach (var path in candidates)
		{
			var rule = OrganizeRule.Match(rules, path, Directory.Exists(path));
			if (rule == null)
			{
				continue;
			}
			if (!groups.TryGetValue(rule, out var list))
			{
				groups[rule] = list = [];
			}
			list.Add(path);
		}
		// 按规则列表的顺序输出，新建分区的排列顺序与规则一致
		return rules.Where(groups.ContainsKey).Select(r => (r, groups[r])).ToList();
	}

	/// <summary>
	/// 桌面上还没进分区的文件和文件夹（含公共桌面上的），取自资源管理器的桌面视图；此电脑等系统图标不参与整理。
	/// </summary>
	List<string> EnumerateDesktop()
	{
		var snapshot = _manager.DesktopItems?.Snapshot;
		if (snapshot == null)
		{
			return [];
		}
		var fenced = _manager.FencedKeys();
		var result = new List<string>();
		foreach (var entry in snapshot.Items)
		{
			if (entry.IsFileSystem && !fenced.Contains(entry.Key) && GetInfo(entry.Key) is { } info && IsCandidate(info))
			{
				result.Add(entry.Key);
			}
		}
		return result;
	}

	/// <summary>
	/// 可被整理的桌面项目：排除隐藏/系统文件、下载中的临时文件，以及本程序自己的目录和映射分区的文件夹。
	/// </summary>
	bool IsCandidate(FileSystemInfo info)
	{
		if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
		{
			return false;
		}
		if (info is FileInfo && DownloadingExtensions.Contains(info.Extension))
		{
			return false;
		}
		var path = info.FullName;
		if (ContainsOrEquals(path, AppPaths.DataDir))
		{
			return false;
		}
		return !_manager.Windows.Any(w => w.Model.IsPortal && ContainsOrEquals(path, w.Model.FolderPath));
	}

	static bool ContainsOrEquals(string folder, string path) => PathUtil.AreEqual(folder, path) || PathUtil.IsUnder(path, folder);

	static FileSystemInfo? GetInfo(string path)
	{
		if (File.Exists(path))
		{
			return new FileInfo(path);
		}
		return Directory.Exists(path) ? new DirectoryInfo(path) : null;
	}

	/// <summary>
	/// 文件仍被写入（下载、保存中）时无法独占打开。
	/// </summary>
	static bool IsLocked(FileInfo file)
	{
		try
		{
			using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
			return false;
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}
}
