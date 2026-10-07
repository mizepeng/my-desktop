using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 一键整理、删除分区动画里飞行的图标：鼠标穿透、不抢焦点的小窗口，按屏幕物理像素移动和缩放。
/// 层级和桌面同一层（紧贴在最上面的分区之上），不置顶：桌面被别的程序挡住时动画也被挡住。
/// 每个图标一个小窗口，比铺满屏幕的透明窗口省得多（透明窗口每帧都要整块重画）。
/// 和选框（DrawFrameWindow）一样是原生分层窗口，每帧在 CPU 上合成好画面交给 UpdateLayeredWindow，不受硬件加速开关影响：
/// WPF 的透明窗口每次改大小都要同步重画（实测每帧 1.2～1.5 毫秒），50 个图标错开起飞时界面线程忙不过来，每个图标只轮得到一半的帧；
/// 这样每帧约 0.5 毫秒（实测进程 CPU 2.7 → 约 1.6 秒，每个图标的帧全部跑满），也不占显卡驱动的内存。
/// </summary>
internal sealed class FlyingIcon
{
	/// <summary>
	/// 图像四周留给阴影的边距（DIP），按画阴影时的图标大小算，图标变大变小时按比例缩放。
	/// </summary>
	const double ShadowPad = 10;

	/// <summary>
	/// 每个图标飞行的时长。
	/// </summary>
	public static readonly TimeSpan FlightDuration = TimeSpan.FromMilliseconds(520);

	/// <summary>
	/// 同一批图标错开起飞的间隔。
	/// </summary>
	public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(40);

	/// <summary>
	/// 两帧之间至少隔这么久：240 Hz 等高刷新率的屏幕上隔一次合成才推进一帧，约 120 次/秒封顶；165 Hz 及以下每次合成都推进。
	/// 飞行图标每帧都要重新合成画面、更新窗口，帧数减半，开销也跟着减半。
	/// </summary>
	static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(5);

	/// <summary>
	/// 等下一帧的都共用这一个任务，为 null 表示还没人等；和 _frameClockRunning 一样只在界面线程上读写。
	/// </summary>
	static TaskCompletionSource? _nextFrame;

	/// <summary>
	/// 等帧的后台线程在运行：有人等时启动，一帧下来没人等就退出。
	/// </summary>
	static bool _frameClockRunning;

	/// <summary>
	/// 合成画面用的离屏位图和 GDI 位图，所有飞行图标共用：都在界面线程上，UpdateLayeredWindow 会把像素拷进窗口，用完就能给下一个。
	/// 最后一个飞行图标关闭时释放。
	/// </summary>
	static RenderTargetBitmap? _frameTarget;
	static readonly DrawingGroup FrameDrawing = new();
	static readonly DrawingVisual FrameVisual = new();
	static IntPtr _frameDc;
	static IntPtr _frameBitmap;
	static IntPtr _frameBits;
	static int _openCount;

	readonly IntPtr _hwnd;
	readonly IntPtr _insertAfter;
	readonly ImageSource _icon;
	bool _placed;
	bool _closed;

	/// <summary>
	/// 预先画好的阴影，合成时拉满整个窗口衬在图标下面。
	/// </summary>
	BitmapSource? _shadow;

	/// <summary>
	/// 阴影图里阴影的边距与图标边长之比：窗口四周按这个比例留边，阴影跟着图标一起缩放。
	/// </summary>
	double _padRatio;

	/// <summary>
	/// 窗口现在的大小（物理像素），0 表示还没画过：大小不变时画面也不变，只挪位置、改透明度，不用重新合成。
	/// </summary>
	int _width;
	int _height;

	/// <param name="insertAfter">层级上排在这个窗口之后，见 FenceManager.FlightInsertAfter。</param>
	public FlyingIcon(ImageSource icon, IntPtr insertAfter)
	{
		_icon = icon;
		_insertAfter = insertAfter;
		// 同 DrawFrameWindow：借用系统自带的 Static 窗口类，内容全由 UpdateLayeredWindow 提供
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), "Static", null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建飞行图标窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		_openCount++;
	}

	/// <summary>
	/// 让图像正好落在屏幕上的 image 范围（物理像素），窗口四周按图像大小的比例多出阴影的边距。
	/// </summary>
	public void Place(RECT image, double opacity)
	{
		// 第一次摆放时按起点和图标本身里大的那个尺寸画阴影（启动时从桌面飞进小图标的分区，起点比图标大）
		if (_shadow == null)
		{
			int iconPixels = _icon is BitmapSource bitmap ? bitmap.PixelWidth : (int)Math.Round(_icon.Width);
			int size = Math.Max(image.Width, iconPixels);
			_shadow = DrawShadow(size, GetDpiForRect(image) / 96.0, out int pad);
			_padRatio = (double)pad / size;
		}
		int padX = (int)Math.Round(image.Width * _padRatio);
		int padY = (int)Math.Round(image.Height * _padRatio);
		int width = image.Width + padX * 2;
		int height = image.Height + padY * 2;
		var destination = new POINT(image.Left - padX, image.Top - padY);
		var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(opacity * 255), AlphaFormat = AC_SRC_ALPHA };
		if (width == _width && height == _height)
		{
			UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
		}
		else
		{
			Compose(_shadow, width, height, padX, padY, image.Width, image.Height);
			var size = new SIZE(width, height);
			var source = new POINT(0, 0);
			UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref size, _frameDc, ref source, 0, ref blend, ULW_ALPHA);
			(_width, _height) = (width, height);
		}
		// 第一次摆放时定好层级
		if (!_placed)
		{
			_placed = true;
			SetWindowPos(_hwnd, _insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
		}
	}

	public void Show()
	{
		SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
	}

	/// <summary>
	/// 关掉窗口；最后一个飞行图标关闭时释放共用的位图。
	/// </summary>
	public void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		DestroyWindow(_hwnd);
		if (--_openCount == 0)
		{
			ReleaseFrame();
		}
	}

	/// <summary>
	/// 把阴影预先画成一张图（图标边长 size 像素，四周留 pad 像素的边距），合成每帧时跟着图标一起缩放，不用每帧现算模糊（画一次约 0.7 毫秒，
	/// 比合成一帧还贵）。阴影参数和原来的 DropShadowEffect 一样按屏幕缩放比例换成像素，图标正好是 size 大小时和原来几乎一样（实测每个通道最多差 3/255）。
	/// </summary>
	BitmapSource DrawShadow(int size, double scale, out int pad)
	{
		pad = (int)Math.Round(ShadowPad * scale);
		// 图标的黑色剪影，模糊、下移、半透明，同原来的 DropShadowEffect（BlurRadius 12、ShadowDepth 4、Direction 270、Opacity 0.45）
		var silhouette = new Rectangle
		{
			Width = size,
			Height = size,
			Fill = Brushes.Black,
			OpacityMask = new ImageBrush(_icon) { Stretch = Stretch.Uniform },
			Opacity = 0.45,
			Effect = new BlurEffect { Radius = 12 * scale, KernelType = KernelType.Gaussian },
			RenderTransform = new TranslateTransform(0, 4 * scale),
		};
		var host = new Grid { Width = size + pad * 2, Height = size + pad * 2, Children = { silhouette } };
		host.Measure(new Size(host.Width, host.Height));
		host.Arrange(new Rect(0, 0, host.Width, host.Height));
		var shadow = new RenderTargetBitmap(size + pad * 2, size + pad * 2, 96, 96, PixelFormats.Pbgra32);
		shadow.Render(host);
		shadow.Freeze();
		return shadow;
	}

	/// <summary>
	/// 在 CPU 上把阴影和图标合成为 width × height 的一帧，放进共用的 GDI 位图：用 WPF 的软件渲染，图标高质量缩放，和原来的样子一致。
	/// </summary>
	void Compose(BitmapSource shadow, int width, int height, int padX, int padY, int imageWidth, int imageHeight)
	{
		if (_frameTarget == null || width > _frameTarget.PixelWidth || height > _frameTarget.PixelHeight)
		{
			// 多留一些，飞行中图标变大时不用每帧重新分配
			CreateFrame(Math.Max(width, _frameTarget?.PixelWidth ?? 0) + 64, Math.Max(height, _frameTarget?.PixelHeight ?? 0) + 64);
		}
		using (var context = FrameDrawing.Open())
		{
			context.DrawImage(shadow, new Rect(0, 0, width, height));
			context.DrawImage(_icon, new Rect(padX, padY, imageWidth, imageHeight));
		}
		_frameTarget!.Clear();
		_frameTarget.Render(FrameVisual);
		// 两张位图一样大，按 GDI 位图的行宽直接拷进去
		int stride = _frameTarget.PixelWidth * 4;
		_frameTarget.CopyPixels(new Int32Rect(0, 0, width, height), _frameBits, stride * height, stride);
	}

	/// <summary>
	/// 换一套 width × height 的共用位图：离屏位图和同样大的 GDI 位图（自上而下、32 位）。
	/// </summary>
	static void CreateFrame(int width, int height)
	{
		ReleaseFrame();
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				// 高度为负：自上而下，和离屏位图的行序一致
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		_frameBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out _frameBits, IntPtr.Zero, 0);
		if (_frameBitmap == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建飞行图标位图失败（{width}×{height}）");
		}
		_frameDc = CreateCompatibleDC(IntPtr.Zero);
		SelectObject(_frameDc, _frameBitmap);
		_frameTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		// 高质量缩放要设在 DrawingGroup 上：设在 DrawingVisual 上时画进离屏位图不起作用（实测和双线性一模一样）
		RenderOptions.SetBitmapScalingMode(FrameDrawing, BitmapScalingMode.HighQuality);
		using (var context = FrameVisual.RenderOpen())
		{
			context.DrawDrawing(FrameDrawing);
		}
	}

	static void ReleaseFrame()
	{
		_frameTarget = null;
		// 不再引用最后一个图标的位图
		FrameDrawing.Children.Clear();
		// 先删 DC，选在里面的位图才删得掉
		if (_frameDc != IntPtr.Zero)
		{
			DeleteDC(_frameDc);
			_frameDc = IntPtr.Zero;
		}
		if (_frameBitmap != IntPtr.Zero)
		{
			DeleteObject(_frameBitmap);
			_frameBitmap = IntPtr.Zero;
			_frameBits = IntPtr.Zero;
		}
	}

	/// <summary>
	/// 从 from 飞到 to（图像范围，物理像素）：沿向上拱起的弧线、先快后慢，中途略微放大。
	/// fadeIn：起点看不到这个图标（分区里要滚动才看得到、分区卷起了），开头淡入；fadeOut：终点看不到，末尾淡出。
	/// </summary>
	public async Task FlyAsync(RECT from, RECT to, bool fadeIn, bool fadeOut)
	{
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < FlightDuration)
		{
			double u = clock.Elapsed / FlightDuration;
			double opacity = (fadeIn ? Math.Clamp(u / 0.3, 0, 1) : 1) * (fadeOut ? 1 - Math.Clamp((u - 0.6) / 0.4, 0, 1) : 1);
			Place(Along(from, to, u), opacity);
			await NextFrame();
		}
	}

	/// <summary>
	/// 飞行进度 u（0～1）时图像所在的范围：先快后慢地从 from 移到 to，路径向上拱起，中途放大一点。
	/// </summary>
	static RECT Along(RECT from, RECT to, double u)
	{
		double eased = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
		double arc = Math.Sin(Math.PI * u);
		double fromX = (from.Left + from.Right) / 2.0;
		double fromY = (from.Top + from.Bottom) / 2.0;
		double toX = (to.Left + to.Right) / 2.0;
		double toY = (to.Top + to.Bottom) / 2.0;
		double distance = Math.Sqrt((toX - fromX) * (toX - fromX) + (toY - fromY) * (toY - fromY));
		double lift = Math.Min(distance * 0.25, from.Height * 2.5) * arc;
		double x = fromX + (toX - fromX) * eased;
		double y = fromY + (toY - fromY) * eased - lift;
		double width = (from.Width + (to.Width - from.Width) * eased) * (1 + 0.15 * arc);
		double height = (from.Height + (to.Height - from.Height) * eased) * (1 + 0.15 * arc);
		return new RECT((int)Math.Round(x - width / 2), (int)Math.Round(y - height / 2), (int)Math.Round(x + width / 2), (int)Math.Round(y + height / 2));
	}

	public static RECT CenteredIn(RECT area, int width, int height)
	{
		int left = (area.Left + area.Right - width) / 2;
		int top = (area.Top + area.Bottom - height) / 2;
		return new RECT(left, top, left + width, top + height);
	}

	/// <summary>
	/// 等到下一帧：DWM 下一次合成之后，在界面线程上排在重绘之后接着执行；两帧至少隔 MinFrameInterval。只能在界面线程上调用。
	/// 不用 CompositionTarget.Rendering：它不按屏幕刷新节奏触发，在回调里接着订阅会连着触发，飞行动画的循环会空转。
	/// </summary>
	public static Task NextFrame()
	{
		_nextFrame ??= new TaskCompletionSource();
		if (!_frameClockRunning)
		{
			_frameClockRunning = true;
			var dispatcher = Dispatcher.CurrentDispatcher;
			new Thread(() => RunFrameClock(dispatcher)) { IsBackground = true, Name = "FrameClock" }.Start();
		}
		return _nextFrame.Task;
	}

	/// <summary>
	/// 等帧的后台线程：每次 DWM 合成之后到界面线程上完成等这一帧的任务，一帧下来没人等就退出。
	/// 在界面线程上同步完成、优先级低于重绘：很多图标一起飞、界面线程忙不过来时不会接着塞新的一帧，重绘和输入照样轮得到
	/// （实测 50 个图标错开起飞时，排在重绘优先级的操作最多等约 20 毫秒；在后台线程上直接完成的话要等近 2 秒）。
	/// </summary>
	static void RunFrameClock(Dispatcher dispatcher)
	{
		long lastFrame = Stopwatch.GetTimestamp();
		while (true)
		{
			WaitForComposition(false);
			while (Stopwatch.GetElapsedTime(lastFrame) < MinFrameInterval)
			{
				WaitForComposition(true);
			}
			lastFrame = Stopwatch.GetTimestamp();
			// 用这个重载：程序退出、调度器关闭后它返回 null；泛型的 Invoke<T> 会抛 TaskCanceledException，后台线程上没人接住，进程就崩了（实测）
			if (dispatcher.Invoke(DispatcherPriority.Input, new Func<bool>(CompleteFrame)) is not true)
			{
				return;
			}
		}
	}

	/// <summary>
	/// 等到 DWM 下一次合成之后。DwmFlush 失败（合成暂停等），或者上一次刚返回、紧接着再调用却没等就返回（DWM 没在合成）时，
	/// 按时间等，免得空转；隔了一阵才调用时离下一次合成本来就可能很近，很快返回是正常的。
	/// </summary>
	static void WaitForComposition(bool justReturned)
	{
		long start = Stopwatch.GetTimestamp();
		if (DwmFlush() < 0 || (justReturned && Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(1)))
		{
			Thread.Sleep(MinFrameInterval);
		}
	}

	/// <summary>
	/// 在界面线程上完成等这一帧的任务；没人等时返回 false，让后台线程退出。
	/// </summary>
	static bool CompleteFrame()
	{
		var frame = _nextFrame;
		if (frame == null)
		{
			_frameClockRunning = false;
			return false;
		}
		_nextFrame = null;
		frame.SetResult();
		return true;
	}
}
