using System.Runtime.InteropServices;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 速览时垫在分区和图标层下面、盖住整个虚拟屏幕的半透明黑色遮罩：把后面的程序压暗，并接住点在空白处的鼠标（结束速览）。
/// 原生窗口加分层窗口的整体透明度（SetLayeredWindowAttributes），不用 WPF 透明窗口：那样要按整屏大小占内存（见 constraints.md）。
/// 不抢焦点；只在界面线程上使用。
/// </summary>
internal sealed class PeekBackdrop : IDisposable
{
	const string ClassName = "MyDesktopPeekBackdrop";

	const int SM_XVIRTUALSCREEN = 76;
	const int SM_YVIRTUALSCREEN = 77;
	const int SM_CXVIRTUALSCREEN = 78;
	const int SM_CYVIRTUALSCREEN = 79;
	const uint WM_MBUTTONDOWN = 0x0207;
	const uint WM_MOUSEACTIVATE = 0x0021;
	const int MA_NOACTIVATE = 3;
	const int IDC_ARROW = 32512;
	const int BLACK_BRUSH = 4;

	/// <summary>
	/// 遮罩的不透明度（0～255）：后面的程序看得出来但明显压暗，分区和图标更醒目。
	/// </summary>
	const byte Alpha = 96;

	[DllImport("user32.dll")]
	static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

	[DllImport("gdi32.dll")]
	static extern IntPtr GetStockObject(int index);

	static bool _registered;

	// 窗口过程要一直被引用，免得被回收；遮罩全程只有一个，点击交给当前的实例
	static PeekBackdrop? _current;

	static readonly WindowProc Proc = (hwnd, msg, wParam, lParam) =>
	{
		if (msg == WM_MOUSEACTIVATE)
		{
			return MA_NOACTIVATE;
		}
		if (msg is (uint)WM_LBUTTONDOWN or (uint)WM_RBUTTONDOWN or WM_MBUTTONDOWN)
		{
			_current?.Clicked?.Invoke();
			return IntPtr.Zero;
		}
		return DefWindowProc(hwnd, msg, wParam, lParam);
	};

	readonly IntPtr _hwnd;

	/// <summary>
	/// 在遮罩上（也就是分区、图标以外的空白处）按下了鼠标。
	/// </summary>
	public event Action? Clicked;

	public PeekBackdrop()
	{
		if (!_registered)
		{
			var windowClass = new WNDCLASS
			{
				lpfnWndProc = Proc,
				hInstance = GetModuleHandle(null),
				hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
				hbrBackground = GetStockObject(BLACK_BRUSH),
				lpszClassName = ClassName,
			};
			if (RegisterClass(ref windowClass) == 0)
			{
				throw new InvalidOperationException($"注册速览遮罩的窗口类失败（错误码 {Marshal.GetLastWin32Error()}）");
			}
			_registered = true;
		}
		_hwnd = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST), ClassName, null,
				unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
		if (_hwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException($"创建速览遮罩失败（错误码 {Marshal.GetLastWin32Error()}）");
		}
		SetLayeredWindowAttributes(_hwnd, 0, Alpha, LWA_ALPHA);
		_current = this;
	}

	/// <summary>
	/// 铺满整个虚拟屏幕显示在置顶层最上面；之后再置顶的分区、图标层会压在它上面。
	/// </summary>
	public void Show()
	{
		int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
		int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
		int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
		int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
		SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
	}

	public void Hide() => ShowWindowAsync(_hwnd, SW_HIDE);

	public void Dispose()
	{
		DestroyWindow(_hwnd);
		if (_current == this)
		{
			_current = null;
		}
	}
}
