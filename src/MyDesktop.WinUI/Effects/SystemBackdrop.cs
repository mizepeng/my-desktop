using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Core;
using MyDesktop.Native;
using WinRT;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Effects;

internal sealed class TransparentBackdrop(nint hwnd) : SystemBackdrop
{
	Windows.UI.Composition.Compositor? _compositor;

	protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
	{
		ConfigureWindow(hwnd);
		Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
		_compositor ??= new Windows.UI.Composition.Compositor();
		target.SystemBackdrop = _compositor.CreateColorBrush(Colors.Transparent);
		var dc = GetDC(hwnd);
		if (dc == 0)
		{
			throw new Win32Exception();
		}
		try
		{
			ClearBackground(hwnd, dc);
		}
		finally
		{
			ReleaseDC(hwnd, dc);
		}
		Log.Info("宿主已配置 DWM alpha 合成和原生背景清除。");
	}

	protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
	{
		target.SystemBackdrop = null;
		_compositor?.Dispose();
		_compositor = null;
	}

	public static void ConfigureWindow(nint hwnd)
	{
		var margins = default(FrameMargins);
		Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
		var region = CreateRectRgn(-2, -2, -1, -1);
		if (region == 0)
		{
			throw new Win32Exception();
		}
		try
		{
			// 让 DWM 保留原生底面的 alpha；实际亚克力仍由分区的 SystemBackdropElement 提供。
			var blur = new BlurBehind { Flags = 3, Enabled = 1, Region = region };
			Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(hwnd, ref blur));
		}
		finally
		{
			DeleteObject(region);
		}
	}

	public static nint ClearBackground(nint hwnd, nint dc)
	{
		int visible = GetClipBox(dc, out _);
		if (visible == 0)
		{
			throw new Win32Exception();
		}
		// 未显示或被完全遮挡时，DC 的可见区域可以为空，没有需要清除的像素。
		if (visible == 1)
		{
			return 1;
		}
		if (!GetClientRect(hwnd, out var rect))
		{
			throw new Win32Exception();
		}
		if (FillRect(dc, ref rect, GetStockObject(4)) == 0)
		{
			throw new InvalidOperationException($"清除原生窗口底面失败，客户区={rect.Width}x{rect.Height}。");
		}
		return 1;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct FrameMargins
	{
		public int Left, Right, Top, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct BlurBehind
	{
		public uint Flags;
		public int Enabled;
		public nint Region;
		public int TransitionOnMaximized;
	}

	[DllImport("dwmapi.dll")]
	static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref FrameMargins margins);
	[DllImport("dwmapi.dll")]
	static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
	[DllImport("gdi32.dll", SetLastError = true)]
	static extern nint CreateRectRgn(int left, int top, int right, int bottom);
	[DllImport("gdi32.dll")]
	static extern nint GetStockObject(int index);
	[DllImport("gdi32.dll", SetLastError = true)]
	static extern int GetClipBox(nint dc, out RECT rect);
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool GetClientRect(nint hwnd, out RECT rect);
	[DllImport("user32.dll", SetLastError = true)]
	static extern int FillRect(nint dc, ref RECT rect, nint brush);
	[DllImport("user32.dll", SetLastError = true)]
	static extern nint GetDC(nint hwnd);
	[DllImport("user32.dll")]
	static extern int ReleaseDC(nint hwnd, nint dc);
}

internal sealed class SystemAcrylicBackdrop : SystemBackdrop
{
	DesktopAcrylicController? _controller;
	SystemBackdropConfiguration? _configuration;

	protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
	{
		Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
		if (!DesktopAcrylicController.IsSupported())
		{
			throw new NotSupportedException("当前系统不支持 DesktopAcrylicController，不能完成系统亚克力验证。");
		}
		// 桌面宿主不激活，也需要显示材质；系统的高对比度、节电等策略仍由控制器处理。
		_configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
		_controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
		_controller.SetSystemBackdropConfiguration(_configuration);
		_controller.AddSystemBackdropTarget(target);
		Log.Info("元素级 DesktopAcrylicController 已连接，IsInputActive=true。");
	}

	protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
	{
		_controller?.RemoveSystemBackdropTarget(target);
		_controller?.Dispose();
		_controller = null;
		_configuration = null;
		// 本类只连接 SystemBackdropElement 的外部背景链接；必须在界面线程关闭，不能留给终结线程关闭 ContentSite。
		target.As<IDisposable>().Dispose();
	}
}
