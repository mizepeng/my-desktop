using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Views;

namespace MyDesktop.Services;

/// <summary>
/// 桌面整理：一键按规则把桌面文件归入分区；可选监视桌面，把新出现的文件自动归类。
/// </summary>
internal sealed class DesktopOrganizer : IDisposable
{
	static readonly HashSet<string> DownloadingExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload",
	};

	/// <summary>
	/// 只自动整理这段时间内新建的项目：从分区拖回桌面的旧文件（同盘移动会保留创建时间）不会被反复移走。
	/// </summary>
	static readonly TimeSpan NewItemWindow = TimeSpan.FromMinutes(10);

	/// <summary>
	/// 每 4 秒检查一次，最多等约 2 分钟内容稳定，仍在变化的（如大文件解压）放弃自动整理。
	/// </summary>
	const int MaxChecks = 30;

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
			MessageDialog.Show("一键整理桌面", "桌面上没有需要整理的文件。", "确定");
			return;
		}
		int total = plan.Sum(g => g.Paths.Count);
		var summary = string.Join("\n", plan.Select(g => $"    · {g.Rule.Name}：{g.Paths.Count} 个"));
		var message = $"将把桌面上的 {total} 个项目整理到 {plan.Count} 个分区：\n\n{summary}\n\n"
				+ $"文件会移动到「{_manager.StorageRoot}」下对应的文件夹，随时可以拖回桌面。";
		if (MessageDialog.Show("一键整理桌面", message, "开始整理", "取消") != 0)
		{
			return;
		}
		Execute(plan, false);
	}

	public void ApplyWatchSetting()
	{
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
		// 用户正在桌面上重命名（比如刚新建的文件夹），等改完名再处理
		if (DesktopHost.IsRenamingOnDesktop())
		{
			_timer.Start();
			return;
		}
		var ready = new List<string>();
		foreach (var (path, item) in _pending.ToList())
		{
			var info = GetInfo(path);
			if (info == null || !IsCandidate(info) || !IsNewItem(info))
			{
				_pending.Remove(path);
				_downloaded.Remove(path);
				continue;
			}
			// 下载、解压仍在进行时内容会继续变化：连续两次检查内容一致且文件未被占用才移动
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
			Execute(BuildPlan(ready), true);
		}
	}

	bool IsNewItem(FileSystemInfo info)
	{
		return _downloaded.Contains(info.FullName) || DateTime.Now - info.CreationTime < NewItemWindow;
	}

	void Execute(List<(OrganizeRule Rule, List<string> Paths)> plan, bool silent)
	{
		foreach (var (rule, paths) in plan)
		{
			var fence = _manager.GetOrCreateRuleFence(rule, reveal: !silent);
			var folder = fence.Model.FolderPath;
			try
			{
				// 托管文件夹可能刚被外部删除，移动前确保存在
				Directory.CreateDirectory(folder);
			}
			catch (Exception ex)
			{
				Log.Warn($"无法创建分区文件夹，跳过整理：{folder}", ex);
				continue;
			}
			ShellFileOps.Move(silent ? IntPtr.Zero : fence.Handle, paths, folder, renameOnCollision: true, silent: silent);
			fence.ScheduleRefresh();
		}
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

	List<string> EnumerateDesktop()
	{
		var result = new List<string>();
		try
		{
			foreach (var info in new DirectoryInfo(AppPaths.Desktop).EnumerateFileSystemInfos())
			{
				if (IsCandidate(info))
				{
					result.Add(info.FullName);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("枚举桌面文件失败", ex);
		}
		return result;
	}

	/// <summary>
	/// 可被整理的桌面项目：排除隐藏/系统文件、下载中的临时文件，以及本程序自己的目录和分区文件夹。
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
		if (ContainsOrEquals(path, _manager.StorageRoot) || ContainsOrEquals(path, AppPaths.DataDir))
		{
			return false;
		}
		return !_manager.Windows.Any(w => ContainsOrEquals(path, w.Model.FolderPath));
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
