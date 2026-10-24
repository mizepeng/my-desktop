using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Native;
using MyDesktop.WinUI.Effects;
using MyDesktop.WinUI.Native;
using Windows.Graphics;
using WinRT.Interop;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Views;

/// <summary>
/// 同屏收起分区常驻的浮层：平时与主宿主一样紧贴桌面，悬停或拖放临时展开时整体提升到置顶层底部。
/// 展开与收回只改变本窗口层级，不在窗口之间迁移分区视图，迁移会有一到几帧两边都没画出分区。
/// </summary>
internal sealed class ExpandedSurface : Window, IDisposable
{
	readonly DesktopSurface _source;
	readonly Canvas _canvas = new() { RequestedTheme = ElementTheme.Dark };
	readonly Border _band = new()
	{
		BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 100, 180, 255)),
		Background = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 70, 150, 240)), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
	};
	readonly DesktopPlacement _placement;
	readonly WindowApi.SubclassProc _procedure;
	RECT _bandBounds;
	bool _disposed;

	internal ExpandedSurface(DesktopSurface source)
	{
		_source = source;
		Content = _canvas;
		_canvas.Children.Add(_band);
		Title = "MyDesktop 收起分区";
		Handle = WindowNative.GetWindowHandle(this);
		_procedure = WindowMessage;
		if (!WindowApi.SetWindowSubclass(Handle, _procedure, 1, 0)) { throw new Win32Exception(); }
		var presenter = (OverlappedPresenter)AppWindow.Presenter;
		presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
		presenter.SetBorderAndTitleBar(false, false);
		WindowApi.Configure(Handle);
		// 只归属于同进程的桌面宿主，不能与 Explorer 建立跨进程窗口关系。
		SetWindowLongPtr(Handle, GWLP_HWNDPARENT, source.Handle);
		var work = source.WorkArea;
		AppWindow.MoveAndResize(new RectInt32(work.Left, work.Top, work.Width, work.Height));
		SystemBackdrop = new TransparentBackdrop(Handle);
		_placement = new DesktopPlacement(Handle);
		_canvas.PointerPressed += source.Surface_PointerPressed;
		Activated += (_, args) =>
		{
			if (args.WindowActivationState == WindowActivationState.Deactivated) { DispatcherQueue.TryEnqueue(source.CancelDeactivatedEdit); }
		};
	}

	internal nint Handle { get; }
	internal bool IsBandVisible => _band.Visibility == Visibility.Visible;
	internal bool Contains(FenceView fence) => _canvas.Children.Contains(fence);
	internal IEnumerable<FenceView> Fences => _canvas.Children.OfType<FenceView>();
	internal void Refresh() => _placement.Refresh();
	internal void Show() => _placement.Show();
	internal void SetRaised(bool raised) => _placement.Raised = raised;

	internal void Add(FenceView fence)
	{
		fence.Rehost(Handle);
		_canvas.Children.Add(fence);
		UpdateRegion();
	}

	internal void Remove(FenceView fence)
	{
		_canvas.Children.Remove(fence);
		if (!_canvas.Children.OfType<FenceView>().Any())
		{
			DrawBand(null);
			_placement.Hide();
		}
		else { UpdateRegion(); }
	}

	internal void DrawBand(System.Drawing.Rectangle? bounds)
	{
		var work = _source.WorkArea;
		var visible = bounds == null ? System.Drawing.Rectangle.Empty : System.Drawing.Rectangle.Intersect(bounds.Value, work);
		_band.Visibility = visible.Width > 0 && visible.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
		_bandBounds = new RECT(visible.Left - work.Left, visible.Top - work.Top, visible.Right - work.Left, visible.Bottom - work.Top);
		Canvas.SetLeft(_band, _bandBounds.Left / _source.Scale);
		Canvas.SetTop(_band, _bandBounds.Top / _source.Scale);
		_band.Width = Math.Max(0, visible.Width / _source.Scale);
		_band.Height = Math.Max(0, visible.Height / _source.Scale);
		Canvas.SetZIndex(_band, _source.Fences.Count + 2);
		UpdateRegion();
	}

	internal void UpdateRegion()
	{
		var rectangles = _canvas.Children.OfType<FenceView>().Select(_source.BoundsOf);
		WindowApi.SetRegion(Handle, IsBandVisible ? rectangles.Append(_bandBounds) : rectangles, _source.CornerRadiusPixels);
	}

	nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
	{
		// 临时浮层不能被系统关闭命令单独关掉，生命周期由桌面会话管理。
		if (message == 0x0010 && !_disposed) { return 0; }
		// 收起分区的系统右键菜单以浮层为宿主，菜单消息同样转给 Shell
		if (ShellContextMenu.TryHandleMenuMessage((int)message, wParam, lParam, out var menuResult)) { return menuResult; }
		if (message == DesktopPlacement.RefreshMessage) { _placement.Refresh(); return 0; }
		if (message == WM_WINDOWPOSCHANGING) { _placement?.Constrain(lParam); }
		if (message == 0x0014) { return TransparentBackdrop.ClearBackground(hwnd, wParam); }
		if (message == 0x031E) { TransparentBackdrop.ConfigureWindow(hwnd); return 0; }
		if (message == 0x0083) { return 0; }
		if (message == 0x0021) { return _source.IsEditing ? 1 : 3; }
		return WindowApi.DefSubclassProc(hwnd, message, wParam, lParam);
	}

	public void Dispose()
	{
		if (_disposed) { return; }
		_disposed = true;
		_placement.Dispose();
		WindowApi.RemoveWindowSubclass(Handle, _procedure, 1);
		Close();
	}
}
