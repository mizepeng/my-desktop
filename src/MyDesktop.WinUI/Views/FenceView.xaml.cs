using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
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
	readonly FileRename _rename;
	readonly nint _owner;
	readonly bool _readOnly;
	readonly Visual _visual;
	readonly CompositionRoundedRectangleGeometry _geometry;
	readonly ObservableCollection<FenceItem> _items = [];
	readonly SystemBackdropElement _backdrop;
	uint? _bandPointer;
	Task _contentTask = Task.CompletedTask;
	int _contentVersion;
	bool _refreshAfterEdit;
	bool _settingSelection;
	IReadOnlySet<string> _cut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	internal FenceView(FenceFrame frame, AppSettings settings, ShellItemLoader images, FileRename rename, nint owner, bool readOnly)
	{
		InitializeComponent();
		_frame = frame;
		_settings = settings;
		_images = images;
		_rename = rename;
		_owner = owner;
		_readOnly = readOnly;
		AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
		{
			if (TryStartResize(args) || _placementPointer != null) { return; }
			if (!IsEditing) { TargetRequested?.Invoke(); }
			if (_bandPointer != null && args.GetCurrentPoint(this).Properties.IsRightButtonPressed)
			{
				_bandPointer = null;
				ReleasePointerCaptures();
				BandFinished?.Invoke(ScreenPoint(args), true);
			}
			if (!_readOnly && !IsEditing && !_frame.Active.RolledUp && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
				&& !args.GetCurrentPoint(this).Properties.IsRightButtonPressed
				&& args.GetCurrentPoint(this).Position.Y >= 36 && ItemAt(args.OriginalSource as DependencyObject) == null
				&& !IsScrollBar(args.OriginalSource as DependencyObject) && CapturePointer(args.Pointer))
			{
				_bandPointer = args.Pointer.PointerId;
				BandStarted?.Invoke(ScreenPoint(args), NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) < 0
					|| NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0);
				args.Handled = true;
			}
		}), true);
		PointerMoved += (_, args) => { if (_bandPointer == args.Pointer.PointerId) { BandMoved?.Invoke(ScreenPoint(args)); } };
		PointerReleased += (_, args) =>
		{
			if (_bandPointer == args.Pointer.PointerId)
			{
				_bandPointer = null;
				BandFinished?.Invoke(ScreenPoint(args), false);
				ReleasePointerCapture(args.Pointer);
			}
		};
		PointerCaptureLost += (_, args) =>
		{
			if (_bandPointer == args.Pointer.PointerId) { _bandPointer = null; BandFinished?.Invoke(ScreenPoint(args), true); }
		};
		Editor.Opened += () => UpdateEditing(false);
		Editor.Closed += () =>
		{
			UpdateEditing(true);
			if (_refreshAfterEdit)
			{
				_refreshAfterEdit = false;
				_contentTask = LoadItemsAsync();
			}
		};
		Width = frame.Active.Width;
		Height = frame.Active.Height;
		_backdrop = new SystemBackdropElement();
		PanelRoot.Children.Insert(0, _backdrop);
		_visual = ElementCompositionPreview.GetElementVisual(this);
		_geometry = _visual.Compositor.CreateRoundedRectangleGeometry();
		_geometry.CornerRadius = new Vector2((float)settings.CornerRadius);
		_visual.Clip = _visual.Compositor.CreateGeometricClip(_geometry);
		Items.ItemsSource = _items;
		Items.ContainerContentChanging += (_, args) =>
		{
			if (args.Item is FenceItem item) { args.ItemContainer.Opacity = _cut.Contains(item.Path) ? 0.4 : 1; }
		};
		Items.SelectionChanged += (_, _) =>
		{
			if (!_settingSelection)
			{
				SelectionRequested?.Invoke(Items.SelectedItems.Cast<FenceItem>().Select(item => item.Path).ToArray(),
					NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) < 0 || NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0);
			}
		};
		Header.PointerPressed += Header_PointerPressed;
		PointerMoved += Placement_PointerMoved;
		PointerReleased += (_, args) => { if (_placementPointer == args.Pointer.PointerId) { FinishPlacementPointer(false); } };
		PointerCaptureLost += (_, args) => { if (_placementPointer == args.Pointer.PointerId) { FinishPlacementPointer(true); } };
		PointerExited += (_, _) => { if (_placementPointer == null) { ProtectedCursor = null; } };
		var menu = new MenuFlyout();
		var renameTitle = new MenuFlyoutItem { Text = "重命名分区", IsEnabled = !readOnly };
		renameTitle.Click += (_, _) => BeginTitleEdit();
		var refresh = new MenuFlyoutItem { Text = "刷新" };
		refresh.Click += (_, _) => RefreshContent();
		var exit = new MenuFlyoutItem { Text = "退出 MyDesktop" };
		exit.Click += (_, _) => ExitRequested?.Invoke();
		menu.Items.Add(renameTitle);
		menu.Items.Add(refresh);
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(exit);
		Header.ContextFlyout = menu;
		Loaded += async (_, _) =>
		{
			_contentTask = LoadItemsAsync();
			await ContentReady;
		};
		RefreshAppearance();
		RefreshTabs();
	}

	internal FenceFrame Frame => _frame;
	internal Visual PanelVisual => _visual;
	internal double VisibleHeight => _frame.Active.RolledUp && !IsEditing ? 36 : Height;
	internal Task ContentReady => WaitForContentAsync();
	internal IReadOnlyList<string> DisplayedPaths => _items.Select(item => item.Path).ToArray();
	internal string ContentStatus => Status.Text;
	internal GridView ItemList => Items;
	internal bool IsEditing => Editor.IsEditing;
	internal bool SuspendContentUpdates { get; set; }
	internal bool IsComposing => Editor.IsComposing;
	internal NameEditor EditorControl => Editor;
	internal TextBox NameInput => Editor.NameInput;
	internal string EditErrorText => Editor.ErrorText;
	internal event Action? BoundsChanged;
	internal event Action? SaveRequested;
	internal event Action? ExitRequested;
	internal event Action<IReadOnlyList<string>, bool>? SelectionRequested;
	internal event Action? TargetRequested;
	internal event Action<System.Drawing.Point, bool>? BandStarted;
	internal event Action<System.Drawing.Point>? BandMoved;
	internal event Action<System.Drawing.Point, bool>? BandFinished;
	internal event Action<Guid>? ScopeHidden;
	internal event Action? ContentChanged;
	internal event Action<FrameworkElement, Point>? ItemContextRequested;

	System.Drawing.Point ScreenPoint(PointerRoutedEventArgs args)
	{
		var point = args.GetCurrentPoint(this).Position;
		var window = NativeMethods.GetWindowRect(_owner);
		double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
		var translation = _visual.TransformMatrix.Translation;
		return new System.Drawing.Point(window.Left + (int)Math.Round((Canvas.GetLeft(this) + translation.X + point.X) * scale),
			window.Top + (int)Math.Round((Canvas.GetTop(this) + translation.Y + point.Y) * scale));
	}

	internal IEnumerable<(string Key, Rect Bounds)> ItemRectangles()
	{
		foreach (var item in _items)
		{
			if (Items.ContainerFromItem(item) is GridViewItem container && container.ActualWidth > 0 && container.ActualHeight > 0)
			{
				yield return (item.Path, container.TransformToVisual(this).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight)));
			}
		}
	}

	internal static string? ItemKeyAt(DependencyObject? source) => ItemAt(source)?.Path;

	static bool IsScrollBar(DependencyObject? source)
	{
		for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
		{
			if (element is ScrollBar) { return true; }
		}
		return false;
	}

	internal void ArrangeAt(double x, double y, double width, double height)
	{
		_visual.TransformMatrix = Matrix4x4.Identity;
		Canvas.SetLeft(this, x);
		Canvas.SetTop(this, y);
		Width = width;
		Height = height;
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
		BoundsChanged?.Invoke();
	}

	internal void ActivateTab(Guid id)
	{
		CancelEdit();
		ScopeHidden?.Invoke(_frame.Active.Id);
		_frame.Activate(id);
		RefreshTabs();
		RefreshAppearance();
		_contentTask = LoadItemsAsync();
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
		CancelEdit();
		if (rolledUp) { ScopeHidden?.Invoke(_frame.Active.Id); }
		_frame.SetRolledUp(rolledUp);
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
		BoundsChanged?.Invoke();
		SaveRequested?.Invoke();
	}

	void Header_PointerPressed(object sender, PointerRoutedEventArgs args)
	{
		if (IsEditing || _frame.Active.Locked || !args.GetCurrentPoint(null).Properties.IsLeftButtonPressed
			|| IsButton(args.OriginalSource as DependencyObject) || ResizeEdgesAt(args.GetCurrentPoint(this).Position) != FenceEdges.None)
		{
			return;
		}
		BeginPlacementPointer(args, FenceEdges.None);
	}

	internal void TranslateTo(double x, double y)
	{
		_visual.TransformMatrix = Matrix4x4.CreateTranslation((float)x, (float)y, 0);
		BoundsChanged?.Invoke();
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
		var loaded = new List<FenceItem>();
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
				loaded.Add(new FenceItem(path, data.Name, data.ToBitmap()));
			}
			if (version == _contentVersion && !SuspendContentUpdates)
			{
				_settingSelection = true;
				_items.Clear();
				foreach (var item in loaded)
				{
					_items.Add(item);
				}
				_settingSelection = false;
				ContentChanged?.Invoke();
				Status.Text = _items.Count == 0 ? "此分区为空" : "";
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

	internal void SelectPaths(IEnumerable<string> paths)
	{
		var keys = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
		_settingSelection = true;
		try
		{
			Items.SelectedItems.Clear();
			foreach (var item in _items.Where(item => keys.Contains(item.Path)))
			{
				Items.SelectedItems.Add(item);
			}
		}
		finally { _settingSelection = false; }
	}

	internal void ApplyCutState(IReadOnlySet<string> cut)
	{
		_cut = cut;
		foreach (var item in _items)
		{
			if (Items.ContainerFromItem(item) is GridViewItem container)
			{
				container.Opacity = cut.Contains(item.Path) ? 0.4 : 1;
			}
		}
	}

	async Task WaitForContentAsync()
	{
		while (true)
		{
			var current = _contentTask;
			await current;
			if (ReferenceEquals(current, _contentTask))
			{
				return;
			}
		}
	}

	internal void RefreshContent(bool preserveEdit = false)
	{
		if (preserveEdit && IsEditing)
		{
			_refreshAfterEdit = true;
			return;
		}
		CancelEdit();
		_contentTask = LoadItemsAsync();
	}

	internal void BeginTitleEdit()
	{
		PrepareEdit();
		Editor.Begin("重命名分区", _frame.Active.Title, input =>
		{
			string name = input.Trim();
			if (name.Length == 0)
			{
				throw new ArgumentException("分区名称不能为空。");
			}
			_frame.Active.Title = name;
			RefreshTabs();
			SaveRequested?.Invoke();
		});
	}

	internal void BeginItemRename(string path)
	{
		var item = _items.Single(item => PathUtil.AreEqual(item.Path, path));
		if (!ItemRename.CanRename(_frame.Active, item.Path))
		{
			throw new InvalidOperationException("此项目暂不支持改名。");
		}
		PrepareEdit();
		Editor.Begin("重命名文件或文件夹", item.Name, input => _rename.Rename(_frame.Active, item.Path, item.Name, input, _owner));
	}

	void PrepareEdit()
	{
		if (_readOnly)
		{
			throw new InvalidOperationException("预览模式不能修改分区或文件名称。");
		}
		CancelPlacementPointer();
	}

	internal bool CommitEdit() => Editor.Commit();
	internal void CancelEdit() => Editor.Cancel();

	void UpdateEditing(bool enabled)
	{
		Items.IsEnabled = FoldButton.IsEnabled = enabled;
		foreach (var tab in Tabs.Children.OfType<Control>())
		{
			tab.IsEnabled = enabled;
		}
		_geometry.Size = new Vector2((float)Width, (float)VisibleHeight);
		BoundsChanged?.Invoke();
	}

	void Items_RightTapped(object sender, RightTappedRoutedEventArgs args)
	{
		if (IsEditing || _readOnly)
		{
			return;
		}
		var item = ItemAt(args.OriginalSource as DependencyObject);
		if (item == null || !Items.SelectedItems.Contains(item))
		{
			SelectionRequested?.Invoke(item == null ? [] : new[] { item.Path }, false);
		}
		ItemContextRequested?.Invoke(Items, args.GetPosition(Items));
		args.Handled = true;
	}

	static FenceItem? ItemAt(DependencyObject? source)
	{
		while (source != null)
		{
			if (source is FrameworkElement { DataContext: FenceItem item })
			{
				return item;
			}
			source = VisualTreeHelper.GetParent(source);
		}
		return null;
	}

	void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
	{
		if (!IsEditing && ItemAt(args.OriginalSource as DependencyObject) is { } item)
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
		CancelEdit();
		_contentVersion++;
		_backdrop.SystemBackdrop = null;
	}
}

internal sealed record FenceItem(string Path, string Name, ImageSource Icon);
