using Microsoft.UI.Dispatching;
using MyDesktop.Core;
using MyDesktop.Services;

namespace MyDesktop.WinUI.Services;

/// <summary>
/// 监视桌面和映射目录的直接子项；文件变化后补读两次，等待 Explorer 完成异步更新。
/// </summary>
internal sealed class FileChangeMonitor : IDisposable
{
	readonly DispatcherQueue _ui;
	readonly Func<IReadOnlyList<FolderWatch>> _folders;
	readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
	readonly DispatcherQueueTimer _rescan, _quick, _settle;
	bool _disposed;

	public FileChangeMonitor(DispatcherQueue ui, Func<IReadOnlyList<FolderWatch>> folders)
	{
		_ui = ui;
		_folders = folders;
		_rescan = ui.CreateTimer();
		_rescan.Interval = TimeSpan.FromSeconds(3);
		_rescan.Tick += (_, _) => CheckFolders();
		_quick = ui.CreateTimer();
		_quick.Interval = TimeSpan.FromMilliseconds(300);
		_quick.IsRepeating = false;
		_quick.Tick += (_, _) => Settled?.Invoke();
		_settle = ui.CreateTimer();
		_settle.Interval = TimeSpan.FromMilliseconds(1200);
		_settle.IsRepeating = false;
		_settle.Tick += (_, _) => Settled?.Invoke();
	}

	public event Func<string, bool>? Relevant;
	public event Action<string, string>? Renamed;
	public event Action? Settled;
	public event Action<string>? Failed;
	internal IReadOnlyList<string> WatchedFolders => _watchers.Keys.ToArray();
	internal bool IsWatching(string folder) => !_disposed && Directory.Exists(folder) && _watchers.Any(pair => PathUtil.AreEqual(pair.Key, folder)
		|| pair.Value.IncludeSubdirectories && PathUtil.IsUnder(folder, pair.Key));

	public void Start()
	{
		CheckFolders();
		_rescan.Start();
	}

	public void CheckFolders()
	{
		if (_disposed)
		{
			return;
		}
		var folders = _folders();
		foreach (string old in _watchers.Keys.Where(folder => !folders.Any(watch => PathUtil.AreEqual(folder, watch.Folder)
			&& _watchers[folder].IncludeSubdirectories == watch.Recursive) || !Directory.Exists(folder)).ToArray())
		{
			_watchers[old].Dispose();
			_watchers.Remove(old);
		}
		foreach (var plan in folders.Where(plan => !_watchers.ContainsKey(plan.Folder) && Directory.Exists(plan.Folder)))
		{
			string folder = plan.Folder;
			FileSystemWatcher watcher;
			try
			{
				watcher = new FileSystemWatcher(folder);
			}
			catch (Exception ex)
			{
				Failed?.Invoke($"无法监视目录：{folder}，{ex.Message}");
				continue;
			}
			watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
			watcher.IncludeSubdirectories = plan.Recursive;
			FileSystemEventHandler changed = (_, args) => Enqueue(() => Changed(args.FullPath));
			watcher.Created += changed;
			watcher.Deleted += changed;
			watcher.Changed += changed;
			watcher.Renamed += (_, args) => Enqueue(() =>
			{
				if (Relevant?.Invoke(args.OldFullPath) == true || Relevant?.Invoke(args.FullPath) == true)
				{
					Renamed?.Invoke(args.OldFullPath, args.FullPath);
					RefreshSoon();
				}
			});
			watcher.Error += (_, args) => Enqueue(() =>
			{
				if (!_watchers.TryGetValue(folder, out var current) || !ReferenceEquals(current, watcher))
				{
					return;
				}
				if (!Directory.Exists(folder))
				{
					CheckFolders();
					RefreshSoon();
					return;
				}
				Failed?.Invoke($"文件监视发生错误：{folder}，{args.GetException().Message}");
			});
			try
			{
				watcher.EnableRaisingEvents = true;
				_watchers.Add(folder, watcher);
			}
			catch (Exception ex)
			{
				watcher.Dispose();
				Failed?.Invoke($"无法监视目录：{folder}，{ex.Message}");
			}
		}
	}

	void Enqueue(Action action) => _ui.TryEnqueue(() => { if (!_disposed) { action(); } });

	void Changed(string path)
	{
		if (Relevant?.Invoke(path) == true)
		{
			RefreshSoon();
		}
	}

	void RefreshSoon()
	{
		_quick.Stop();
		_quick.Start();
		_settle.Stop();
		_settle.Start();
	}

	public void Dispose()
	{
		_disposed = true;
		_rescan.Stop();
		_quick.Stop();
		_settle.Stop();
		foreach (var watcher in _watchers.Values)
		{
			watcher.Dispose();
		}
		_watchers.Clear();
	}
}
