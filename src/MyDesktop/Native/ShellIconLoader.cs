using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 在后台 STA 线程中通过 IShellItemImageFactory 加载文件图标或缩略图，带内存缓存。
/// </summary>
internal static class ShellIconLoader
{
	const uint SIIGBF_RESIZETOFIT = 0x00;
	const uint SIIGBF_ICONONLY = 0x04;
	const uint GIL_FORSHELL = 0x0002;
	const uint GIL_CHECKSHIELD = 0x0200;
	const uint GIL_SHIELD = 0x0200;
	const uint SIID_SHIELD = 77;
	const uint SHGSI_ICONLOCATION = 0;
	const int WorkerCount = 2;
	// 缓存满了整个清空重来；常开的分区和桌面一般只有一两百个图标，映射分区里浏览大文件夹时才会涨上去
	const int MaxCacheEntries = 1000;
	const int MaxRetries = 2;

	static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

	sealed record LoadRequest(string Path, int Size, string Key, Action<BitmapSource?> Callback, Dispatcher Dispatcher, int Attempt = 0);

	/// <summary>
	/// 系统图像列表（IImageList）的两个方法，按虚表位置直接调用：
	/// 本进程里该对象不应答 IImageList 的 QueryInterface（实测 E_NOINTERFACE），无法经 COM 接口转换使用。
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	delegate int GetOverlayImageMethod(IntPtr self, int overlay, out int index);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	delegate int GetIconMethod(IntPtr self, int index, uint flags, out IntPtr icon);

	const int GetIconSlot = 10;
	const int GetOverlayImageSlot = 31;

	static readonly BlockingCollection<LoadRequest> Queue = new();
	static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
	static readonly ConcurrentDictionary<int, BitmapSource?> Shields = new();
	static readonly ConcurrentDictionary<int, BitmapSource?> Overlays = new();
	static readonly Lazy<int> LinkOverlay = new(() => SHGetIconOverlayIndex(null, IDO_SHGIOI_LINK));

	static readonly Lazy<BitmapSource?> FolderIconSource = new(LoadFolderIcon);

	/// <summary>
	/// 系统当前的文件夹小图标（映射分区的标题栏和标签上用）：只按「文件夹」这个属性向系统要，
	/// 用户换过图标主题或改过文件夹图标的，拿到的就是那一套。
	/// </summary>
	public static BitmapSource? FolderIcon => FolderIconSource.Value;

	static BitmapSource? LoadFolderIcon()
	{
		var info = new SHFILEINFO();
		if (SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero
				|| info.hIcon == IntPtr.Zero)
		{
			Log.Warn("读取系统文件夹图标失败");
			return null;
		}
		try
		{
			var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			source.Freeze();
			return source;
		}
		finally
		{
			DestroyIcon(info.hIcon);
		}
	}

	/// <summary>
	/// 是否叠加快捷方式小箭头（设置项），改了之后要清空缓存重新加载图标。
	/// </summary>
	public static bool ShowShortcutArrows { get; set; } = true;

	static ShellIconLoader()
	{
		for (int i = 0; i < WorkerCount; i++)
		{
			var thread = new Thread(Work) { IsBackground = true, Name = $"IconLoader-{i}" };
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
		}
	}

	/// <summary>
	/// 请求图标，回调在调用线程的 Dispatcher 上执行；命中缓存时同步回调。
	/// </summary>
	/// <param name="stamp">文件修改时间，文件变化后缓存自动失效。</param>
	/// <param name="variant">系统图标的图标位置（回收站空、满时不同），变化后缓存自动失效。</param>
	public static void Request(string path, int pixelSize, DateTime stamp, Action<BitmapSource?> callback, string? variant = null)
	{
		var key = $"{pixelSize}|{stamp.Ticks}|{variant}|{path}";
		if (Cache.TryGetValue(key, out var cached))
		{
			callback(cached);
			return;
		}
		Queue.Add(new LoadRequest(path, pixelSize, key, callback, Dispatcher.CurrentDispatcher));
	}

	/// <summary>
	/// 缓存只按文件修改时间失效；文件没变而角标状态变了（如兼容性里的「以管理员身份运行」）时整体清空。
	/// </summary>
	public static void ClearCache() => Cache.Clear();

	static void Work()
	{
		foreach (var request in Queue.GetConsumingEnumerable())
		{
			BitmapSource? image = null;
			try
			{
				if (!Cache.TryGetValue(request.Key, out image))
				{
					image = Load(request.Path, request.Size);
					if (image != null)
					{
						if (Cache.Count > MaxCacheEntries)
						{
							Cache.Clear();
						}
						Cache[request.Key] = image;
					}
				}
			}
			catch (Exception ex)
			{
				Log.Warn($"加载图标失败：{request.Path}", ex);
			}
			// 偶尔会取不到图标（实测回收站刚由空变满时），稍后重试，不让图标一直空着
			if (image == null && request.Attempt < MaxRetries)
			{
				var retry = request with { Attempt = request.Attempt + 1 };
				Task.Delay(RetryDelay).ContinueWith(_ => Queue.Add(retry), TaskScheduler.Default);
				continue;
			}
			var result = image;
			request.Dispatcher.BeginInvoke(() => request.Callback(result));
		}
	}

	static BitmapSource? Load(string path, int size)
	{
		var iid = typeof(IShellItemImageFactory).GUID;
		if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
		{
			return null;
		}
		var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ptr);
		try
		{
			// 优先取缩略图（图片、视频等），取不到再退回普通图标
			if (factory.GetImage(new SIZE(size, size), SIIGBF_RESIZETOFIT, out var bitmap) != 0 || bitmap == IntPtr.Zero)
			{
				if (factory.GetImage(new SIZE(size, size), SIIGBF_ICONONLY, out bitmap) != 0 || bitmap == IntPtr.Zero)
				{
					return null;
				}
			}
			BitmapSource image;
			try
			{
				image = ToBitmapSource(bitmap);
			}
			finally
			{
				DeleteObject(bitmap);
			}
			image = StampOverlay(path, image);
			return NeedsShield(path) ? StampShield(image) : image;
		}
		finally
		{
			Marshal.ReleaseComObject(factory);
			Marshal.Release(ptr);
		}
	}

	/// <summary>
	/// 资源管理器按 IExtractIcon 返回的 GIL_SHIELD 决定是否叠加管理员盾牌
	/// （需要提权的程序、勾选了「以管理员身份运行」的快捷方式）。
	/// </summary>
	static bool NeedsShield(string path)
	{
		try
		{
			using var items = ShellItemSet.Create([path]);
			if (items == null)
			{
				return false;
			}
			var ptr = items.GetUIObject(IntPtr.Zero, typeof(IExtractIconW).GUID);
			var extract = (IExtractIconW)Marshal.GetObjectForIUnknown(ptr);
			try
			{
				var location = new StringBuilder(260);
				return extract.GetIconLocation(GIL_FORSHELL | GIL_CHECKSHIELD, location, location.Capacity, out _, out var flags) == 0
						&& (flags & GIL_SHIELD) != 0;
			}
			finally
			{
				Marshal.ReleaseComObject(extract);
				Marshal.Release(ptr);
			}
		}
		catch (Exception ex)
		{
			Log.Warn($"检查管理员盾牌失败：{path}", ex);
			return false;
		}
	}

	/// <summary>
	/// 叠加资源管理器在这个图标上显示的角标（快捷方式箭头、共享、同步状态等），角标图像与图标同样大小、整张盖在图标上。
	/// </summary>
	static BitmapSource StampOverlay(string path, BitmapSource image)
	{
		int overlay;
		try
		{
			using var items = ShellItemSet.Create([path]);
			overlay = items?.GetOverlayIndex() ?? 0;
		}
		catch (Exception ex)
		{
			Log.Warn($"读取图标角标失败：{path}", ex);
			return image;
		}
		if (overlay <= 0 || (!ShowShortcutArrows && overlay == LinkOverlay.Value) || LoadOverlay(overlay) is not BitmapSource badge)
		{
			return image;
		}
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
			context.DrawImage(image, rect);
			context.DrawImage(badge, rect);
		}
		var result = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
		result.Render(visual);
		result.Freeze();
		return result;
	}

	/// <summary>
	/// 从系统超大图像列表取角标图像（缩小绘制不发糊），每种角标只取一次。
	/// </summary>
	static BitmapSource? LoadOverlay(int overlay)
	{
		return Overlays.GetOrAdd(overlay, o =>
		{
			const int SHIL_JUMBO = 4;
			const uint ILD_TRANSPARENT = 0x1;
			var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
			if (SHGetImageList(SHIL_JUMBO, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				var vtable = Marshal.ReadIntPtr(ptr);
				var getOverlay = Marshal.GetDelegateForFunctionPointer<GetOverlayImageMethod>(Marshal.ReadIntPtr(vtable, GetOverlayImageSlot * IntPtr.Size));
				var getIcon = Marshal.GetDelegateForFunctionPointer<GetIconMethod>(Marshal.ReadIntPtr(vtable, GetIconSlot * IntPtr.Size));
				if (getOverlay(ptr, o, out int index) != 0 || index < 0 || getIcon(ptr, index, ILD_TRANSPARENT, out var icon) != 0 || icon == IntPtr.Zero)
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
			}
			finally
			{
				Marshal.Release(ptr);
			}
		});
	}

	/// <summary>
	/// 与资源管理器相同：盾牌边长为图标的一半，贴图标右下角。
	/// </summary>
	static BitmapSource StampShield(BitmapSource image)
	{
		int width = image.PixelWidth;
		int height = image.PixelHeight;
		int shieldSize = Math.Min(width, height) / 2;
		if (LoadShield(shieldSize) is not BitmapSource shield)
		{
			return image;
		}
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			context.DrawImage(image, new Rect(0, 0, width, height));
			context.DrawImage(shield, new Rect(width - shieldSize, height - shieldSize, shieldSize, shieldSize));
		}
		var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		result.Render(visual);
		result.Freeze();
		return result;
	}

	/// <summary>
	/// 按目标尺寸直接从系统资源取盾牌图标（避免缩放发糊），每种尺寸只取一次。
	/// </summary>
	static BitmapSource? LoadShield(int size)
	{
		return Shields.GetOrAdd(size, s =>
		{
			var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
			if (s <= 0
					|| SHGetStockIconInfo(SIID_SHIELD, SHGSI_ICONLOCATION, ref info) != 0
					|| SHDefExtractIcon(info.szPath, info.iIcon, 0, out var icon, IntPtr.Zero, (uint)s) != 0)
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

	/// <summary>
	/// 用 GetDIBits 取出 32 位像素以保留透明通道（Imaging.CreateBitmapSourceFromHBitmap 会丢失 alpha）。
	/// Shell 返回的位图有的自下而上（多数图标），有的自上而下（缩略图、部分图标），
	/// GetObject 读到的高度一律是正数、分不出来，直接读内存会把后一种上下颠倒，所以让 GDI 按位图实际的行序转换。
	/// </summary>
	static BitmapSource ToBitmapSource(IntPtr bitmap)
	{
		if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), out var section) == 0 || section.dsBm.bmBitsPixel != 32)
		{
			var fallback = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			fallback.Freeze();
			return fallback;
		}

		int width = section.dsBm.bmWidth;
		int height = section.dsBm.bmHeight;
		int stride = width * 4;
		var pixels = new byte[stride * height];
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				// 高度为负：按自上而下的行序输出
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var dc = CreateCompatibleDC(IntPtr.Zero);
		try
		{
			if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
			{
				throw new InvalidOperationException($"GetDIBits 读取图标像素失败（{width}×{height}）");
			}
		}
		finally
		{
			DeleteDC(dc);
		}

		// Shell 返回的图标通常是非预乘 alpha（存在颜色值大于 alpha 的像素），按预乘解读会让半透明边缘发白；
		// 预乘数据不可能出现这种像素，据此逐张判断格式
		bool hasAlpha = false;
		bool straightAlpha = false;
		for (int i = 0; i < pixels.Length && !(hasAlpha && straightAlpha); i += 4)
		{
			byte alpha = pixels[i + 3];
			hasAlpha |= alpha != 0;
			straightAlpha |= pixels[i] > alpha || pixels[i + 1] > alpha || pixels[i + 2] > alpha;
		}
		// 部分缩略图不带透明通道（alpha 全为 0），按不透明处理，否则会显示成全透明
		if (!hasAlpha)
		{
			for (int i = 3; i < pixels.Length; i += 4)
			{
				pixels[i] = 0xFF;
			}
		}

		var format = !hasAlpha || straightAlpha ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
		var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
		source.Freeze();
		return source;
	}
}
