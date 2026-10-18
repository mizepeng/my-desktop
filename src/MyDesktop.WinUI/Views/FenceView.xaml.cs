using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.WinUI.Effects;
using MyDesktop.WinUI.Services;
using Windows.Foundation;
using Windows.UI;

namespace MyDesktop.WinUI.Views;

public sealed partial class FenceView : UserControl, IDisposable
{
	readonly FenceFrame _frame;
	readonly AppSettings _settings;
	readonly ShellItemLoader _images;
	readonly Visual _visual;
	readonly CompositionRoundedRectangleGeometry _geometry;
	readonly ObservableCollection<FenceItem> _items = [];
	readonly SystemBackdropElement _backdrop;
	int _contentVersion;
	bool _dragging;
	Point _pointerStart;
	Vector3 _translationStart;

	internal FenceView(FenceFrame frame, AppSettings settings, ShellItemLoader images)
	{
		InitializeComponent();
		_frame = frame;
		_settings = settings;
		_images = images;
		Width = frame.Active.Width;
		Height = frame.Active.Height;
		_backdrop = new SystemBackdropElement();
		PanelRoot.Children.Insert(0, _backdrop);
		_visual = ElementCompositionPreview.GetElementVisual(this);
		_geometry = _visual.Compositor.CreateRoundedRectangleGeometry();
		_geometry.CornerRadius = new Vector2((float)settings.CornerRadius);
		_visual.Clip = _visual.Compositor.CreateGeometricClip(_geometry);
		Items.ItemsSource = _items;
		Header.PointerPressed += Header_PointerPressed;
		Header.PointerMoved += Header_PointerMoved;
		Header.PointerReleased += (_, _) => FinishDrag();
		Header.PointerCaptureLost += (_, _) => FinishDrag();
		var menu = new MenuFlyout();
		var refresh = new MenuFlyoutItem { Text = "刷新" };
		refresh.Click += async (_, _) => await LoadItemsAsync();
		var exit = new MenuFlyoutItem { Text = "退出 MyDesktop" };
		exit.Click += (_, _) => ExitRequested?.Invoke();
		menu.Items.Add(refresh);
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(exit);
		Header.ContextFlyout = menu;
		Loaded += async (_, _) =>
		{
			ContentReady = LoadItemsAsync();
			await ContentReady;
		};
		RefreshAppearance();
		RefreshTabs();
	}

	internal FenceFrame Frame => _frame;
	internal Visual PanelVisual => _visual;
	internal double VisibleHeight => _frame.Active.RolledUp ? 36 : Height;
	internal Task ContentReady { get; private set; } = Task.CompletedTask;
	internal IReadOnlyList<string> DisplayedPaths => _items.Select(item => item.Path).ToArray();
	internal string ContentStatus => Status.Text;
	internal GridView ItemList => Items;
	internal event Action? BoundsChanged;
	internal event Action? SaveRequested;
	internal event Action? ExitRequested;

	internal void ArrangeAt(double x, double y, double width, double height)
	{
		Canvas.SetLeft(this, x);
		Canvas.SetTop(this, y);
		Width = width;
		Height = height;
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
	}

	internal void ActivateTab(Guid id)
	{
		_frame.Activate(id);
		RefreshTabs();
		RefreshAppearance();
		ContentReady = LoadItemsAsync();
		SaveRequested?.Invoke();
	}

	void RefreshTabs()
	{
		Tabs.Children.Clear();
		foreach (var member in _frame.Members)
		{
			var tab = new ToggleButton { Content = member.Title, IsChecked = member == _frame.Active, AllowFocusOnInteraction = false };
			tab.Click += (_, _) => ActivateTab(member.Id);
			Tabs.Children.Add(tab);
		}
	}

	void RefreshAppearance()
	{
		var model = _frame.Active;
		var color = (Color)XamlBindingHelper.ConvertValue(typeof(Color), model.Color ?? _settings.DefaultColor);
		color.A = (byte)Math.Round(255 * (model.Opacity ?? _settings.DefaultOpacity));
		Tint.Background = new SolidColorBrush(color);
		Tint.CornerRadius = new CornerRadius(_settings.CornerRadius);
		_backdrop.CornerRadius = Tint.CornerRadius;
		var background = model.Background ?? _settings.DefaultBackground;
		_backdrop.SystemBackdrop = background switch
		{
			BackgroundEffect.Translucent => null,
			// 新界面的毛玻璃统一由系统材质承载，保留旧配置枚举以兼容数据。
			BackgroundEffect.Blur => new SystemAcrylicBackdrop(),
			BackgroundEffect.Acrylic => new SystemAcrylicBackdrop(),
		};
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
	}

	void Fold_Click(object sender, RoutedEventArgs args) => SetRolledUp(!_frame.Active.RolledUp);

	internal void SetRolledUp(bool rolledUp)
	{
		_frame.SetRolledUp(rolledUp);
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
		BoundsChanged?.Invoke();
		SaveRequested?.Invoke();
	}

	void Header_PointerPressed(object sender, PointerRoutedEventArgs args)
	{
		if (_frame.Active.Locked || !args.GetCurrentPoint(null).Properties.IsLeftButtonPressed || IsButton(args.OriginalSource as DependencyObject))
		{
			return;
		}
		if (Header.CapturePointer(args.Pointer))
		{
			_dragging = true;
			_pointerStart = args.GetCurrentPoint((UIElement)Parent).Position;
			_translationStart = _visual.TransformMatrix.Translation;
		}
	}

	void Header_PointerMoved(object sender, PointerRoutedEventArgs args)
	{
		if (_dragging)
		{
			var point = args.GetCurrentPoint((UIElement)Parent).Position;
			TranslateTo(_translationStart.X + point.X - _pointerStart.X, _translationStart.Y + point.Y - _pointerStart.Y);
		}
	}

	internal void TranslateTo(double x, double y)
	{
		_visual.TransformMatrix = Matrix4x4.CreateTranslation((float)x, (float)y, 0);
		BoundsChanged?.Invoke();
	}

	void FinishDrag()
	{
		if (!_dragging)
		{
			return;
		}
		_dragging = false;
		Header.ReleasePointerCaptures();
		CommitPosition();
	}

	internal void CommitPosition() => SaveRequested?.Invoke();

	static bool IsButton(DependencyObject? source)
	{
		for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
		{
			if (element is ButtonBase)
			{
				return true;
			}
		}
		return false;
	}

	async Task LoadItemsAsync()
	{
		int version = ++_contentVersion;
		var model = _frame.Active;
		_items.Clear();
		Status.Text = "";
		try
		{
			var paths = await Task.Run(() => model.IsPortal ? Directory.GetFileSystemEntries(model.FolderPath) : model.Members.ToArray());
			foreach (string path in paths)
			{
				if (version != _contentVersion)
				{
					return;
				}
				if (!path.StartsWith("::{", StringComparison.Ordinal) && !File.Exists(path) && !Directory.Exists(path))
				{
					continue;
				}
				if (model.IsPortal && !_settings.ShowHiddenFiles && File.GetAttributes(path).HasFlag(FileAttributes.Hidden))
				{
					continue;
				}
				var data = await _images.LoadAsync(path, 96);
				if (version != _contentVersion)
				{
					return;
				}
				var bitmap = new WriteableBitmap(data.Width, data.Height);
				using (var stream = bitmap.PixelBuffer.AsStream())
				{
					stream.Write(data.Pixels);
				}
				bitmap.Invalidate();
				_items.Add(new FenceItem(path, data.Name, bitmap));
			}
			if (version == _contentVersion && _items.Count == 0)
			{
				Status.Text = "此分区为空";
			}
		}
		catch (Exception ex)
		{
			if (version == _contentVersion)
			{
				Status.Text = $"读取分区失败：{ex.Message}";
				Log.Error($"读取分区失败：{model.Title}", ex);
			}
		}
	}

	void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
	{
		var source = args.OriginalSource as DependencyObject;
		while (source != null && source is not FrameworkElement { DataContext: FenceItem })
		{
			source = VisualTreeHelper.GetParent(source);
		}
		if (source is FrameworkElement { DataContext: FenceItem item })
		{
			try
			{
				ExplorerShell.Open(item.Path, Path.GetDirectoryName(item.Path) ?? AppPaths.Desktop);
				UsageStats.Add(item.Path);
			}
			catch (Exception ex)
			{
				Native.WindowApi.ShowError($"无法打开项目：{ex.Message}");
			}
		}
	}

	public void Dispose()
	{
		_contentVersion++;
		_backdrop.SystemBackdrop = null;
	}
}

internal sealed record FenceItem(string Path, string Name, ImageSource Icon);
