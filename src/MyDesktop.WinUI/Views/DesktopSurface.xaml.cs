using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Effects;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using Windows.Graphics;
using WinRT.Interop;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Views;

public sealed partial class DesktopSurface : Window
{
	readonly WindowApi.SubclassProc _procedure;
	readonly DesktopPlacement _placement;
	readonly RECT _workArea;
	readonly double _scale;
	readonly List<FenceView> _fences = [];
	readonly double _cornerRadius;
	readonly DesktopIconsView _desktopIcons;
	readonly Border _notice;
	readonly TextBlock _noticeText;
	FenceView? _editingFence;

	internal DesktopSurface(RECT workArea, double scale, IEnumerable<FenceFrame> frames, AppSettings settings, ShellItemLoader images, FileRename rename, bool readOnly)
	{
		InitializeComponent();
		Title = "MyDesktop";
		_workArea = workArea;
		_scale = scale;
		_cornerRadius = settings.CornerRadius;
		Handle = WindowNative.GetWindowHandle(this);
		_procedure = WindowMessage;
		if (!WindowApi.SetWindowSubclass(Handle, _procedure, 1, 0))
		{
			throw new Win32Exception();
		}
		var presenter = (OverlappedPresenter)AppWindow.Presenter;
		presenter.IsResizable = false;
		presenter.IsMinimizable = false;
		presenter.IsMaximizable = false;
		presenter.SetBorderAndTitleBar(false, false);
		WindowApi.Configure(Handle);
		AppWindow.MoveAndResize(new RectInt32(workArea.Left, workArea.Top, workArea.Width, workArea.Height));
		SystemBackdrop = new TransparentBackdrop(Handle);
		_placement = new DesktopPlacement(Handle);
		_desktopIcons = new DesktopIconsView(images, workArea, scale);
		_desktopIcons.BoundsChanged += UpdateRegion;
		_desktopIcons.SelectionRequested += (_, _) =>
		{
			_editingFence?.CancelEdit();
			if (DesktopInput.IsInputDesktop() && !_placement.ActivateDesktop())
			{
				SetNotice("未能将键盘焦点交给桌面，请再次点击图标。");
			}
		};
		Surface.Children.Add(_desktopIcons);
		foreach (var frame in frames)
		{
			var model = frame.Active;
			if (model.Width <= 0 || model.Height <= 0)
			{
				throw new InvalidDataException($"分区「{model.Title}」的尺寸无效：{model.Width}×{model.Height}。");
			}
			var fence = new FenceView(frame, settings, images, rename, Handle, readOnly);
			fence.ArrangeAt((model.X - workArea.Left) / scale, (model.Y - workArea.Top) / scale, model.Width / scale, model.Height / scale);
			fence.BoundsChanged += UpdateRegion;
			fence.SaveRequested += () =>
			{
				var bounds = BoundsOf(fence);
				frame.MoveTo(bounds.Left + workArea.Left, bounds.Top + workArea.Top, (int)Math.Round(scale * 96));
				SaveRequested?.Invoke();
			};
			fence.ExitRequested += () => ExitRequested?.Invoke();
			fence.EditingStarted += () =>
			{
				var previous = _editingFence;
				_editingFence = fence;
				if (previous != fence)
				{
					previous?.CancelEdit();
				}
				return DesktopInput.Begin(Handle);
			};
			fence.EditingEnded += () =>
			{
				if (_editingFence == fence)
				{
					_editingFence = null;
					if (!IsClosed)
					{
						DesktopInput.End(Handle, _placement.DesktopHandle);
					}
				}
			};
			fence.CompositionFinished += () => DispatcherQueue.TryEnqueue(CancelDeactivatedEdit);
			Surface.Children.Add(fence);
			_fences.Add(fence);
		}
		_noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
		_notice = new Border
		{
			Child = _noticeText, Width = Math.Min(440, workArea.Width / scale - 24), Height = 72, Padding = new Thickness(10), CornerRadius = new CornerRadius(8),
			Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 45, 45, 45)), Visibility = Visibility.Collapsed,
		};
		var dismiss = new Button { Content = "×", AllowFocusOnInteraction = false, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 0, 6, 0) };
		dismiss.Click += (_, _) => SetNotice(null);
		var noticeContent = new Grid();
		noticeContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		noticeContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		_notice.Child = noticeContent;
		noticeContent.Children.Add(_noticeText);
		Grid.SetColumn(dismiss, 1);
		noticeContent.Children.Add(dismiss);
		Canvas.SetLeft(_notice, 12);
		Canvas.SetTop(_notice, 12);
		Surface.Children.Add(_notice);
		UpdateRegion();
		Surface.PointerPressed += Surface_PointerPressed;
		Activated += (_, args) =>
		{
			if (args.WindowActivationState == WindowActivationState.Deactivated)
			{
				DispatcherQueue.TryEnqueue(CancelDeactivatedEdit);
			}
		};
		Closed += (_, _) =>
		{
			IsClosed = true;
			_desktopIcons.Invalidate();
			foreach (var fence in _fences)
			{
				fence.Dispose();
			}
			_placement.Dispose();
			WindowApi.RemoveWindowSubclass(Handle, _procedure, 1);
		};
	}

	internal nint Handle { get; }
	internal bool IsClosed { get; private set; }
	internal IReadOnlyList<FenceView> Fences => _fences;
	internal event Action? SaveRequested;
	internal event Action? ExitRequested;
	internal event Action? Ready;
	internal void ShowOnDesktop() => _placement.Show();
	internal bool IsEditing => _editingFence != null;
	internal RECT WorkArea => _workArea;
	internal double Scale => _scale;
	internal DesktopIconsView DesktopIcons => _desktopIcons;
	internal string Notice => _noticeText.Text;

	internal void SetNotice(string? message)
	{
		_noticeText.Text = message ?? "";
		_notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
		UpdateRegion();
	}

	void CancelDeactivatedEdit()
	{
		if (_editingFence is { IsComposing: false } && DesktopInput.IsInputDesktop() && !DesktopInput.OwnsForeground(Handle))
		{
			_editingFence.CancelEdit();
		}
	}

	void Surface_PointerPressed(object sender, PointerRoutedEventArgs args)
	{
		if (_editingFence == null)
		{
			return;
		}
		for (var source = args.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
		{
			if (source == _editingFence)
			{
				return;
			}
		}
		_editingFence.CancelEdit();
	}

	void Surface_Loaded(object sender, RoutedEventArgs args) => Ready?.Invoke();

	internal RECT BoundsOf(FenceView fence)
	{
		var translation = fence.PanelVisual.TransformMatrix.Translation;
		double left = Canvas.GetLeft(fence) + translation.X, top = Canvas.GetTop(fence) + translation.Y;
		return new RECT((int)Math.Round(left * _scale), (int)Math.Round(top * _scale),
			(int)Math.Round((left + fence.Width) * _scale), (int)Math.Round((top + fence.VisibleHeight) * _scale));
	}

	void UpdateRegion()
	{
		var rectangles = _fences.Select(BoundsOf).ToList();
		if (_desktopIcons != null)
		{
			foreach (var bounds in _desktopIcons.HitRectangles)
			{
				rectangles.Add(new RECT(bounds.Left - _workArea.Left, bounds.Top - _workArea.Top, bounds.Right - _workArea.Left, bounds.Bottom - _workArea.Top));
			}
		}
		if (_notice?.Visibility == Visibility.Visible)
		{
			rectangles.Add(new RECT((int)(12 * _scale), (int)(12 * _scale), (int)((12 + _notice.Width) * _scale), (int)(84 * _scale)));
		}
		WindowApi.SetRegion(Handle, rectangles, (int)Math.Round(_cornerRadius * _scale));
	}

	nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
	{
		if (message == DesktopPlacement.RefreshMessage)
		{
			_placement.Refresh();
			return 0;
		}
		if (message == WM_WINDOWPOSCHANGING)
		{
			_placement?.Constrain(lParam);
		}
		if (message == 0x0014)
		{
			return TransparentBackdrop.ClearBackground(hwnd, wParam);
		}
		if (message == 0x031E)
		{
			TransparentBackdrop.ConfigureWindow(hwnd);
			return 0;
		}
		if (message == 0x0083)
		{
			return 0;
		}
		if (message == 0x0021)
		{
			return IsEditing ? 1 : 3;
		}
		return WindowApi.DefSubclassProc(hwnd, message, wParam, lParam);
	}
}
