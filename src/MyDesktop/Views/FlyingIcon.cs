using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDesktop.Core;
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
	/// 帧时钟线程和界面线程共用的状态（_nextFrame、_completionPosted、_frameClockRunning、_flights）以及合成画面用的共用位图都由它保护。
	/// </summary>
	static readonly object FrameLock = new();

	/// <summary>
	/// 等下一帧的都共用这一个任务，为 null 表示还没人等。
	/// </summary>
	static TaskCompletionSource? _nextFrame;

	/// <summary>
	/// 已经排给界面线程去完成 _nextFrame，还没执行：界面线程忙时不重复排。
	/// </summary>
	static bool _completionPosted;

	/// <summary>
	/// 帧时钟线程在运行：有人等帧或有图标在飞时启动，都没有了就退出。
	/// </summary>
	static bool _frameClockRunning;

	/// <summary>
	/// 正在飞的图标：每帧在帧时钟线程上一次摆完，见 AdvanceFlights。
	/// </summary>
	static readonly List<Flight> _flights = [];

	/// <summary>
	/// 一次飞行：起止范围、淡入淡出、起飞时刻（Stopwatch 时间戳），飞完时完成 Done。
	/// </summary>
	sealed record Flight(FlyingIcon Icon, RECT From, RECT To, bool FadeIn, bool FadeOut, long Start, TaskCompletionSource Done);

	/// <summary>
	/// 合成画面用的 GDI 位图（_frameWidth × _frameHeight），所有飞行图标共用（在 FrameLock 里用），UpdateLayeredWindow 会把像素拷进窗口，
	/// 用完就能给下一个。最后一个飞行图标关闭时释放。
	/// </summary>
	static IntPtr _frameDc;
	static IntPtr _frameBitmap;
	static IntPtr _frameBits;
	static int _frameWidth;
	static int _frameHeight;

	/// <summary>
	/// 缩放画面用的中间结果和一行输出，所有飞行图标共用，只增不减，最后一个飞行图标关闭时释放：每帧现分配的话 GC 太频繁。
	/// </summary>
	static Vector4[] _scaleBuffer = [];
	static int[] _rowBuffer = [];
	static int _openCount;
	// 还开着的飞行图标窗口：桌面被提到最前时要一起压回分区之上（只在界面线程上读写）
	static readonly List<IntPtr> _openWindows = [];

	readonly IntPtr _hwnd;
	readonly IntPtr _insertAfter;
	readonly ImageSource _icon;
	bool _placed;
	bool _closed;

	/// <summary>
	/// 预先合成好的整幅画面（阴影衬着图标，边长 _spriteSize，预乘透明度的 BGRA，每个通道 0～255），每帧缩放成窗口大小；为 null 表示还没画。
	/// </summary>
	Vector4[]? _sprite;
	int _spriteSize;

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
		// 内容全由 UpdateLayeredWindow 提供；窗口类见 DesktopHost.LayeredWindowClass（层级要在 WM_WINDOWPOSCHANGING 里定）
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), DesktopHost.LayeredWindowClass(), null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建飞行图标窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		_openCount++;
		_openWindows.Add(_hwnd);
	}

	/// <summary>
	/// 还开着的飞行图标窗口。
	/// </summary>
	public static IReadOnlyList<IntPtr> OpenWindows => _openWindows;

	/// <summary>
	/// 把所有飞行图标重新排到 insertAfter 之后（见 FenceManager.FlightInsertAfter）：它们只在起飞时定一次层级，
	/// 桌面被提到最前（刚装完点「完成」、Win+D）后会被盖住，分区放回桌面之上时它们也要跟着回来。
	/// </summary>
	public static void Restack(IntPtr insertAfter) => DesktopHost.PlaceAfter([.. _openWindows], insertAfter);

	/// <summary>
	/// 让图像正好落在屏幕上的 image 范围（物理像素），窗口四周按图像大小的比例多出阴影的边距。
	/// 第一次摆放要在界面线程上（要用 WPF 画阴影、定层级），之后飞行中的摆放在帧时钟线程上。
	/// </summary>
	public void Place(RECT image, double opacity)
	{
		// 第一次摆放时按起点和图标本身里大的那个尺寸画阴影（启动时从桌面飞进小图标的分区，起点比图标大）；
		// 只用到自己的字段，在锁外面画，不耽误帧时钟线程摆别的图标
		if (_sprite == null)
		{
			int iconPixels = _icon is BitmapSource bitmap ? bitmap.PixelWidth : (int)Math.Round(_icon.Width);
			int size = Math.Max(image.Width, iconPixels);
			var shadow = DrawShadow(size, GetDpiForRect(image) / 96.0, out int pad);
			_padRatio = (double)pad / size;
			_spriteSize = size + pad * 2;
			_sprite = DrawSprite(shadow, pad, size);
		}
		lock (FrameLock)
		{
			PlaceCore(image, opacity);
		}
	}

	void PlaceCore(RECT image, double opacity)
	{
		int padX = (int)Math.Round(image.Width * _padRatio);
		int padY = (int)Math.Round(image.Height * _padRatio);
		int width = image.Width + padX * 2;
		int height = image.Height + padY * 2;
		var destination = new POINT(image.Left - padX, image.Top - padY);
		var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(opacity * 255), AlphaFormat = AC_SRC_ALPHA };
		bool recompose = width != _width || height != _height;
		long placeStart = Stopwatch.GetTimestamp();
		FrameDiagnostics.Place(recompose);
		if (width == _width && height == _height)
		{
			long t0 = Stopwatch.GetTimestamp();
			UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
			FrameDiagnostics.Part(t0, true);
		}
		else
		{
			long t0 = Stopwatch.GetTimestamp();
			Compose(width, height);
			FrameDiagnostics.Part(t0, false);
			var size = new SIZE(width, height);
			var source = new POINT(0, 0);
			t0 = Stopwatch.GetTimestamp();
			UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref size, _frameDc, ref source, 0, ref blend, ULW_ALPHA);
			FrameDiagnostics.Part(t0, true);
			(_width, _height) = (width, height);
		}
		FrameDiagnostics.PlaceEnd(placeStart, recompose);
		// 第一次摆放时定好层级
		if (!_placed)
		{
			_placed = true;
			// 直接传插在哪个窗口之后不可靠：紧挨在分区上面的是以管理员身份运行的程序的窗口时被拒绝访问，
			// 飞行图标留在刚创建时的最上层，盖在别的程序上面（用户反馈：装完没切回桌面也看到了飞入动画）；见 DesktopHost.SetZOrder
			DesktopHost.PlaceAfter([_hwnd], _insertAfter);
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
		_openWindows.Remove(_hwnd);
		DestroyWindow(_hwnd);
		// 一个都不开着时也就没有在飞的了，帧时钟线程不会再用共用位图；加锁只是保证它这一帧已经用完
		if (--_openCount == 0)
		{
			lock (FrameLock)
			{
				ReleaseFrame();
			}
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
	/// 把阴影和图标合成为一整幅画面（边长 size + pad * 2），图标用 WPF 高质量缩放画在中间。只在第一次摆放时画一次，之后每帧只缩放它。
	/// </summary>
	Vector4[] DrawSprite(BitmapSource shadow, int pad, int size)
	{
		int side = size + pad * 2;
		var drawing = new DrawingGroup();
		// 高质量缩放要设在 DrawingGroup 上：设在 DrawingVisual 上时画进离屏位图不起作用（实测和双线性一模一样）
		RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.HighQuality);
		using (var context = drawing.Open())
		{
			context.DrawImage(shadow, new Rect(0, 0, side, side));
			context.DrawImage(_icon, new Rect(pad, pad, size, size));
		}
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			context.DrawDrawing(drawing);
		}
		var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
		target.Render(visual);
		var pixels = new int[side * side];
		target.CopyPixels(pixels, side * 4, 0);
		var sprite = new Vector4[pixels.Length];
		for (int i = 0; i < pixels.Length; i++)
		{
			int p = pixels[i];
			sprite[i] = new Vector4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, (p >>> 24) & 0xFF);
		}
		return sprite;
	}

	/// <summary>
	/// 把预先合成好的画面缩放成 width × height 的一帧，放进共用的 GDI 位图。
	/// 以前每帧用 WPF 的软件渲染重画阴影和图标（RenderTargetBitmap，实测每次约 0.23 毫秒），一帧里十几二十个图标都要重画时一帧就超时了；
	/// 现在只缩放：横竖两遍分开算，缩小时按覆盖面积取平均（三角形滤波，半径为缩小倍数），放大时双线性，和 WPF 的高质量缩放效果相近。
	/// </summary>
	void Compose(int width, int height)
	{
		if (width > _frameWidth || height > _frameHeight)
		{
			// 多留一些，飞行中图标变大时不用每帧重新分配
			CreateFrame(Math.Max(width, _frameWidth) + 64, Math.Max(height, _frameHeight) + 64);
		}
		int side = _spriteSize;
		var sprite = _sprite!;
		var (startX, countX, weightsX, tapsX) = ScaleWeights(side, width);
		var (startY, countY, weightsY, tapsY) = ScaleWeights(side, height);
		// 先横向：side 行 × width 列
		if (_scaleBuffer.Length < side * width)
		{
			_scaleBuffer = new Vector4[side * width];
		}
		var buffer = _scaleBuffer;
		for (int y = 0; y < side; y++)
		{
			int row = y * side;
			for (int x = 0; x < width; x++)
			{
				var sum = Vector4.Zero;
				int from = row + startX[x];
				int w = x * tapsX;
				for (int k = 0; k < countX[x]; k++)
				{
					sum += sprite[from + k] * weightsX[w + k];
				}
				buffer[y * width + x] = sum;
			}
		}
		// 再纵向，逐行写进 GDI 位图（行宽是整个共用位图的宽度）
		if (_rowBuffer.Length < width)
		{
			_rowBuffer = new int[width];
		}
		var line = _rowBuffer;
		var max = new Vector4(255);
		var half = new Vector4(0.5f);
		for (int y = 0; y < height; y++)
		{
			int w = y * tapsY;
			for (int x = 0; x < width; x++)
			{
				var sum = Vector4.Zero;
				int from = startY[y] * width + x;
				for (int k = 0; k < countY[y]; k++)
				{
					sum += buffer[from + k * width] * weightsY[w + k];
				}
				var c = Vector4.Clamp(sum + half, Vector4.Zero, max);
				line[x] = (int)c.X | ((int)c.Y << 8) | ((int)c.Z << 16) | ((int)c.W << 24);
			}
			Marshal.Copy(line, 0, _frameBits + y * _frameWidth * 4, width);
		}
	}

	/// <summary>
	/// 一个方向上从 source 个像素缩放到 target 个像素时，每个输出像素取哪些输入像素（从 Start 起 Count 个）、各占多少权重（每个输出像素占 Taps 格）。
	/// </summary>
	static (int[] Start, int[] Count, float[] Weights, int Taps) ScaleWeights(int source, int target)
	{
		double scale = (double)source / target;
		// 缩小时三角形滤波的半径就是缩小倍数，覆盖到对应的那几个输入像素；放大时半径 1，即双线性
		double radius = Math.Max(1, scale);
		int taps = (int)Math.Ceiling(radius) * 2 + 1;
		var start = new int[target];
		var count = new int[target];
		var weights = new float[target * taps];
		for (int i = 0; i < target; i++)
		{
			double center = (i + 0.5) * scale - 0.5;
			int first = Math.Max(0, (int)Math.Floor(center - radius) + 1);
			int last = Math.Min(source - 1, (int)Math.Ceiling(center + radius) - 1);
			double total = 0;
			for (int j = first; j <= last; j++)
			{
				total += Math.Max(0, 1 - Math.Abs(j - center) / radius);
			}
			start[i] = first;
			count[i] = last - first + 1;
			for (int j = first; j <= last; j++)
			{
				weights[i * taps + j - first] = (float)(Math.Max(0, 1 - Math.Abs(j - center) / radius) / total);
			}
		}
		return (start, count, weights, taps);
	}

	/// <summary>
	/// 换一个 width × height 的共用 GDI 位图（自上而下、32 位）。
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
		(_frameWidth, _frameHeight) = (width, height);
	}

	static void ReleaseFrame()
	{
		(_frameWidth, _frameHeight) = (0, 0);
		_scaleBuffer = [];
		_rowBuffer = [];
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
	/// 不在界面线程上逐帧 await：每个图标的续体各自排队，一帧里的图标前后隔开好几毫秒才摆完，常常跨过一次合成，
	/// 而且各按自己被轮到的时刻取位置，节奏忽快忽慢（实测启动 59 个图标：一帧的摆放首尾隔 p50 6.8 毫秒，八成的帧跨过了合成）；
	/// 界面线程被建分区、排版、重画占住时所有图标一起停住。所以登记下来，由帧时钟线程每帧按同一个时刻一次摆完（AdvanceFlights）。
	/// 只能在界面线程上调用，起飞前要先摆好（Place）。
	/// </summary>
	public Task FlyAsync(RECT from, RECT to, bool fadeIn, bool fadeOut)
	{
		if (!_placed)
		{
			throw new InvalidOperationException("飞行图标起飞前要先在界面线程上摆好");
		}
		// 飞完后的续体（关窗口、显示落地的图标）回到界面线程上排队执行，不在帧时钟线程上跑
		var flight = new Flight(this, from, to, fadeIn, fadeOut, Stopwatch.GetTimestamp(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
		lock (FrameLock)
		{
			_flights.Add(flight);
			StartFrameClock();
		}
		return flight.Done.Task;
	}

	/// <summary>
	/// 在帧时钟线程上把每个正在飞的图标摆到 frameTime（这一帧 DWM 合成的时刻）对应的位置；飞完的不再摆，完成它的任务。
	/// 按同一个时刻取位置，相邻两帧之间所有图标都正好推进一帧的时长，不受各自被轮到早晚的影响。在 FrameLock 里调用。
	/// UpdateLayeredWindow 不用窗口所在的线程配合（实测界面线程阻塞着也照常更新），所以界面线程忙时动画照样走。
	/// </summary>
	static void AdvanceFlights(long frameTime)
	{
		for (int i = 0; i < _flights.Count;)
		{
			var flight = _flights[i];
			double u = Math.Max(0, Stopwatch.GetElapsedTime(flight.Start, frameTime) / FlightDuration);
			if (u >= 1)
			{
				_flights.RemoveAt(i);
				flight.Done.SetResult();
				continue;
			}
			try
			{
				double opacity = (flight.FadeIn ? Math.Clamp(u / 0.3, 0, 1) : 1) * (flight.FadeOut ? 1 - Math.Clamp((u - 0.6) / 0.4, 0, 1) : 1);
				flight.Icon.PlaceCore(Along(flight.From, flight.To, u), opacity);
				i++;
			}
			catch (Exception ex)
			{
				_flights.RemoveAt(i);
				flight.Done.SetException(ex);
			}
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
		lock (FrameLock)
		{
			_nextFrame ??= new TaskCompletionSource();
			StartFrameClock();
			return _nextFrame.Task;
		}
	}

	/// <summary>
	/// 帧时钟线程没在运行时启动它。在界面线程上、FrameLock 里调用。
	/// </summary>
	static void StartFrameClock()
	{
		if (!_frameClockRunning)
		{
			_frameClockRunning = true;
			var dispatcher = Dispatcher.CurrentDispatcher;
			FrameDiagnostics.Begin(dispatcher);
			new Thread(() => RunFrameClock(dispatcher)) { IsBackground = true, Name = "FrameClock" }.Start();
		}
	}

	/// <summary>
	/// 帧时钟线程：每次 DWM 合成之后先摆好正在飞的图标，再让界面线程完成等这一帧的任务；既没有飞行也没人等时退出。
	/// 等帧的任务排到界面线程上完成、优先级低于重绘，排着还没执行时不再重复排：很多图标一起飞、界面线程忙不过来时不会接着塞新的一帧，
	/// 重绘和输入照样轮得到（实测 50 个图标错开起飞时，排在重绘优先级的操作最多等约 20 毫秒；在后台线程上直接完成的话要等近 2 秒）。
	/// </summary>
	static void RunFrameClock(Dispatcher dispatcher)
	{
		long lastFrame = Stopwatch.GetTimestamp();
		while (true)
		{
			WaitForComposition(false);
			int flushes = 1;
			while (Stopwatch.GetElapsedTime(lastFrame) < MinFrameInterval)
			{
				WaitForComposition(true);
				flushes++;
			}
			lastFrame = Stopwatch.GetTimestamp();
			lock (FrameLock)
			{
				// 程序退出、调度器关闭后不再排任务（排了也不会执行）
				if (dispatcher.HasShutdownStarted || (_flights.Count == 0 && _nextFrame == null && !_completionPosted))
				{
					_frameClockRunning = false;
					FrameDiagnostics.End();
					return;
				}
				long start = Stopwatch.GetTimestamp();
				FrameDiagnostics.FrameStart(lastFrame, flushes, start);
				AdvanceFlights(lastFrame);
				FrameDiagnostics.FrameEnd(start);
				if (_nextFrame != null && !_completionPosted)
				{
					_completionPosted = true;
					dispatcher.BeginInvoke(DispatcherPriority.Input, CompleteFrame);
				}
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
		int result = DwmFlush();
		FrameDiagnostics.Flushed();
		if (result < 0 || (justReturned && Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(1)))
		{
			Thread.Sleep(MinFrameInterval);
		}
	}

	/// <summary>
	/// 在界面线程上完成等这一帧的任务（续体就地接着执行）。
	/// </summary>
	static void CompleteFrame()
	{
		TaskCompletionSource? frame;
		lock (FrameLock)
		{
			frame = _nextFrame;
			_nextFrame = null;
			_completionPosted = false;
		}
		frame?.SetResult();
	}
}
