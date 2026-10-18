using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Views;

public sealed partial class DesktopIconsView : UserControl
{
	readonly ShellItemLoader _images;
	readonly RECT _work;
	readonly double _scale;
	readonly Dictionary<string, (DesktopIconPlacement Placement, Button Button, string? IconKey)> _cells = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
	int _version;

	internal DesktopIconsView(ShellItemLoader images, RECT work, double scale)
	{
		InitializeComponent();
		_images = images;
		_work = work;
		_scale = scale;
		Width = work.Width / scale;
		Height = work.Height / scale;
		Visibility = Visibility.Collapsed;
	}

	internal IReadOnlyList<string> Keys => _cells.Keys.ToArray();
	internal IReadOnlyList<string> SelectedKeys => _selected.ToArray();
	internal IEnumerable<RECT> HitRectangles => Visibility == Visibility.Visible ? _cells.Values.Select(cell => (RECT)cell.Placement.Bounds) : [];
	internal event Action? BoundsChanged;
	internal event Action<string, bool>? SelectionRequested;
	internal event Action<string>? OpenRequested;

	internal async Task SetItemsAsync(IReadOnlyList<DesktopIconPlacement> items)
	{
		int version = ++_version;
		var cells = new Dictionary<string, (DesktopIconPlacement Placement, Button Button, string? IconKey)>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items)
		{
			Button button;
			if (_cells.TryGetValue(item.Entry.Key, out var previous) && previous.IconKey == item.Entry.IconKey)
			{
				button = previous.Button;
			}
			else
			{
				var data = await _images.LoadAsync(item.Entry.Key, Math.Max(96, (int)Math.Round(item.IconSize * _scale)));
				if (version != _version)
				{
					return;
				}
				button = CreateButton(item.Entry.Key, data.ToBitmap());
			}
			var content = (StackPanel)button.Content;
			var image = (Image)content.Children[0];
			image.Width = image.Height = item.IconSize;
			((TextBlock)content.Children[1]).Text = item.Entry.DisplayName;
			button.Width = item.Bounds.Width / _scale;
			button.Height = item.Bounds.Height / _scale;
			Canvas.SetLeft(button, (item.Bounds.Left - _work.Left) / _scale);
			Canvas.SetTop(button, (item.Bounds.Top - _work.Top) / _scale);
			cells.Add(item.Entry.Key, (item, button, item.Entry.IconKey));
		}
		if (version != _version)
		{
			return;
		}
		Icons.Children.Clear();
		_cells.Clear();
		foreach (var (key, cell) in cells)
		{
			_cells.Add(key, cell);
			Icons.Children.Add(cell.Button);
		}
		_selected.IntersectWith(_cells.Keys);
		UpdateSelection();
		BoundsChanged?.Invoke();
	}

	Button CreateButton(string key, ImageSource image)
	{
		var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
		stack.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
		stack.Children.Add(new TextBlock
		{
			Foreground = new SolidColorBrush(Colors.White), FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
			MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
		});
		var button = new Button
		{
			Content = stack, Padding = new Thickness(3, 1, 3, 2), AllowFocusOnInteraction = false, IsTabStop = false,
			MinWidth = 0, MinHeight = 0,
			HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
			BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
		};
		ToolTipService.SetToolTip(button, key);
		button.Click += (_, _) => SelectionRequested?.Invoke(key, GetAsyncKeyState(VK_CONTROL) < 0 || GetAsyncKeyState(VK_SHIFT) < 0);
		button.DoubleTapped += (_, args) =>
		{
			OpenRequested?.Invoke(key);
			args.Handled = true;
		};
		return button;
	}

	internal void Select(IEnumerable<string> keys)
	{
		_selected.Clear();
		_selected.UnionWith(keys.Where(_cells.ContainsKey));
		UpdateSelection();
	}

	void UpdateSelection()
	{
		foreach (var (key, cell) in _cells)
		{
			cell.Button.Background = new SolidColorBrush(_selected.Contains(key) ? Windows.UI.Color.FromArgb(90, 156, 203, 245) : Colors.Transparent);
			cell.Button.BorderBrush = new SolidColorBrush(_selected.Contains(key) ? Windows.UI.Color.FromArgb(153, 180, 220, 255) : Colors.Transparent);
		}
	}

	internal void ShowIcons(bool visible)
	{
		Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		BoundsChanged?.Invoke();
	}

	internal void Invalidate() => _version++;
}
