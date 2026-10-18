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
	readonly FileRename _rename;
	readonly nint _owner;
	readonly Dictionary<string, (DesktopIconPlacement Placement, Button Button, string? IconKey)> _cells = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
	int _version;
	string? _editingKey;

	internal DesktopIconsView(ShellItemLoader images, RECT work, double scale, FileRename rename, nint owner)
	{
		InitializeComponent();
		_images = images;
		_work = work;
		_scale = scale;
		_rename = rename;
		_owner = owner;
		Width = work.Width / scale;
		Height = work.Height / scale;
		Visibility = Visibility.Collapsed;
		Editor.Opened += () => { Icons.IsHitTestVisible = false; BoundsChanged?.Invoke(); };
		Editor.Closed += () => { Icons.IsHitTestVisible = true; _editingKey = null; BoundsChanged?.Invoke(); };
	}

	internal IReadOnlyList<string> Keys => _cells.Keys.ToArray();
	internal IReadOnlyList<string> SelectedKeys => _selected.ToArray();
	internal IEnumerable<RECT> HitRectangles => Visibility == Visibility.Visible ? IconRectangles() : [];
	internal NameEditor EditorControl => Editor;
	internal event Action? BoundsChanged;
	internal event Action<string, bool>? SelectionRequested;
	internal event Action<string>? OpenRequested;
	internal event Action<string>? Failed;

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
		if (_editingKey != null && !cells.ContainsKey(_editingKey))
		{
			Editor.Cancel();
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
		button.RightTapped += (_, args) =>
		{
			SelectionRequested?.Invoke(key, false);
			if (_cells.TryGetValue(key, out var cell) && _rename.CanRenameDesktop(cell.Placement.Entry))
			{
				var command = new MenuFlyoutItem { Text = "重命名" };
				command.Click += (_, _) =>
				{
					try
					{
						BeginRename(key);
					}
					catch (Exception ex)
					{
						Failed?.Invoke(ex.Message);
					}
				};
				var menu = new MenuFlyout();
				menu.Items.Add(command);
				menu.ShowAt(button, args.GetPosition(button));
			}
			args.Handled = true;
		};
		return button;
	}

	internal void BeginRename(string key)
	{
		if (!_cells.TryGetValue(key, out var item))
		{
			throw new InvalidOperationException("此图标已发生变化，请刷新后重试。");
		}
		if (!_rename.CanRenameDesktop(item.Placement.Entry))
		{
			throw new InvalidOperationException("此桌面项目暂不支持改名。");
		}
		Editor.Cancel();
		_editingKey = key;
		Canvas.SetLeft(Editor, Math.Clamp((item.Placement.Bounds.Left - _work.Left) / _scale, 0, Math.Max(0, Width - Editor.Width)));
		Canvas.SetTop(Editor, Math.Clamp((item.Placement.Bounds.Top - _work.Top) / _scale, 0, Math.Max(0, Height - Editor.Height)));
		Editor.Begin("重命名文件或文件夹", item.Placement.Entry.DisplayName, input => _rename.RenameDesktop(item.Placement.Entry, input, _owner));
	}

	IEnumerable<RECT> IconRectangles()
	{
		foreach (var cell in _cells.Values)
		{
			yield return cell.Placement.Bounds;
		}
		if (Editor.IsEditing)
		{
			int left = _work.Left + (int)Math.Floor(Canvas.GetLeft(Editor) * _scale);
			int top = _work.Top + (int)Math.Floor(Canvas.GetTop(Editor) * _scale);
			yield return new RECT(left, top, left + (int)Math.Ceiling(Editor.Width * _scale), top + (int)Math.Ceiling(Editor.Height * _scale));
		}
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
		if (!visible)
		{
			Editor.Cancel();
		}
		Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		BoundsChanged?.Invoke();
	}

	internal void Invalidate()
	{
		_version++;
		Editor.Cancel();
	}
}
