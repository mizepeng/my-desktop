using System.Runtime.InteropServices;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 毛玻璃改用系统亚克力时，垫在分区下面显示亚克力的原生窗口：鼠标穿透、不抢焦点、不画任何内容。
/// 亚克力铺满整个窗口，窗口区域（SetWindowRgn）裁不掉它，WPF 窗口的不透明度也管不了它（实测），
/// 所以不直接加在分区上，而是放在这个窗口上：卷起展开的动画期间逐帧改它的大小（约 1 毫秒），淡入淡出时改它的整体透明度。
/// </summary>
internal sealed class AcrylicBackdrop : IDisposable
{
	const string ClassName = "MyDesktopAcrylicBackdrop";
	// 窗口过程要一直被引用，免得被回收
	static readonly WindowProc Proc = WindowProcedure;
	static bool _registered;

	readonly IntPtr _hwnd;
	byte _alpha = 255;
	int _corner = -1;

	public AcrylicBackdrop()
	{
		var instance = GetModuleHandle(null);
		if (!_registered)
		{
			// 不设背景画刷：窗口内容一直是空的，只剩系统亚克力
			var windowClass = new WNDCLASS { lpfnWndProc = Proc, hInstance = instance, lpszClassName = ClassName };
			if (RegisterClass(ref windowClass) == 0)
			{
				throw new InvalidOperationException($"注册亚克力窗口类失败（错误码 {Marshal.GetLastWin32Error()}）");
			}
			_registered = true;
		}
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), ClassName, null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建亚克力窗口失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		DesktopHost.Backdrops.Add(_hwnd);
		SetLayeredWindowAttributes(_hwnd, 0, _alpha, LWA_ALPHA);
		// DWM 给圆角窗口画的那圈边框去掉，边框由分区自己画
		int border = DWMWA_COLOR_NONE;
		DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
		// 系统亚克力自己不叠色（AccentFlags 为 0），分区的背景颜色叠在上面当作叠色；指定叠色的话不论透明度多少都几乎只剩叠色（实测）
		var policy = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND };
		int size = Marshal.SizeOf<ACCENT_POLICY>();
		IntPtr data = Marshal.AllocHGlobal(size);
		try
		{
			Marshal.StructureToPtr(policy, data, false);
			var attribute = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = data, SizeOfData = size };
			if (SetWindowCompositionAttribute(_hwnd, ref attribute) == 0)
			{
				Log.Warn("设置系统亚克力失败");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(data);
		}
	}

	static IntPtr WindowProcedure(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		if (msg == WM_WINDOWPOSCHANGING)
		{
			DesktopHost.KeepBackdropAboveFloor(hwnd, lParam);
		}
		return DefWindowProc(hwnd, msg, wParam, lParam);
	}

	/// <summary>
	/// DWM 窗口圆角（DWMWCP_*）：亚克力只按它裁剪。
	/// </summary>
	public void SetCorner(int corner)
	{
		if (corner == _corner)
		{
			return;
		}
		_corner = corner;
		DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
	}

	/// <summary>
	/// 显示在 rect（屏幕物理像素）处、整体透明度 alpha；层级在所有分区之下：传 HWND_BOTTOM，
	/// 由 WM_WINDOWPOSCHANGING 改写成紧贴底座之上（DesktopHost.KeepBackdropAboveFloor），直接传插在哪个窗口之后不可靠（见 DesktopHost.SetZOrder）。
	/// </summary>
	public void Show(RECT rect, byte alpha)
	{
		if (alpha != _alpha)
		{
			_alpha = alpha;
			SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
		}
		// 调整层级的过程中（比如这一批正把亚克力窗口和分区一起摆好），分区收到 WM_WINDOWPOSCHANGED 就会来这里：只改位置，
		// 否则它会按那一批的要求再插一次，跑到刚摆好的分区上面
		uint zOrder = DesktopHost.IsAdjustingZOrder ? SWP_NOZORDER : 0;
		SetWindowPos(_hwnd, HWND_BOTTOM, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | zOrder);
	}

	public IntPtr Handle => _hwnd;

	public void Hide() => SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);

	public void SetCloaked(bool cloaked)
	{
		int value = cloaked ? 1 : 0;
		DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref value, sizeof(int));
	}

	public void Dispose()
	{
		DesktopHost.Backdrops.Remove(_hwnd);
		DestroyWindow(_hwnd);
	}
}
