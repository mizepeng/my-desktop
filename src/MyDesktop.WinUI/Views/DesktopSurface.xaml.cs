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
	readonly Border _band;
	NameEditor? _editingEditor;
	FrameworkElement? _editingOwner;
	bool _clipboardListener;
	bool _captureInput;

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
		_desktopIcons = new DesktopIconsView(images, workArea, scale, rename, Handle);
		_desktopIcons.BoundsChanged += UpdateRegion;
		Placement = new FencePlacement(this, settings, readOnly);
		RegisterEditor(_desktopIcons.EditorControl, _desktopIcons);
		_desktopIcons.SelectionRequested += (_, _) =>
		{
			_editingEditor?.Cancel();
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
			fence.PlacementStarted += (point, edges) => Placement.Begin(fence, point, edges);
			fence.PlacementMoved += Placement.Update;
			fence.PlacementFinished += async canceled => await Placement.EndAsync(canceled);
			fence.SaveRequested += () =>
			{
				var bounds = BoundsOf(fence);
				frame.SetBounds(bounds.Left + workArea.Left, bounds.Top + workArea.Top,
					(int)Math.Round(fence.Width * scale), (int)Math.Round(fence.Height * scale), (int)Math.Round(scale * 96));
				SaveRequested?.Invoke();
			};
			fence.ExitRequested += () => ExitRequested?.Invoke();
			RegisterEditor(fence.EditorControl, fence);
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
		Canvas.SetZIndex(_notice, _fences.Count + 1);
		_band = new Border
		{
			BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 100, 180, 255)),
			Background = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 70, 150, 240)), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
		};
		Surface.Children.Add(_band);
		Canvas.SetZIndex(_band, _fences.Count + 2);
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
			Placement.Cancel();
			if (_clipboardListener) { RemoveClipboardFormatListener(Handle); }
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
	internal FencePlacement Placement { get; }
	internal Func<bool>? PlacementAllowed { get; set; }
	internal bool IsClosed { get; private set; }
	internal IReadOnlyList<FenceView> Fences => _fences;
	internal event Action? SaveRequested;
	internal event Action? ExitRequested;
	internal event Action? Ready;
	internal event Action? ClipboardChanged;
	internal void ShowOnDesktop() => _placement.Show();
	internal bool IsEditing => _editingEditor != null;
	internal RECT WorkArea => _workArea;
	internal double Scale => _scale;
	internal DesktopIconsView DesktopIcons => _desktopIcons;
	internal string Notice => _noticeText.Text;
	internal Canvas RootElement => Surface;
	internal bool IsInputCaptured => _captureInput;
	internal bool IsBandVisible => _band.Visibility == Visibility.Visible;

	internal void RaiseFence(FenceView fence)
	{
		int order = 1;
		foreach (var other in _fences.Where(other => other != fence).OrderBy(Canvas.GetZIndex)) { Canvas.SetZIndex(other, order++); }
		Canvas.SetZIndex(fence, order);
	}

	internal void SetInputCapture(bool capture)
	{
		_captureInput = capture;
		Surface.Background = capture ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : null;
		UpdateRegion();
	}

	internal void DrawBand(System.Drawing.Rectangle? bounds)
	{
		var visible = bounds == null ? System.Drawing.Rectangle.Empty : System.Drawing.Rectangle.Intersect(bounds.Value, _workArea);
		_band.Visibility = visible.Width > 0 && visible.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
		Canvas.SetLeft(_band, (visible.Left - _workArea.Left) / _scale);
		Canvas.SetTop(_band, (visible.Top - _workArea.Top) / _scale);
		_band.Width = Math.Max(0, visible.Width / _scale);
		_band.Height = Math.Max(0, visible.Height / _scale);
	}

	internal System.Drawing.Rectangle ContentBoundsOf(FenceView fence)
	{
		var bounds = BoundsOf(fence);
		int top = bounds.Top + (int)Math.Round(36 * _scale);
		return System.Drawing.Rectangle.FromLTRB(bounds.Left + _workArea.Left, top + _workArea.Top, bounds.Right + _workArea.Left,
			Math.Max(top, bounds.Bottom) + _workArea.Top);
	}

	internal SelectionTarget[] SelectionTargets(FenceView? fence)
	{
		if (fence == null) { return _desktopIcons.Cells.Select(pair => new SelectionTarget(Guid.Empty, pair.Key, pair.Value)).ToArray(); }
		var content = ContentBoundsOf(fence);
		var bounds = BoundsOf(fence);
		return fence.ItemRectangles().Select(item =>
		{
			var rectangle = new System.Drawing.Rectangle(_workArea.Left + bounds.Left + (int)Math.Round(item.Bounds.X * _scale),
				_workArea.Top + bounds.Top + (int)Math.Round(item.Bounds.Y * _scale), (int)Math.Round(item.Bounds.Width * _scale), (int)Math.Round(item.Bounds.Height * _scale));
			return new SelectionTarget(fence.Frame.Active.Id, item.Key, System.Drawing.Rectangle.Intersect(content, rectangle));
		}).Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0).ToArray();
	}

	internal void WatchClipboard()
	{
		if (!AddClipboardFormatListener(Handle)) { throw new Win32Exception(); }
		_clipboardListener = true;
	}

	internal void ActivateForSelection()
	{
		_editingEditor?.Cancel();
		if (DesktopInput.IsInputDesktop() && !_placement.ActivateDesktop())
		{
			SetNotice("未能将键盘焦点交给桌面，请再次点击图标。");
		}
	}

	internal void SetNotice(string? message)
	{
		_noticeText.Text = message ?? "";
		_notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
		UpdateRegion();
	}

	void CancelDeactivatedEdit()
	{
		if (_editingEditor is { IsComposing: false } && DesktopInput.IsInputDesktop() && !DesktopInput.OwnsForeground(Handle))
		{
			_editingEditor.Cancel();
		}
	}

	void RegisterEditor(NameEditor editor, FrameworkElement owner)
	{
		editor.ActivationRequested += () =>
		{
			var previous = _editingEditor;
			_editingEditor = editor;
			_editingOwner = owner;
			if (previous != editor)
			{
				previous?.Cancel();
			}
			return DesktopInput.Begin(Handle);
		};
		editor.Closed += () =>
		{
			if (_editingEditor == editor)
			{
				_editingEditor = null;
				_editingOwner = null;
				if (!IsClosed)
				{
					DesktopInput.End(Handle, _placement.DesktopHandle);
				}
			}
		};
		editor.CompositionFinished += () => DispatcherQueue.TryEnqueue(CancelDeactivatedEdit);
	}

	void Surface_PointerPressed(object sender, PointerRoutedEventArgs args)
	{
		if (_editingEditor == null)
		{
			return;
		}
		for (var source = args.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
		{
			if (source == _editingOwner)
			{
				return;
			}
		}
		_editingEditor.Cancel();
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
		if (_captureInput)
		{
			WindowApi.SetRegion(Handle, [new RECT(0, 0, _workArea.Width, _workArea.Height)], 0);
			return;
		}
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
		if (message == 0x031D)
		{
			ClipboardChanged?.Invoke();
			return 0;
		}
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
