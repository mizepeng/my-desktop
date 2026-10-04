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
	const int MaxCacheEntries = 3000;
	const int MaxRetries = 2;

	static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

	sealed record LoadRequest(string Path, int Size, string Key, Action<BitmapSource?> Callback, Dispatcher Dispatcher, int Attempt = 0);

	static readonly BlockingCollection<LoadRequest> Queue = new();
	static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
	static readonly ConcurrentDictionary<int, BitmapSource?> Shields = new();

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
	public static void Request(string path, int pixelSize, DateTime stamp, Action<BitmapSource?> callback)
	{
		var key = $"{pixelSize}|{stamp.Ticks}|{path}";
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
	/// 直接读取 32 位 DIB 像素以保留透明通道（Imaging.CreateBitmapSourceFromHBitmap 会丢失 alpha）。
	/// </summary>
	static BitmapSource ToBitmapSource(IntPtr bitmap)
	{
		if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), out var section) == 0
				|| section.dsBm.bmBitsPixel != 32
				|| section.dsBm.bmBits == IntPtr.Zero)
		{
			var fallback = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			fallback.Freeze();
			return fallback;
		}

		int width = section.dsBm.bmWidth;
		int height = section.dsBm.bmHeight;
		int stride = width * 4;
		var pixels = new byte[stride * height];
		bool bottomUp = section.dsBmih.biHeight > 0;
		for (int y = 0; y < height; y++)
		{
			int sourceRow = bottomUp ? height - 1 - y : y;
			Marshal.Copy(section.dsBm.bmBits + sourceRow * section.dsBm.bmWidthBytes, pixels, y * stride, stride);
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
