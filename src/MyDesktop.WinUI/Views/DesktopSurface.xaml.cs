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

	void UpdateRegion() => WindowApi.SetRegion(Handle, _fences.Select(BoundsOf), (int)Math.Round(_cornerRadius * _scale));

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
