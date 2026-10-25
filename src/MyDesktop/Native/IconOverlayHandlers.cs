using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 角标处理程序（ShellIconOverlayIdentifiers）实现的接口：资源管理器逐个问它某个文件要不要叠它的角标。
/// </summary>
[ComImport]
[Guid("0C6C4200-C589-11D0-999A-00C04FD655E1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellIconOverlayIdentifier
{
	[PreserveSig]
	int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);

	[PreserveSig]
	int GetOverlayInfo([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconFile, int maxIconFile, out int index, out uint flags);

	[PreserveSig]
	int GetPriority(out int priority);
}

/// <summary>
/// 注册表里的一个角标处理程序：Name 是键名（常带前导空格，用来挤到前面），DisplayName 去掉了前导空白。
/// </summary>
internal sealed record OverlayHandlerInfo(string Name, Guid Clsid)
{
	public string DisplayName => Name.TrimStart();
}

/// <summary>
/// 角标处理程序在本进程里的加载情况（设置界面上标在每一项后面）。
/// </summary>
internal enum OverlayHandlerState
{
	// 系统已给它分配角标位置，跟随系统时也会显示
	Loaded,
	// 提供了角标，但系统的角标位置已满、没轮到它，跟随系统时不显示
	SlotsFull,
	// GetOverlayInfo 返回 S_FALSE，处理程序自己不提供角标（如 TortoiseGit 设置里关掉了），怎样都不会显示
	NoOverlay,
	// 创建或调用出错
	Failed,
}

/// <summary>
/// 一个处理程序的检查结果：Priority 是它报告的优先级（系统按它排序，越小越优先）。
/// </summary>
internal sealed record OverlayHandlerStatus(OverlayHandlerState State, int Priority);

/// <summary>
/// 自定义顺序的角标。Windows 的角标管理器只给按键名排序的前十几个处理程序分配角标位置，排在后面的在资源管理器里不显示，
/// 优先级也由各处理程序自己报告。自定义顺序时，「系统角标」（Windows 内置的角标，见 ShellIconLoader.SystemEntryOverlay）和勾选的处理程序
/// 排成一个列表，按列表顺序逐个判断，取第一个认领的；处理程序由本程序自己创建。
/// 默认跟随系统，这时加载图标不创建任何处理程序（打开设置页查加载情况时各建一次，见 GetStatusAsync）。
/// 处理程序是 STA 组件，每个图标加载线程各建一套实例。
/// </summary>
internal static class IconOverlayHandlers
{
	const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
	const uint ISIOI_ICONFILE = 0x1;
	const uint ISIOI_ICONINDEX = 0x2;
	// 与 ShellIconLoader.LoadOverlay 从超大图像列表取的角标一样大，缩小绘制不发糊
	const uint OverlaySize = 256;

	/// <summary>
	/// 列表里「系统角标」那一项的 Id。
	/// </summary>
	public const string SystemId = "system";

	/// <summary>
	/// 列表里的一项：处理程序（Identifier、Image 不为空）或「系统角标」（都为空）。
	/// </summary>
	sealed record Entry(string Id, IShellIconOverlayIdentifier? Identifier, BitmapSource? Image);

	// 是否自定义顺序（设置项）
	static volatile bool _custom;

	// 自定义顺序时勾选的项（SystemId 或处理程序的 CLSID），按优先级排好；整体替换，加载线程按引用判断是否变了
	static volatile string[] _enabled = [];

	// 每个图标文件和序号只提取一次，各线程共用
	static readonly ConcurrentDictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);

	// 加载情况只查一次：系统的角标管理器在进程里只初始化一次，之后不会变
	static readonly Lazy<Task<Dictionary<Guid, OverlayHandlerStatus>>> Status = new(StartProbe);

	// 勾选后创建成功过的处理程序（CLSID），取消勾选后扩展仍留在进程里，要重启才卸载
	static readonly ConcurrentDictionary<string, byte> Loaded = new(StringComparer.OrdinalIgnoreCase);

	// 「系统角标」里认不出来的系统角标（序号 → 第一次遇到的文件），设置页列出来，不静默丢掉
	static readonly ConcurrentDictionary<int, string> Others = new();

	// 本线程已创建的项；创建或初始化失败的记 null，不再重试。取消勾选的处理程序不释放，要重启程序才卸载
	[ThreadStatic]
	static Dictionary<string, Entry?>? _created;

	// 本线程按顺序创建好的勾选项，及其对应的勾选列表
	[ThreadStatic]
	static Entry[]? _active;

	[ThreadStatic]
	static string[]? _activeFor;

	public static bool Custom => _custom;

	/// <summary>
	/// 遇到过的、认不出来的系统角标：序号 → 第一次遇到它的文件。
	/// </summary>
	public static IReadOnlyDictionary<int, string> OtherOverlays => Others;

	/// <summary>
	/// 记下一个认不出来的系统角标，每个序号只记一次日志。
	/// </summary>
	public static void ReportOtherOverlay(int overlay, string path)
	{
		if (Others.TryAdd(overlay, path))
		{
			Log.Info($"发现列表外的系统角标：序号 {overlay}，例如 {path}（文件属性 {FileAttributesOf(path):X}）");
		}
	}

	/// <summary>
	/// 按注册表顺序（即系统加载的顺序）读出全部角标处理程序。
	/// </summary>
	public static List<OverlayHandlerInfo> ReadAll()
	{
		var result = new List<OverlayHandlerInfo>();
		try
		{
			using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = root.OpenSubKey(RegistryPath);
			if (key == null)
			{
				return result;
			}
			foreach (var name in key.GetSubKeyNames())
			{
				using var sub = key.OpenSubKey(name);
				if (sub?.GetValue(null) is string value && Guid.TryParse(value.Trim(), out var clsid))
				{
					result.Add(new OverlayHandlerInfo(name, clsid));
				}
				else
				{
					Log.Warn($"角标处理程序的 CLSID 无效：{name}");
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("读取角标处理程序列表失败", ex);
		}
		return result;
	}

	/// <summary>
	/// 查全部处理程序在本进程里的加载情况：逐个创建，取角标图像位置和优先级，再用 SHGetIconOverlayIndex 问系统有没有给这个图像分配角标位置。
	/// 它返回的编号和 IShellIconOverlay.GetOverlayIndex 给图标的角标序号不是一套（实测 Tortoise1Normal 前者 12、后者 5），只能看是否 >= 0。
	/// 处理程序是 STA 组件，可能很慢，在单独的 STA 线程上查，每个进程只查一次。
	/// </summary>
	public static Task<Dictionary<Guid, OverlayHandlerStatus>> GetStatusAsync() => Status.Value;

	static Task<Dictionary<Guid, OverlayHandlerStatus>> StartProbe()
	{
		var source = new TaskCompletionSource<Dictionary<Guid, OverlayHandlerStatus>>();
		var thread = new Thread(() =>
		{
			var status = new Dictionary<Guid, OverlayHandlerStatus>();
			foreach (var handler in ReadAll())
			{
				status.TryAdd(handler.Clsid, Probe(handler));
			}
			source.SetResult(status);
		})
		{
			IsBackground = true,
			Name = "OverlayHandlerProbe",
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return source.Task;
	}

	static OverlayHandlerStatus Probe(OverlayHandlerInfo handler)
	{
		var failed = new OverlayHandlerStatus(OverlayHandlerState.Failed, int.MaxValue);
		var clsid = handler.Clsid;
		var iid = typeof(IShellIconOverlayIdentifier).GUID;
		int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var ptr);
		if (hr != 0 || ptr == IntPtr.Zero)
		{
			Log.Info($"角标处理程序 {handler.DisplayName} {clsid:B} 创建失败：0x{hr:X8}");
			return failed;
		}
		IShellIconOverlayIdentifier? identifier = null;
		try
		{
			identifier = (IShellIconOverlayIdentifier)Marshal.GetObjectForIUnknown(ptr);
			if (identifier.GetPriority(out int priority) != 0)
			{
				priority = int.MaxValue;
			}
			var file = new StringBuilder(260);
			hr = identifier.GetOverlayInfo(file, file.Capacity, out int index, out uint flags);
			if (hr < 0)
			{
				Log.Info($"角标处理程序 {handler.DisplayName} {clsid:B} 读取角标图像失败：0x{hr:X8}");
				return failed;
			}
			if (hr != 0 || (flags & ISIOI_ICONFILE) == 0 || file.Length == 0)
			{
				return new OverlayHandlerStatus(OverlayHandlerState.NoOverlay, priority);
			}
			if ((flags & ISIOI_ICONINDEX) == 0)
			{
				index = 0;
			}
			// 系统按处理程序给的原样记录图像位置，查不到再试展开环境变量后的
			string raw = file.ToString();
			string expanded = Environment.ExpandEnvironmentVariables(raw);
			int overlay = SHGetIconOverlayIndex(raw, index);
			if (overlay < 0 && expanded != raw)
			{
				overlay = SHGetIconOverlayIndex(expanded, index);
			}
			return new OverlayHandlerStatus(overlay >= 0 ? OverlayHandlerState.Loaded : OverlayHandlerState.SlotsFull, priority);
		}
		catch (Exception ex)
		{
			Log.Warn($"检查角标处理程序 {handler.DisplayName} {clsid:B} 失败", ex);
			return failed;
		}
		finally
		{
			if (identifier != null)
			{
				Marshal.ReleaseComObject(identifier);
			}
			Marshal.Release(ptr);
		}
	}

	/// <summary>
	/// 有没有加载过、现在却没在用的处理程序（取消了勾选或改回跟随系统）：它们的扩展要重启程序才卸载。
	/// </summary>
	public static bool HasUnusedLoaded()
	{
		var enabled = _custom ? _enabled : [];
		return Loaded.Keys.Any(id => !enabled.Contains(id, StringComparer.OrdinalIgnoreCase));
	}

	/// <summary>
	/// 应用设置：自定义顺序时 enabled 是勾选的项（SystemId 或处理程序的 CLSID），按优先级排好。改了之后要清空图标缓存重新加载。
	/// </summary>
	public static void Configure(bool custom, IEnumerable<string> enabled)
	{
		_enabled = [.. enabled.Distinct(StringComparer.OrdinalIgnoreCase)];
		_custom = custom;
	}

	/// <summary>
	/// 按列表顺序判断勾选的项，返回第一个认领这个项目的角标图像；都不认领或不是自定义顺序时返回 null。
	/// 轮到「系统角标」时调 systemOverlay 取它的结果。
	/// 处理程序的 IsMemberOf 传 Win32 文件属性（FILE_ATTRIBUTE_*），不是文档说的 SFGAO_*：TortoiseGit 按 FILE_ATTRIBUTE_DIRECTORY 判断是不是文件夹，
	/// 传 SFGAO_FOLDER 时文件夹一个都不认领（实测）。
	/// 只能在图标加载线程（STA）上调用。
	/// </summary>
	public static BitmapSource? Find(string path, Func<BitmapSource?> systemOverlay)
	{
		var enabled = _enabled;
		if (!_custom || enabled.Length == 0)
		{
			return null;
		}
		uint? fileAttributes = null;
		foreach (var entry in ActiveEntries(enabled))
		{
			if (entry.Identifier == null)
			{
				if (systemOverlay() is BitmapSource image)
				{
					return image;
				}
				continue;
			}
			fileAttributes ??= FileAttributesOf(path);
			// 处理程序只认存在的文件系统路径
			if (fileAttributes == 0)
			{
				continue;
			}
			int hr;
			try
			{
				hr = entry.Identifier!.IsMemberOf(path, fileAttributes.Value);
			}
			catch (Exception ex)
			{
				Log.Warn($"角标处理程序 {entry.Id} 判断文件失败：{path}", ex);
				continue;
			}
			if (hr == 0)
			{
				return entry.Image;
			}
		}
		return null;
	}

	/// <summary>
	/// 系统加载的处理程序里有没有认领这个文件的：有的话，系统给它的角标就是那个处理程序的，由列表里那个处理程序自己那一项决定显示不显示。
	/// 会等加载情况查完，只在图标加载线程上调用。
	/// </summary>
	public static bool IsClaimedBySystemHandler(string path)
	{
		uint fileAttributes = FileAttributesOf(path);
		if (fileAttributes == 0)
		{
			return false;
		}
		foreach (var (clsid, status) in GetStatusAsync().Result)
		{
			if (status.State != OverlayHandlerState.Loaded || CreatedEntry(clsid.ToString("B")) is not Entry { Identifier: { } identifier })
			{
				continue;
			}
			try
			{
				if (identifier.IsMemberOf(path, fileAttributes) == 0)
				{
					return true;
				}
			}
			catch (Exception ex)
			{
				Log.Warn($"角标处理程序 {clsid:B} 判断文件失败：{path}", ex);
			}
		}
		return false;
	}

	/// <summary>
	/// 文件系统项目的 Win32 文件属性；此电脑等 ::{CLSID}、已经不存在的项目返回 0，不交给处理程序判断。
	/// </summary>
	static uint FileAttributesOf(string path)
	{
		if (path.Length <= 2 || !(path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)))
		{
			return 0;
		}
		try
		{
			return (uint)File.GetAttributes(path);
		}
		catch (IOException)
		{
			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			return 0;
		}
	}

	static Entry[] ActiveEntries(string[] enabled)
	{
		if (_active != null && ReferenceEquals(_activeFor, enabled))
		{
			return _active;
		}
		var entries = new List<Entry>();
		foreach (var id in enabled)
		{
			if (CreatedEntry(id) is Entry entry)
			{
				entries.Add(entry);
				if (entry.Identifier != null)
				{
					Loaded[id] = 0;
				}
			}
		}
		_active = [.. entries];
		_activeFor = enabled;
		return _active;
	}

	/// <summary>
	/// 本线程的这一项，没有就创建（失败的记 null，不再重试）。
	/// </summary>
	static Entry? CreatedEntry(string id)
	{
		_created ??= new(StringComparer.OrdinalIgnoreCase);
		if (!_created.TryGetValue(id, out var entry))
		{
			entry = CreateEntry(id);
			_created[id] = entry;
		}
		return entry;
	}

	static Entry? CreateEntry(string id)
	{
		if (id == SystemId)
		{
			return new Entry(id, null, null);
		}
		if (Guid.TryParse(id, out var clsid))
		{
			return Create(clsid);
		}
		Log.Warn($"设置里的角标项无效：{id}");
		return null;
	}

	/// <summary>
	/// 在当前线程创建处理程序，取一次角标图像；任何一步失败都写日志并返回 null，这个处理程序就不用了。
	/// </summary>
	static Entry? Create(Guid clsid)
	{
		var iid = typeof(IShellIconOverlayIdentifier).GUID;
		int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var ptr);
		if (hr != 0 || ptr == IntPtr.Zero)
		{
			Log.Warn($"创建角标处理程序 {clsid:B} 失败：0x{hr:X8}");
			return null;
		}
		IShellIconOverlayIdentifier identifier;
		try
		{
			identifier = (IShellIconOverlayIdentifier)Marshal.GetObjectForIUnknown(ptr);
		}
		finally
		{
			Marshal.Release(ptr);
		}
		try
		{
			var file = new StringBuilder(260);
			hr = identifier.GetOverlayInfo(file, file.Capacity, out int index, out uint flags);
			if (hr != 0 || (flags & ISIOI_ICONFILE) == 0 || file.Length == 0)
			{
				Log.Warn($"角标处理程序 {clsid:B} 没有给出角标图像：0x{hr:X8}");
				Marshal.ReleaseComObject(identifier);
				return null;
			}
			if ((flags & ISIOI_ICONINDEX) == 0)
			{
				index = 0;
			}
			string iconFile = Environment.ExpandEnvironmentVariables(file.ToString());
			if (LoadImage(iconFile, index) is not BitmapSource image)
			{
				Log.Warn($"提取角标处理程序 {clsid:B} 的角标图像失败：{iconFile},{index}");
				Marshal.ReleaseComObject(identifier);
				return null;
			}
			Log.Info($"已加载角标处理程序 {clsid:B}");
			return new Entry(clsid.ToString("B"), identifier, image);
		}
		catch (Exception ex)
		{
			Log.Warn($"初始化角标处理程序 {clsid:B} 失败", ex);
			Marshal.ReleaseComObject(identifier);
			return null;
		}
	}

	static BitmapSource? LoadImage(string file, int index)
	{
		return Images.GetOrAdd($"{index}|{file}", _ =>
		{
			if (SHDefExtractIcon(file, index, 0, out var icon, IntPtr.Zero, OverlaySize) != 0 || icon == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return source;
			}
			finally
			{
				DestroyIcon(icon);
			}
		});
	}
}
