using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 资源管理器桌面上的一个项目。Key 为完整解析名：文件是完整路径，此电脑等系统图标是 ::{CLSID}。
/// Position 是它在资源管理器图标列表里的位置（物理像素，相对列表左上角）。
/// IconKey 是系统图标当前使用的图标位置（回收站空、满时不同），用来判断图标是否需要重新加载。
/// </summary>
internal sealed record DesktopEntry(string Key, string DisplayName, bool IsFileSystem, bool IsFolder, POINT Position, string? IconKey = null,
		bool CanRename = true, bool CanDelete = true);

/// <summary>
/// 一次读取到的资源管理器桌面视图：排列方式、间距、图标大小、列表在屏幕上的范围和全部项目。
/// </summary>
internal sealed class DesktopSnapshot
{
	public DesktopSnapshot(bool autoArrange, POINT spacing, int iconSize, RECT listBounds, IReadOnlyList<DesktopEntry> items)
	{
		AutoArrange = autoArrange;
		Spacing = spacing;
		IconSize = iconSize;
		ListBounds = listBounds;
		Items = items;
		var byKey = new Dictionary<string, DesktopEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items)
		{
			byKey.TryAdd(item.Key, item);
		}
		ByKey = byKey;
		var sb = new StringBuilder();
		sb.Append(autoArrange).Append('|').Append(spacing.X).Append('x').Append(spacing.Y).Append('|').Append(iconSize)
				.Append('|').Append(listBounds.Left).Append(',').Append(listBounds.Top).Append(',').Append(listBounds.Right).Append(',').Append(listBounds.Bottom);
		foreach (var item in items)
		{
			sb.Append('\n').Append(item.Key).Append('|').Append(item.DisplayName).Append('|').Append(item.Position.X).Append(',').Append(item.Position.Y)
					.Append('|').Append(item.IconKey);
		}
		Signature = sb.ToString();
	}

	public bool AutoArrange { get; }

	/// <summary>
	/// 图标格子的间距（物理像素）。
	/// </summary>
	public POINT Spacing { get; }

	/// <summary>
	/// 资源管理器设置的图标大小（与缩放无关的逻辑像素，中等图标为 48）。
	/// </summary>
	public int IconSize { get; }

	/// <summary>
	/// 资源管理器图标列表在屏幕上的范围（物理像素），项目位置以它的左上角为原点。
	/// </summary>
	public RECT ListBounds { get; }

	public IReadOnlyList<DesktopEntry> Items { get; }

	public IReadOnlyDictionary<string, DesktopEntry> ByKey { get; }

	/// <summary>
	/// 用于判断两次读取之间有没有变化。
	/// </summary>
	public string Signature { get; }
}

/// <summary>
/// 跨进程读取资源管理器的桌面视图（IShellWindows → IShellBrowser → IFolderView2），并把图标位置写回。
/// 散放图标的排列、顺序、位置、间距和图标大小都以它为准，在桌面右键菜单「查看」「排序方式」里的改动也就自然跟随。
/// 资源管理器的 COM 代理只能在创建它的线程上使用，资源管理器无响应时调用会一直等待，
/// 所以全部放在独立的 STA 线程上；界面线程只拿到读取完成的快照，不会被拖住。
/// </summary>
internal sealed class ExplorerDesktopView : IDisposable
{
	static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
	static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
	const int CSIDL_DESKTOP = 0;
	const int SWC_DESKTOP = 8;
	const int SWFO_NEEDDISPATCH = 1;
	const uint SVGIO_ALLVIEW = 2;
	const uint SVSI_DESELECTOTHERS = 0x4;
	const uint SVSI_POSITIONITEM = 0x80;
	const uint SVSI_NOTAKEFOCUS = 0x40000000;
	const int DefaultIconSize = 48;
	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

	readonly Dispatcher _ui;
	readonly Dispatcher _worker;
	string[] _desktopFolders = [];
	IFolderView2? _view;
	IShellFolder? _desktop;
	string? _lastSignature;
	int _refreshQueued;

	public ExplorerDesktopView(Dispatcher ui)
	{
		_ui = ui;
		Dispatcher? worker = null;
		using var ready = new ManualResetEventSlim();
		var thread = new Thread(() =>
		{
			worker = Dispatcher.CurrentDispatcher;
			ready.Set();
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "ExplorerDesktopView",
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		ready.Wait();
		_worker = worker!;
		// 兜底轮询：资源管理器的有些变化（比如在「查看」里改图标大小）没有能直接收到的通知
		_worker.BeginInvoke(() => new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Read(), _worker));
	}

	/// <summary>
	/// 读取到与上次不同的快照时触发，在界面线程上执行。
	/// </summary>
	public event Action<DesktopSnapshot>? Changed;

	/// <summary>
	/// 请求重新读取；排队中的多次请求合并为一次。
	/// </summary>
	public void Refresh()
	{
		if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
		{
			return;
		}
		_worker.BeginInvoke(() =>
		{
			Interlocked.Exchange(ref _refreshQueued, 0);
			Read();
		});
	}

	/// <summary>
	/// 把图标移到资源管理器列表坐标中的位置（只改位置，不选中、不抢焦点），完成后重新读取。
	/// </summary>
	public void Move(IReadOnlyList<string> keys, IReadOnlyList<POINT> points)
	{
		var keyArray = keys.ToArray();
		var pointArray = points.ToArray();
		_worker.BeginInvoke(() =>
		{
			MoveCore(keyArray, pointArray);
			Read();
		});
	}

	/// <summary>
	/// 查询资源管理器桌面视图当前的焦点项目，结果在界面线程回调。
	/// </summary>
	public void QueryFocused(Action<string?> callback)
	{
		_worker.BeginInvoke(() =>
		{
			string? key = null;
			try
			{
				key = ReadFocused();
			}
			catch (Exception ex)
			{
				Log.Warn("读取资源管理器焦点项目失败", ex);
				ResetView();
			}
			_ui.BeginInvoke(() => callback(key));
		});
	}

	/// <summary>
	/// 取消资源管理器隐藏视图里的选择。键盘钩子之外再加一道保险：即使有按键绕过钩子直接送到资源管理器，
	/// 也没有看不见的选中项可供删除。
	/// </summary>
	public void ClearSelection()
	{
		_worker.BeginInvoke(() =>
		{
			try
			{
				if (EnsureView() && _view!.ItemCount(SVGIO_ALLVIEW, out int count) == 0 && count > 0)
				{
					// 第 0 项本身也是「取消选择」（不带 SVSI_SELECT），加上 DESELECTOTHERS 即全部取消
					_view.SelectItem(0, SVSI_DESELECTOTHERS | SVSI_NOTAKEFOCUS);
				}
			}
			catch (Exception ex)
			{
				Log.Warn("取消资源管理器的选择失败", ex);
				ResetView();
			}
		});
	}

	public void Dispose() => _worker.BeginInvokeShutdown(DispatcherPriority.Normal);

	void Read()
	{
		DesktopSnapshot? snapshot;
		try
		{
			snapshot = ReadSnapshot();
		}
		catch (Exception ex)
		{
			// 资源管理器重启后旧代理全部失效，下次读取时重新获取；工作线程上的异常不能抛出，否则整个进程会退出
			Log.Warn("读取资源管理器桌面视图失败", ex);
			ResetView();
			return;
		}
		if (snapshot == null || snapshot.Signature == _lastSignature)
		{
			return;
		}
		_lastSignature = snapshot.Signature;
		_ui.BeginInvoke(() => Changed?.Invoke(snapshot));
	}

	DesktopSnapshot? ReadSnapshot()
	{
		if (!EnsureView())
		{
			return null;
		}
		var view = _view!;
		var desktop = _desktop!;
		// 每次都重新取：桌面文件夹可能被 OneDrive 等迁移到别处
		_desktopFolders = [PathUtil.Normalize(AppPaths.Desktop), PathUtil.Normalize(AppPaths.CommonDesktop)];
		Marshal.ThrowExceptionForHR(view.ItemCount(SVGIO_ALLVIEW, out int count));
		Marshal.ThrowExceptionForHR(view.GetSpacing(out var spacing));
		bool autoArrange = view.GetAutoArrange() == 0;
		int iconSize = view.GetViewModeAndIconSize(out _, out int size) == 0 && size > 0 ? size : DefaultIconSize;
		var items = new List<DesktopEntry>(count);
		for (int i = 0; i < count; i++)
		{
			if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero)
			{
				continue;
			}
			try
			{
				view.GetItemPosition(pidl, out var position);
				var key = ShellNames.Get(desktop, pidl, SHGDN_FORPARSING);
				if (key == null)
				{
					continue;
				}
				var name = ShellNames.Get(desktop, pidl, SHGDN_NORMAL) ?? key;
				uint attributes = SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_STREAM | SFGAO_CANRENAME | SFGAO_CANDELETE;
				desktop.GetAttributesOf(1, [pidl], ref attributes);
				// 压缩包这类同时带 FOLDER 和 STREAM 的按文件处理
				bool folder = (attributes & SFGAO_FOLDER) != 0 && (attributes & SFGAO_STREAM) == 0;
				bool fileSystem = (attributes & SFGAO_FILESYSTEM) != 0 && IsInDesktopFolder(key);
				var iconKey = fileSystem ? null : ReadIconLocation(desktop, pidl);
				items.Add(new DesktopEntry(key, name, fileSystem, folder, position, iconKey,
						(attributes & SFGAO_CANRENAME) != 0, (attributes & SFGAO_CANDELETE) != 0));
			}
			finally
			{
				Marshal.FreeCoTaskMem(pidl);
			}
		}
		var listView = DesktopHost.FindFolderView();
		var bounds = listView == IntPtr.Zero ? default : NativeMethods.GetWindowRect(listView);
		return new DesktopSnapshot(autoArrange, spacing, iconSize, bounds, items);
	}

	/// <summary>
	/// 系统图标当前使用的图标位置，例如回收站空时与满时分别是 imageres.dll 里的不同图标。
	/// </summary>
	static string? ReadIconLocation(IShellFolder desktop, IntPtr pidl)
	{
		const uint GIL_FORSHELL = 0x2;
		var iid = typeof(IExtractIconW).GUID;
		if (desktop.GetUIObjectOf(IntPtr.Zero, 1, [pidl], ref iid, IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			var extract = (IExtractIconW)Marshal.GetObjectForIUnknown(ptr);
			try
			{
				var location = new StringBuilder(260);
				return extract.GetIconLocation(GIL_FORSHELL, location, location.Capacity, out int index, out _) == 0 ? $"{location},{index}" : null;
			}
			finally
			{
				Marshal.ReleaseComObject(extract);
			}
		}
		finally
		{
			Marshal.Release(ptr);
		}
	}

	/// <summary>
	/// 只有真正放在桌面文件夹（用户桌面、公共桌面）里的项目才按文件处理。用户文件夹、OneDrive 这类系统图标的解析名
	/// 也是完整路径（如 C:\Users\xxx），对它们删除、改名、移动会作用到真实的文件夹上，必须按系统图标处理。
	/// </summary>
	bool IsInDesktopFolder(string key)
	{
		if (!Path.IsPathFullyQualified(key) || Path.GetDirectoryName(key) is not string parent)
		{
			return false;
		}
		parent = PathUtil.Normalize(parent);
		return _desktopFolders.Any(f => string.Equals(f, parent, StringComparison.OrdinalIgnoreCase));
	}

	void MoveCore(string[] keys, POINT[] points)
	{
		try
		{
			if (!EnsureView())
			{
				return;
			}
			var view = _view!;
			var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < keys.Length; i++)
			{
				index.TryAdd(keys[i], i);
			}
			var pidls = new IntPtr[keys.Length];
			try
			{
				Marshal.ThrowExceptionForHR(view.ItemCount(SVGIO_ALLVIEW, out int count));
				for (int i = 0; i < count; i++)
				{
					if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero)
					{
						continue;
					}
					var key = ShellNames.Get(_desktop!, pidl, SHGDN_FORPARSING);
					if (key != null && index.TryGetValue(key, out int target) && pidls[target] == IntPtr.Zero)
					{
						pidls[target] = pidl;
					}
					else
					{
						Marshal.FreeCoTaskMem(pidl);
					}
				}
				var found = Enumerable.Range(0, keys.Length).Where(i => pidls[i] != IntPtr.Zero).ToArray();
				if (found.Length > 0)
				{
					view.SelectAndPositionItems((uint)found.Length, found.Select(i => pidls[i]).ToArray(), found.Select(i => points[i]).ToArray(),
							SVSI_POSITIONITEM | SVSI_NOTAKEFOCUS);
				}
			}
			finally
			{
				foreach (var pidl in pidls.Where(p => p != IntPtr.Zero))
				{
					Marshal.FreeCoTaskMem(pidl);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("写回桌面图标位置失败", ex);
			ResetView();
		}
	}

	string? ReadFocused()
	{
		if (!EnsureView() || _view!.GetFocusedItem(out int index) != 0 || index < 0 || _view.Item(index, out var pidl) != 0)
		{
			return null;
		}
		try
		{
			return ShellNames.Get(_desktop!, pidl, SHGDN_FORPARSING);
		}
		finally
		{
			Marshal.FreeCoTaskMem(pidl);
		}
	}

	bool EnsureView()
	{
		if (_view != null)
		{
			return true;
		}
		if (_desktop == null)
		{
			Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var desktopPtr));
			_desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
			Marshal.Release(desktopPtr);
		}
		var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;
		try
		{
			object location = CSIDL_DESKTOP;
			// 空 VARIANT（VT_EMPTY）
			object root = null!;
			if (windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH, out var dispatch) != 0 || dispatch == null)
			{
				return false;
			}
			try
			{
				var service = TopLevelBrowserService;
				var iid = typeof(IShellBrowser).GUID;
				if (((IComServiceProvider)dispatch).QueryService(ref service, ref iid, out var browserPtr) != 0 || browserPtr == IntPtr.Zero)
				{
					return false;
				}
				var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
				Marshal.Release(browserPtr);
				try
				{
					if (browser.QueryActiveShellView(out var view) == 0 && view is IFolderView2 folderView)
					{
						_view = folderView;
					}
				}
				finally
				{
					Marshal.ReleaseComObject(browser);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(dispatch);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(windows);
		}
		return _view != null;
	}

	void ResetView()
	{
		if (_view == null)
		{
			return;
		}
		try
		{
			Marshal.ReleaseComObject(_view);
		}
		catch (Exception)
		{
			// 代理已失效时释放也可能失败，忽略
		}
		_view = null;
	}
}
