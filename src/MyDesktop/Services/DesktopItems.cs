using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Native;

namespace MyDesktop.Services;

/// <summary>
/// 桌面上的全部项目，以资源管理器桌面视图的快照为准（显示哪些系统图标、是否显示隐藏文件等都跟随系统设置）；
/// 另外监视用户桌面和公共桌面两个文件夹，文件增删改名后及时刷新，并报告改名以便分区成员跟着改。
/// </summary>
internal sealed class DesktopItems : IDisposable
{
	readonly ExplorerDesktopView _view;
	readonly Dispatcher _dispatcher;
	readonly List<FileSystemWatcher> _watchers = [];
	string[] _folders = [];
	readonly DispatcherTimer _quickTimer;
	readonly DispatcherTimer _settleTimer;

	public DesktopItems(Dispatcher dispatcher)
	{
		_dispatcher = dispatcher;
		_view = new ExplorerDesktopView(dispatcher);
		_view.Changed += snapshot =>
		{
			Snapshot = snapshot;
			Changed?.Invoke();
		};
		// 文件增删后资源管理器要过一会儿才更新视图：先尽快读一次，稍后再补读一次
		_quickTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
		_quickTimer.Tick += (_, _) =>
		{
			_quickTimer.Stop();
			_view.Refresh();
		};
		_settleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(1200) };
		_settleTimer.Tick += (_, _) =>
		{
			_settleTimer.Stop();
			_view.Refresh();
			// 只改了内容（大小、修改时间）的文件不会让快照变化，也要通知分区更新缩略图和提示信息
			if (Snapshot != null)
			{
				Changed?.Invoke();
			}
		};
		CheckFolders();
		_view.Refresh();
	}

	/// <summary>
	/// 监视用户桌面和公共桌面；桌面文件夹被迁移（如 OneDrive 备份桌面）后改为监视新位置。由看门狗定时调用。
	/// </summary>
	public void CheckFolders()
	{
		var folders = new[] { AppPaths.Desktop, AppPaths.CommonDesktop }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (folders.SequenceEqual(_folders, StringComparer.OrdinalIgnoreCase))
		{
			return;
		}
		bool moved = _folders.Length > 0;
		_folders = folders;
		foreach (var watcher in _watchers)
		{
			watcher.Dispose();
		}
		_watchers.Clear();
		foreach (var folder in folders)
		{
			Watch(folder, _dispatcher);
		}
		if (moved)
		{
			Log.Info($"桌面文件夹位置变化，改为监视：{string.Join("、", folders)}");
			RefreshSoon();
		}
	}

	/// <summary>
	/// 最近一次读取到的桌面视图；还没读到（资源管理器未就绪）时为 null。
	/// </summary>
	public DesktopSnapshot? Snapshot { get; private set; }

	/// <summary>
	/// 桌面项目、排列方式或文件内容有变化，在界面线程上触发。
	/// </summary>
	public event Action? Changed;

	/// <summary>
	/// 文件改名（旧完整路径、新完整路径），在界面线程上触发。
	/// </summary>
	public event Action<string, string>? Renamed;

	public void Refresh() => _view.Refresh();

	/// <summary>
	/// 稍后刷新两次，用于资源管理器异步更新视图的场合（文件变化、在系统菜单里改了排列方式等）。
	/// </summary>
	public void RefreshSoon()
	{
		_quickTimer.Stop();
		_quickTimer.Start();
		_settleTimer.Stop();
		_settleTimer.Start();
	}

	/// <summary>
	/// 把图标写回资源管理器的位置（资源管理器图标列表坐标，物理像素）。
	/// </summary>
	public void Move(IReadOnlyList<string> keys, IReadOnlyList<POINT> points) => _view.Move(keys, points);

	public void QueryFocused(Action<string?> callback) => _view.QueryFocused(callback);

	/// <summary>
	/// 取消资源管理器隐藏视图里的选择。
	/// </summary>
	public void ClearExplorerSelection() => _view.ClearSelection();

	public DesktopEntry? Find(string key) => Snapshot?.ByKey.GetValueOrDefault(key);

	public void Dispose()
	{
		_quickTimer.Stop();
		_settleTimer.Stop();
		foreach (var watcher in _watchers)
		{
			watcher.Dispose();
		}
		_watchers.Clear();
		_view.Dispose();
	}

	void Watch(string folder, Dispatcher dispatcher)
	{
		try
		{
			var watcher = new FileSystemWatcher(folder)
			{
				IncludeSubdirectories = false,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
			};
			FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(RefreshSoon);
			watcher.Created += changed;
			watcher.Deleted += changed;
			watcher.Changed += changed;
			watcher.Renamed += (_, e) => dispatcher.BeginInvoke(() =>
			{
				// 替换快捷方式时更新程序先把旧文件改成备份名（不再以 .lnk 结尾）再写新的，
				// 这一下不是用户改名，分区应继续指向原路径；备份随后被删除也不该把成员带走。
				bool replaced = string.Equals(Path.GetExtension(e.OldFullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(Path.GetExtension(e.FullPath), ".lnk", StringComparison.OrdinalIgnoreCase);
				if (!replaced)
				{
					Renamed?.Invoke(e.OldFullPath, e.FullPath);
				}
				RefreshSoon();
			});
			watcher.Error += (_, _) => dispatcher.BeginInvoke(RefreshSoon);
			watcher.EnableRaisingEvents = true;
			_watchers.Add(watcher);
		}
		catch (Exception ex)
		{
			Log.Warn($"监视桌面文件夹失败：{folder}", ex);
		}
	}
}
