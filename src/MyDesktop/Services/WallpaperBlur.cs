using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Services;

/// <summary>
/// 分区的毛玻璃背景。系统的模糊对分区这类不激活的窗口不起作用（见 CLAUDE.md），改为自己画：
/// 按各显示器的壁纸和契合度把壁纸画成缩小的图并模糊，拼成覆盖整个虚拟屏幕的一张图，分区按自己在屏幕上的位置截取对应的一块铺在背景颜色下面。
/// 分区下面只有壁纸（散放图标会避开分区），看上去就和毛玻璃一样。壁纸设置由资源管理器提供（跨进程 COM），在后台线程读取，资源管理器无响应时不会拖住界面。
/// 屏幕上的壁纸是动态的（Wallpaper Engine 等动态壁纸、视频壁纸）或读不出图片时没有图，分区显示为半透明，动画直接透出来。
/// </summary>
internal sealed class WallpaperBlur
{
	/// <summary>
	/// 动态壁纸时的签名，与任何静态壁纸的签名都不同。
	/// </summary>
	const string LiveSignature = "live";

	/// <summary>
	/// 按屏幕像素的 1/8 绘制再模糊：模糊后的图放大显示依然平滑，绘制和内存开销都很小。
	/// </summary>
	const double ImageScale = 1 / 8.0;

	/// <summary>
	/// 模糊半径（缩小后的像素），相当于屏幕上约 50 像素。
	/// </summary>
	const double BlurRadius = 6;

	/// <summary>
	/// 解码壁纸文件时的最大宽度，模糊后看不出细节，没必要按原尺寸解码。
	/// </summary>
	const int MaxDecodeWidth = 1024;

	/// <summary>
	/// 系统缓存的当前壁纸，换壁纸（包括幻灯片放映换图）时会更新。
	/// </summary>
	static readonly string TranscodedWallpaper = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

	readonly DispatcherTimer _checkTimer;
	RECT _bounds;
	string? _signature;
	string? _quickSignature;
	bool _refreshing;
	bool _pending;

	public WallpaperBlur()
	{
		// 幻灯片放映换图、动态壁纸开关都不一定有通知，每秒比对一次（只读注册表、文件时间和窗口，开销很小）
		_checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_checkTimer.Tick += (_, _) => CheckForChanges();
	}

	/// <summary>
	/// 模糊壁纸换了。
	/// </summary>
	public event Action? Changed;

	/// <summary>
	/// 覆盖整个虚拟屏幕的模糊壁纸；还没读到或读取失败时为 null。
	/// </summary>
	public BitmapSource? Image { get; private set; }

	/// <summary>
	/// 屏幕上的范围（物理像素）在模糊壁纸里对应的区域，用作分区背景画刷的 Viewbox。
	/// </summary>
	public Rect ViewboxFor(RECT rect)
	{
		return new Rect((rect.Left - _bounds.Left) * ImageScale, (rect.Top - _bounds.Top) * ImageScale, rect.Width * ImageScale, rect.Height * ImageScale);
	}

	public void Start()
	{
		_checkTimer.Start();
		Refresh();
	}

	public void Stop() => _checkTimer.Stop();

	/// <summary>
	/// 比较注册表里的壁纸设置、系统缓存的当前壁纸和有没有动态壁纸，变了就重新读取。
	/// </summary>
	void CheckForChanges()
	{
		var quick = QuickSignature();
		if (quick != _quickSignature)
		{
			_quickSignature = quick;
			Refresh();
		}
	}

	/// <summary>
	/// 重新读取壁纸设置，壁纸、契合度或显示器有变化时重画；正在读取时再次调用会在读完后再读一遍。
	/// </summary>
	public async void Refresh()
	{
		if (_refreshing)
		{
			_pending = true;
			return;
		}
		_refreshing = true;
		try
		{
			do
			{
				_pending = false;
				if (HasLiveWallpaper())
				{
					SetImage(null, LiveSignature);
					continue;
				}
				var monitors = await Task.Run(ReadMonitors).WaitAsync(TimeSpan.FromSeconds(10));
				var signature = string.Join("|", monitors.Select(m => m.Signature));
				if (signature == _signature)
				{
					continue;
				}
				var images = await Task.Run(() => LoadImages(monitors));
				// 有显示器的壁纸读不出图片（如视频壁纸），模糊出来的和屏幕上的对不上，不如半透明
				if (monitors.Any(m => m.Path.Length > 0 && !images.ContainsKey(m.Path)))
				{
					SetImage(null, signature);
					continue;
				}
				(var image, _bounds) = Render(monitors, images);
				SetImage(image, signature);
			}
			while (_pending);
		}
		catch (Exception ex)
		{
			Log.Warn("读取桌面壁纸失败，分区背景暂时显示为半透明", ex);
		}
		finally
		{
			_refreshing = false;
		}
	}

	void SetImage(BitmapSource? image, string signature)
	{
		if (signature == _signature)
		{
			return;
		}
		Image = image;
		_signature = signature;
		Changed?.Invoke();
	}

	static string QuickSignature()
	{
		using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
		using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
		long written = File.Exists(TranscodedWallpaper) ? File.GetLastWriteTimeUtc(TranscodedWallpaper).Ticks : 0;
		return $"{desktop?.GetValue("WallPaper")}|{desktop?.GetValue("WallpaperStyle")}|{desktop?.GetValue("TileWallpaper")}|{colors?.GetValue("Background")}|{written}|{HasLiveWallpaper()}";
	}

	/// <summary>
	/// Wallpaper Engine、Lively 等动态壁纸把自己的窗口挂在桌面最底层播放：挂在 Progman 或资源管理器的 WorkerW 下面
	/// （新版系统 WorkerW 本身也挂在 Progman 下面）。这些地方平时只有资源管理器自己的窗口，出现别的程序的可见窗口就是动态壁纸。
	/// </summary>
	static bool HasLiveWallpaper()
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		if (progman == IntPtr.Zero || GetWindowThreadProcessId(progman, out uint explorer) == 0)
		{
			return false;
		}
		var hosts = new List<IntPtr> { progman };
		for (var child = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null); child != IntPtr.Zero; child = FindWindowEx(progman, child, "WorkerW", null))
		{
			hosts.Add(child);
		}
		EnumWindows((hwnd, _) =>
		{
			if (IsWindowVisible(hwnd) && GetClassName(hwnd) == "WorkerW" && GetWindowThreadProcessId(hwnd, out uint owner) != 0 && owner == explorer)
			{
				hosts.Add(hwnd);
			}
			return true;
		}, IntPtr.Zero);
		foreach (var host in hosts)
		{
			for (var child = FindWindowEx(host, IntPtr.Zero, null, null); child != IntPtr.Zero; child = FindWindowEx(host, child, null, null))
			{
				if (IsWindowVisible(child) && GetWindowThreadProcessId(child, out uint owner) != 0 && owner != explorer)
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// 在后台线程读取各显示器的范围、壁纸文件、契合度和背景色。
	/// </summary>
	static List<MonitorWallpaper> ReadMonitors()
	{
		var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
		try
		{
			var position = wallpaper.GetPosition();
			uint color = wallpaper.GetBackgroundColor();
			var background = Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16));
			var found = new List<(RECT Rect, string Path)>();
			uint count = wallpaper.GetMonitorDevicePathCount();
			for (uint i = 0; i < count; i++)
			{
				var id = wallpaper.GetMonitorDevicePathAt(i);
				var rect = wallpaper.GetMonitorRECT(id);
				// 列表里还有没接上的显示器，它们的范围是空的
				if (rect.Width <= 0 || rect.Height <= 0)
				{
					continue;
				}
				var path = wallpaper.GetWallpaper(id) ?? string.Empty;
				found.Add((rect, File.Exists(path) ? path : string.Empty));
			}
			var span = found.Count == 0 ? default : found.Select(m => m.Rect).Aggregate(Union);
			return found.Select(m => new MonitorWallpaper(m.Rect, span, m.Path, position, background)).ToList();
		}
		finally
		{
			Marshal.FinalReleaseComObject(wallpaper);
		}
	}

	static Dictionary<string, WallpaperImage> LoadImages(List<MonitorWallpaper> monitors)
	{
		var images = new Dictionary<string, WallpaperImage>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in monitors.Select(m => m.Path).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				images[path] = LoadImage(path);
			}
			catch (Exception ex)
			{
				Log.Warn($"读取壁纸文件失败：{path}", ex);
			}
		}
		return images;
	}

	static WallpaperImage LoadImage(string path)
	{
		int width, height;
		using (var stream = File.OpenRead(path))
		{
			var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
			(width, height) = (frame.PixelWidth, frame.PixelHeight);
		}
		var bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.UriSource = new Uri(path);
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
		bitmap.DecodePixelWidth = Math.Min(width, MaxDecodeWidth);
		bitmap.EndInit();
		bitmap.Freeze();
		return new WallpaperImage(bitmap, width, height);
	}

	/// <summary>
	/// 各显示器分别画好、模糊，再按位置拼成覆盖整个虚拟屏幕的一张图。
	/// </summary>
	static (BitmapSource? Image, RECT Bounds) Render(List<MonitorWallpaper> monitors, Dictionary<string, WallpaperImage> images)
	{
		if (monitors.Count == 0)
		{
			return (null, default);
		}
		var bounds = monitors[0].Span;
		var canvas = new DrawingVisual();
		using (var dc = canvas.RenderOpen())
		{
			foreach (var monitor in monitors)
			{
				var blurred = RenderMonitor(monitor, images.GetValueOrDefault(monitor.Path));
				dc.DrawImage(blurred, new Rect((monitor.Rect.Left - bounds.Left) * ImageScale, (monitor.Rect.Top - bounds.Top) * ImageScale, blurred.PixelWidth, blurred.PixelHeight));
			}
		}
		var result = new RenderTargetBitmap(Scaled(bounds.Width), Scaled(bounds.Height), 96, 96, PixelFormats.Pbgra32);
		result.Render(canvas);
		result.Freeze();
		return (result, bounds);
	}

	/// <summary>
	/// 一个显示器上的壁纸：先按契合度画出来，再模糊；四周按镜像外延一圈再模糊，免得靠近屏幕边缘的地方发暗。
	/// </summary>
	static BitmapSource RenderMonitor(MonitorWallpaper monitor, WallpaperImage? image)
	{
		int width = Scaled(monitor.Rect.Width);
		int height = Scaled(monitor.Rect.Height);
		var area = new Rect(0, 0, width, height);
		var sharp = new DrawingVisual();
		using (var dc = sharp.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(monitor.Background), null, area);
			if (image != null)
			{
				dc.PushClip(new RectangleGeometry(area));
				if (monitor.Position == WallpaperPosition.Tile)
				{
					var tiles = new ImageBrush(image.Bitmap)
					{
						TileMode = TileMode.Tile,
						ViewportUnits = BrushMappingMode.Absolute,
						Viewport = new Rect(0, 0, image.Width * ImageScale, image.Height * ImageScale),
					};
					dc.DrawRectangle(tiles, null, area);
				}
				else
				{
					// 跨屏时一张图铺满所有显示器合起来的范围，这里只画落在本显示器上的部分
					var frame = monitor.Position == WallpaperPosition.Span
							? new Rect((monitor.Span.Left - monitor.Rect.Left) * ImageScale, (monitor.Span.Top - monitor.Rect.Top) * ImageScale, Scaled(monitor.Span.Width), Scaled(monitor.Span.Height))
							: area;
					dc.DrawImage(image.Bitmap, Place(image, frame, monitor.Position));
				}
				dc.Pop();
			}
		}
		var sharpBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		sharpBitmap.Render(sharp);
		var blurred = new DrawingVisual { Effect = new BlurEffect { Radius = BlurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
		using (var dc = blurred.RenderOpen())
		{
			var mirror = new ImageBrush(sharpBitmap) { TileMode = TileMode.FlipXY, ViewportUnits = BrushMappingMode.Absolute, Viewport = area };
			double pad = BlurRadius * 3;
			dc.DrawRectangle(mirror, null, new Rect(-pad, -pad, width + pad * 2, height + pad * 2));
		}
		var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		result.Render(blurred);
		result.Freeze();
		return result;
	}

	/// <summary>
	/// 壁纸在画框中的位置和大小，与系统「选择契合度」一致；居中按原尺寸，其余按画框缩放。
	/// 填充、跨区时图片比画框高的，系统上面裁掉多出部分的三分之一、下面裁三分之二，不是上下居中；比画框宽的左右居中（实测，
	/// 用偏高、偏宽、需要放大的几种测试图设成壁纸后量屏幕上的位置）。按居中算的话，毛玻璃比后面的壁纸往上偏。
	/// </summary>
	static Rect Place(WallpaperImage image, Rect frame, WallpaperPosition position)
	{
		double fit = Math.Min(frame.Width / image.Width, frame.Height / image.Height);
		double fill = Math.Max(frame.Width / image.Width, frame.Height / image.Height);
		var (width, height) = position switch
		{
			WallpaperPosition.Center => (image.Width * ImageScale, image.Height * ImageScale),
			WallpaperPosition.Stretch or WallpaperPosition.Tile => (frame.Width, frame.Height),
			WallpaperPosition.Fit => (image.Width * fit, image.Height * fit),
			WallpaperPosition.Fill or WallpaperPosition.Span => (image.Width * fill, image.Height * fill),
		};
		bool topThird = position is WallpaperPosition.Fill or WallpaperPosition.Span && height > frame.Height;
		double top = frame.Y + (frame.Height - height) / (topThird ? 3 : 2);
		return new Rect(frame.X + (frame.Width - width) / 2, top, width, height);
	}

	static int Scaled(int pixels) => Math.Max(1, (int)Math.Round(pixels * ImageScale));

	static RECT Union(RECT a, RECT b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

	sealed record WallpaperImage(BitmapSource Bitmap, int Width, int Height);

	/// <summary>
	/// 一个显示器的壁纸设置；Span 是所有显示器合起来的范围（跨屏契合度用）。
	/// </summary>
	sealed record MonitorWallpaper(RECT Rect, RECT Span, string Path, WallpaperPosition Position, Color Background)
	{
		public string Signature { get; } = $"{Rect.Left},{Rect.Top},{Rect.Right},{Rect.Bottom}:{Position}:{Background}:{Path}:"
				+ (Path.Length > 0 ? File.GetLastWriteTimeUtc(Path).Ticks : 0);
	}

	/// <summary>
	/// 壁纸契合度，数值与 DESKTOP_WALLPAPER_POSITION 一致。
	/// </summary>
	enum WallpaperPosition
	{
		Center,
		Tile,
		Stretch,
		Fit,
		Fill,
		Span,
	}

	[ComImport]
	[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IDesktopWallpaper
	{
		void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

		[return: MarshalAs(UnmanagedType.LPWStr)]
		string? GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

		[return: MarshalAs(UnmanagedType.LPWStr)]
		string GetMonitorDevicePathAt(uint monitorIndex);

		uint GetMonitorDevicePathCount();

		RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

		void SetBackgroundColor(uint color);

		uint GetBackgroundColor();

		void SetPosition(WallpaperPosition position);

		WallpaperPosition GetPosition();
	}

	[ComImport]
	[Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
	class DesktopWallpaperClass
	{
	}
}
