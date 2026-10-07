using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 一键整理、删除分区动画里飞行的图标：置顶、鼠标穿透、不抢焦点的小窗口，按屏幕物理像素移动和缩放。
/// 每个图标一个小窗口，比铺满屏幕的透明窗口省得多（透明窗口每帧都要整块重画）。
/// </summary>
internal sealed class FlyingIcon : Window
{
	/// <summary>
	/// 图像四周留给阴影的边距（DIP）。
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

	IntPtr _hwnd;

	public FlyingIcon(ImageSource icon)
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		ShowActivated = false;
		Topmost = true;
		var image = new Image
		{
			Source = icon,
			Stretch = Stretch.Uniform,
			Margin = new Thickness(ShadowPad),
			Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		Content = image;
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
		SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
	}

	/// <summary>
	/// 让图像正好落在屏幕上的 image 范围（物理像素），窗口四周多出阴影的边距。
	/// </summary>
	public void Place(RECT image, double opacity)
	{
		if (_hwnd == IntPtr.Zero)
		{
			_hwnd = new WindowInteropHelper(this).EnsureHandle();
		}
		int pad = (int)Math.Round(ShadowPad * GetDpiForWindow(_hwnd) / 96.0);
		SetWindowPos(_hwnd, HWND_TOPMOST, image.Left - pad, image.Top - pad, image.Width + pad * 2, image.Height + pad * 2, SWP_NOACTIVATE);
		Opacity = opacity;
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
	/// 等到界面下一次重绘。
	/// </summary>
	public static Task NextFrame()
	{
		var done = new TaskCompletionSource();
		EventHandler? handler = null;
		handler = (_, _) =>
		{
			CompositionTarget.Rendering -= handler;
			done.TrySetResult();
		};
		CompositionTarget.Rendering += handler;
		return done.Task;
	}
}
