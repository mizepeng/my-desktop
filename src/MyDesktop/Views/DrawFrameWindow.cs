using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 在桌面上右键拖动画框时显示的选框：鼠标穿透、不抢焦点。
/// </summary>
internal sealed class DrawFrameWindow : Window
{
	IntPtr _hwnd;

	public DrawFrameWindow()
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		ShowActivated = false;
		Topmost = true;
		Content = new Border
		{
			BorderThickness = new Thickness(2),
			CornerRadius = new CornerRadius(6),
			BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x60, 0xA5, 0xFA)),
			Background = new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6)),
		};
	}

	/// <summary>
	/// 在屏幕物理像素矩形处显示选框。
	/// </summary>
	public void ShowAt(RECT rect)
	{
		if (_hwnd == IntPtr.Zero)
		{
			_hwnd = new WindowInteropHelper(this).EnsureHandle();
			long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
			SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
		}
		SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
		if (!IsVisible)
		{
			Show();
		}
	}
}
