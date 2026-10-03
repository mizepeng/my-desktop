using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
	const int WorkerCount = 2;
	const int MaxCacheEntries = 3000;

	sealed record LoadRequest(string Path, int Size, string Key, Action<BitmapSource?> Callback, Dispatcher Dispatcher);

	static readonly BlockingCollection<LoadRequest> Queue = new();
	static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

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
			try
			{
				return ToBitmapSource(bitmap);
			}
			finally
			{
				DeleteObject(bitmap);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(factory);
			Marshal.Release(ptr);
		}
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

		// 部分缩略图不带透明通道（alpha 全为 0），按不透明处理，否则会显示成全透明
		bool hasAlpha = false;
		for (int i = 3; i < pixels.Length; i += 4)
		{
			if (pixels[i] != 0)
			{
				hasAlpha = true;
				break;
			}
		}
		if (!hasAlpha)
		{
			for (int i = 3; i < pixels.Length; i += 4)
			{
				pixels[i] = 0xFF;
			}
		}

		var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
		source.Freeze();
		return source;
	}
}
