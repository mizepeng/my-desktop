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
	FenceFrame _frame;
	Guid _scope;
	readonly AppSettings _settings;
	readonly ShellItemLoader _images;
	readonly FileRename _rename;
	nint _owner;
	readonly nint _desktopOwner;
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
	bool _disposed;
	bool _suspendContent, _contentDeferred;
	IReadOnlySet<string> _cut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	internal FenceView(FenceFrame frame, AppSettings settings, ShellItemLoader images, FileRename rename, nint owner, bool readOnly)
	{
		InitializeComponent();
		_frame = frame;
		_scope = frame.Active.Id;
		_settings = settings;
		_images = images;
		_rename = rename;
		_owner = owner;
		_desktopOwner = owner;
		_readOnly = readOnly;
		AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
		{
			if (TryStartResize(args) || _placementPointer != null || _tabPointer != null) { return; }
			if (!IsEditing) { TargetRequested?.Invoke(); }
			if (_bandPointer != null && args.GetCurrentPoint(this).Properties.IsRightButtonPressed)
			{
				_bandPointer = null;
				ReleasePointerCaptures();
				BandFinished?.Invoke(ScreenPoint(args), true);
			}
			if (!_readOnly && !IsEditing && !IsCollapsed && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
				&& !args.GetCurrentPoint(this).Properties.IsRightButtonPressed
				&& ContentBounds.Contains(args.GetCurrentPoint(this).Position) && ItemAt(args.OriginalSource as DependencyObject) == null
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
		InitializeRoll();
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
		Header.AddHandler(PointerPressedEvent, new PointerEventHandler(Header_PointerPressed), true);
		InitializeTabs();
		PointerMoved += Placement_PointerMoved;
		PointerReleased += (_, args) =>
		{
			if (_placementPointer != args.Pointer.PointerId) { return; }
			UpdatePlacementPointer(args);
			FinishPlacementPointer(false);
		};
		PointerCaptureLost += (_, args) => { if (_placementPointer == args.Pointer.PointerId) { FinishPlacementPointer(true); } };
		PointerExited += (_, _) => { if (_placementPointer == null) { ProtectedCursor = null; } };
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
	internal double VisibleHeight => RevealedBounds.Height;
	internal nint InputOwner => _owner;
	internal void Rehost(nint owner)
	{
		_owner = owner;
		UpdateRollLayout();
	}
	internal Task ContentReady => WaitForContentAsync();
	internal IReadOnlyList<string> DisplayedPaths => _items.Select(item => item.Path).ToArray();
	internal string ContentStatus => Status.Text;
	internal GridView ItemList => Items;
	internal bool IsEditing => Editor.IsEditing;
	internal bool SuspendContentUpdates
	{
		get => _suspendContent;
		set
		{
			_suspendContent = value;
			// 暂缓期间读完的内容已丢弃，恢复后只补读这个分区。
			if (!value && _contentDeferred)
			{
				_contentDeferred = false;
				RefreshContent(true);
			}
		}
	}
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

	System.Drawing.Point ScreenPoint(PointerRoutedEventArgs args) => ScreenPoint(args.GetCurrentPoint(null).Position);

	/// <summary>
	/// 按宿主窗口坐标换算屏幕物理像素。
	/// XAML 要到下一次合成提交才看到新的 Composition 平移，相对分区自身取点再叠加当前平移，同一帧内连续拖动会让坐标发散。
	/// </summary>
	internal System.Drawing.Point ScreenPoint(Point windowPoint)
	{
		var window = NativeMethods.GetWindowRect(_owner);
		double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
		return new System.Drawing.Point(window.Left + (int)Math.Round(windowPoint.X * scale), window.Top + (int)Math.Round(windowPoint.Y * scale));
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
		UpdateRollLayout();
		FinishRoll();
	}

	internal void ActivateTab(Guid id)
	{
		if (!_frame.Members.Any(member => member.Id == id)) { throw new ArgumentException("指定分区不属于这个标签组。", nameof(id)); }
		if (_frame.Active.Id == id || TabSwitchAllowed?.Invoke() == false) { return; }
		CancelEdit();
		_frame.Activate(id);
		RebindFrame(_frame);
		SaveRequested?.Invoke();
	}

	internal void RebindFrame(FenceFrame frame)
	{
		StopTabHover();
		_frame = frame;
		if (_scope != frame.Active.Id)
		{
			CancelEdit();
			ScopeHidden?.Invoke(_scope);
			_scope = frame.Active.Id;
			// 新标签加载完成前不能把旧标签的图标当作新标签的文件进行操作。
			_settingSelection = true;
			try { _items.Clear(); }
			finally { _settingSelection = false; }
			_contentTask = LoadItemsAsync();
		}
		RefreshTabs();
		RefreshAppearance();
		if (!_frame.Active.RolledUp && _temporaryExpanded) { ResetTemporaryExpansion(); }
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
		// 非空替换会复用外部链接，不能关闭后重接；相同材质保持原对象，变为半透明时才拆除链接。
		_backdrop.SystemBackdrop = background switch
		{
			BackgroundEffect.Translucent => null,
			// 新界面的毛玻璃统一由系统材质承载，保留旧配置枚举以兼容数据。
			BackgroundEffect.Blur => _backdrop.SystemBackdrop ?? new SystemAcrylicBackdrop(),
			BackgroundEffect.Acrylic => _backdrop.SystemBackdrop ?? new SystemAcrylicBackdrop(),
		};
		UpdateRollLayout();
	}

	void Fold_Click(object sender, RoutedEventArgs args) => SetRolledUp(!_frame.Active.RolledUp);

	internal void SetRolledUp(bool rolledUp)
	{
		if (RollInteractionHeld?.Invoke() == true) { return; }
		CancelEdit();
		if (rolledUp) { ScopeHidden?.Invoke(_frame.Active.Id); }
		_temporaryExpanded = false;
		_collapseTimer.Stop();
		_frame.SetRolledUp(rolledUp);
		RaiseRequested?.Invoke(false);
		RolledChanged?.Invoke();
		_rollTask = AnimateRollAsync();
		FoldButton.Content = rolledUp ? "⌄" : "⌃";
		SaveRequested?.Invoke();
	}

	void Header_PointerPressed(object sender, PointerRoutedEventArgs args)
	{
		StopTabHover();
		var tab = TabAt(args.OriginalSource as DependencyObject);
		if (IsEditing || _frame.Active.Locked || !args.GetCurrentPoint(null).Properties.IsLeftButtonPressed
			|| IsButton(args.OriginalSource as DependencyObject) && tab == null || ResizeEdgesAt(args.GetCurrentPoint(this).Position) != FenceEdges.None)
		{
			return;
		}
		if (tab is { } id && NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0 && _frame.Members.Count > 1)
		{
			BeginTabPointer(args, id);
		}
		else { BeginPlacementPointer(args, FenceEdges.None, tab); }
	}

	internal void TranslateTo(double x, double y)
	{
		_visual.TransformMatrix = Matrix4x4.CreateTranslation((float)x, (float)y, 0);
		UpdateRollLayout();
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
		if (_disposed) { return; }
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
			if (version == _contentVersion && SuspendContentUpdates)
			{
				_contentDeferred = true;
			}
			else if (version == _contentVersion)
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
		CancelTabPointer();
		StopTabHover();
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
		FinishRoll();
		if (enabled && _temporaryExpanded && !_pointerInside) { _collapseTimer.Start(); }
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
		ResetTemporaryExpansion();
		_disposed = true;
		_rollVersion++;
		_geometry.StopAnimation("Size");
		_geometry.StopAnimation("Offset");
		_collapseTimer.Stop();
		StopTabHover();
		CancelTabPointer();
		CancelPlacementPointer();
		CancelEdit();
		_contentVersion++;
		_backdrop.SystemBackdrop = null;
	}
}

internal sealed record FenceItem(string Path, string Name, ImageSource Icon);
