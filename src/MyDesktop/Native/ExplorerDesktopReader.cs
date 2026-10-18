using System.Runtime.InteropServices;
using System.Text;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 不依赖界面框架的 Explorer 桌面 COM 读取器，所有调用必须位于同一个 STA 线程。
/// </summary>
internal sealed class ExplorerDesktopReader : IDisposable
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
	string[] _desktopFolders = [];
	IFolderView2? _view;
	IShellFolder? _desktop;

	public DesktopSnapshot? Read() => ReadSnapshot();

	public void ClearSelection()
	{
		if (EnsureView() && _view!.ItemCount(SVGIO_ALLVIEW, out int count) == 0 && count > 0)
		{
			_view.SelectItem(0, SVSI_DESELECTOTHERS | SVSI_NOTAKEFOCUS);
		}
	}

	public void Reset() => ResetView();

	public void Dispose()
	{
		ResetView();
		if (_desktop != null)
		{
			Marshal.ReleaseComObject(_desktop);
			_desktop = null;
		}
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
		var listView = ExplorerDesktopWindow.FindListView();
		var bounds = listView == IntPtr.Zero ? default : NativeMethods.GetWindowRect(listView);
		return new DesktopSnapshot(autoArrange, CorrectSpacing(spacing, autoArrange, items, listView), iconSize, bounds, items);
	}

	/// <summary>
	/// 资源管理器按图标列表窗口自己的 DPI 换算间距，缩放比例变了而这个窗口的 DPI 没跟上时（实测隐藏期间一直停在旧值），
	/// 报告的间距会按两者之比放大或缩小，图标位置却已按新比例排好。自动排列时以位置为准，用同一列相邻图标的距离校正；
	/// 算不出时按主显示器的实际 DPI 与图标列表窗口 DPI 之比换算。两者一致时原样返回。
	/// </summary>
	static POINT CorrectSpacing(POINT spacing, bool autoArrange, List<DesktopEntry> items, IntPtr listView)
	{
		double factor = 1;
		int rowStep = autoArrange ? RowStep(items) : 0;
		if (rowStep > 0 && spacing.Y > 0)
		{
			factor = (double)rowStep / spacing.Y;
		}
		else if (GetDpiForWindow(listView) is uint listDpi and > 0
				&& GetDpiForMonitor(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY), MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
		{
			factor = (double)dpi / listDpi;
		}
		if (Math.Abs(factor - 1) < 0.01)
		{
			return spacing;
		}
		return new POINT((int)Math.Round(spacing.X * factor), (int)Math.Round(spacing.Y * factor));
	}

	/// <summary>
	/// 自动排列时同一列里相邻两个图标的纵向距离（取最小的），同一列不足两个图标时返回 0。
	/// </summary>
	static int RowStep(List<DesktopEntry> items)
	{
		int step = 0;
		foreach (var column in items.GroupBy(i => i.Position.X))
		{
			var rows = column.Select(i => i.Position.Y).Order().ToList();
			for (int i = 1; i < rows.Count; i++)
			{
				int distance = rows[i] - rows[i - 1];
				if (distance > 0 && (step == 0 || distance < step))
				{
					step = distance;
				}
			}
		}
		return step;
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

	public void Move(string[] keys, POINT[] points)
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

	public string? QueryFocused()
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
