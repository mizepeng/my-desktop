using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;

namespace MyDesktop.Views;

/// <summary>
/// 设置窗口的「角标」页：角标顺序（跟随系统 / 自定义顺序）、快捷方式小箭头，以及自定义顺序的列表（拖动排序、勾选启用、标出加载情况）。
/// </summary>
internal partial class OverlaySettingsPage : UserControl
{
	FenceManager? _manager;
	// LoadValues 填控件期间不当作用户改动
	bool _loading;
	// 各角标处理程序的加载情况，后台查好之前为空
	Dictionary<Guid, OverlayHandlerStatus>? _overlayStatus;
	// 角标列表每重新生成一次加一，等加载情况期间又重新生成的话，旧的那次作废
	int _overlayListVersion;
	// 正在拖动调整顺序的那一行
	Border? _draggingRow;
	// 列表里「系统角标」的三态勾选框，以及它下面的快捷方式箭头、共享、其他系统角标
	CheckBox? _systemBox;
	CheckBox? _arrowBox;
	CheckBox? _shareBox;
	CheckBox? _otherBox;

	public OverlaySettingsPage()
	{
		InitializeComponent();
	}

	FenceManager Manager => _manager ?? throw new InvalidOperationException("角标页还没有绑定 FenceManager");

	AppSettings Settings => Manager.Settings;

	/// <summary>
	/// 角标顺序的下拉框（设置窗口统一处理下拉框的滚轮）。
	/// </summary>
	public ComboBox ModeBox => OverlayModeBox;

	public void Bind(FenceManager manager) => _manager = manager;

	/// <summary>
	/// 按当前设置填控件（打开设置窗口、恢复默认后）。
	/// </summary>
	public void LoadValues()
	{
		_loading = true;
		ShortcutArrowBox.IsChecked = Settings.ShowShortcutArrows;
		OverlayModeBox.ItemsSource = Enum.GetValues<OverlayMode>().Select(m => m.DisplayName()).ToList();
		OverlayModeBox.SelectedIndex = (int)Settings.OverlayMode;
		_loading = false;
		UpdateOverlayPage();
	}

	void ShortcutArrowBox_Click(object sender, RoutedEventArgs e)
	{
		Settings.ShowShortcutArrows = ShortcutArrowBox.IsChecked == true;
		Manager.ApplyShortcutArrows();
		Manager.SaveSoon();
	}

	void OverlayModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || OverlayModeBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.OverlayMode = (OverlayMode)OverlayModeBox.SelectedIndex;
		UpdateOverlayPage();
		Manager.ApplyOverlayHandlers();
		Manager.SaveSoon();
	}

	/// <summary>
	/// 跟随系统时只显示快捷方式小箭头的开关；自定义顺序时显示列表，箭头是「系统角标」下面的一项（同一个设置）。
	/// 列表只在自定义顺序时生成：要逐个创建处理程序查加载情况，跟随系统时不去碰它们。
	/// </summary>
	void UpdateOverlayPage()
	{
		bool custom = Settings.OverlayMode == OverlayMode.Custom;
		ShortcutArrowCard.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
		OverlayListCard.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
		if (custom)
		{
			_ = BuildOverlayListAsync();
		}
	}

	/// <summary>
	/// 按 Settings.CustomOverlayHandlers 的顺序列出「系统角标」和全部处理程序。列表为空（第一次切到自定义顺序）时按系统的优先级生成：
	/// 处理程序按报告的优先级、同级按注册表顺序，能提供角标的都勾上，「系统角标」在最后（Git 仓库里的快捷方式默认显示 Git 状态，
	/// 也只有处理程序都不认领的图标才去问系统，省得每个图标都多问几轮）；注册表里新出现的处理程序加在「系统角标」前面，已经不在注册表里的去掉。
	/// </summary>
	async Task BuildOverlayListAsync()
	{
		int version = ++_overlayListVersion;
		OverlayHandlerPanel.Children.Clear();
		_systemBox = null;
		OverlayHandlerPanel.Children.Add(new TextBlock { Style = (Style)FindResource("Hint"), Text = "正在检查角标处理程序…" });
		UpdateOverlayButtons();
		var status = await IconOverlayHandlers.GetStatusAsync();
		if (version != _overlayListVersion)
		{
			return;
		}
		_overlayStatus = status;
		var handlers = IconOverlayHandlers.ReadAll();
		var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [IconOverlayHandlers.SystemId] = "系统角标" };
		foreach (var handler in handlers)
		{
			names.TryAdd(handler.Clsid.ToString("B"), handler.DisplayName);
		}

		var list = Settings.CustomOverlayHandlers;
		bool fresh = list.Count == 0;
		int before = list.Count;
		list.RemoveAll(h => !names.ContainsKey(h.Id));
		bool changed = list.Count != before;
		var newcomers = handlers
				.Select((h, index) => (Handler: h, Index: index))
				.OrderBy(h => status.TryGetValue(h.Handler.Clsid, out var s) ? s.Priority : int.MaxValue)
				.ThenBy(h => h.Index)
				.Select(h => new OverlayHandlerSetting { Id = h.Handler.Clsid.ToString("B"), Enabled = ProvidesOverlay(h.Handler.Clsid) })
				.Where(n => !list.Any(h => string.Equals(h.Id, n.Id, StringComparison.OrdinalIgnoreCase)))
				.ToList();
		if (newcomers.Count > 0)
		{
			// 新出现的处理程序插在「系统角标」前面（它不在列表里时加在最后）
			int system = list.FindIndex(h => h.Id == IconOverlayHandlers.SystemId);
			list.InsertRange(system < 0 ? list.Count : system, newcomers);
			changed = true;
			if (!fresh)
			{
				Log.Info($"角标自定义顺序加入新出现的项：{string.Join("、", newcomers.Select(n => names[n.Id]))}");
			}
		}
		if (!list.Any(h => h.Id == IconOverlayHandlers.SystemId))
		{
			list.Add(new OverlayHandlerSetting { Id = IconOverlayHandlers.SystemId, Enabled = true });
			changed = true;
		}
		if (changed)
		{
			Manager.ApplyOverlayHandlers();
			Manager.SaveSoon();
		}

		OverlayHandlerPanel.Children.Clear();
		foreach (var entry in list)
		{
			OverlayHandlerPanel.Children.Add(CreateOverlayRow(entry, names[entry.Id]));
		}
		UpdateOverlayButtons();
	}

	bool ProvidesOverlay(Guid clsid)
	{
		return _overlayStatus != null && _overlayStatus.TryGetValue(clsid, out var s)
				&& s.State is OverlayHandlerState.Loaded or OverlayHandlerState.SlotsFull;
	}

	/// <summary>
	/// 这一项能不能勾选：「系统角标」总能；处理程序要提供了角标才能。
	/// </summary>
	bool CanEnableOverlay(string id) => id == IconOverlayHandlers.SystemId || (Guid.TryParse(id, out var clsid) && ProvidesOverlay(clsid));

	/// <summary>
	/// 一行：左边是拖动调整顺序的把手，右边是勾选框（名称和加载情况）。行的 Tag 是设置里的那一项。
	/// 「系统角标」的勾选框是三态的，下面缩进列出它包含的三项（见 CreateSystemChildren）。
	/// </summary>
	Border CreateOverlayRow(OverlayHandlerSetting entry, string name)
	{
		bool system = entry.Id == IconOverlayHandlers.SystemId;
		string state;
		if (system)
		{
			state = "Windows 内置的角标，包含下面三项";
		}
		else if (Guid.TryParse(entry.Id, out var clsid) && _overlayStatus!.TryGetValue(clsid, out var status))
		{
			state = status.State switch
			{
				OverlayHandlerState.Loaded => "系统已加载，资源管理器里也显示",
				OverlayHandlerState.SlotsFull => "系统角标位置已满，资源管理器里不显示",
				OverlayHandlerState.NoOverlay => "处理程序自己没有提供角标（可能在它的设置里关掉了），不能勾选",
				OverlayHandlerState.Failed => "无法加载，不能勾选",
			};
		}
		else
		{
			state = "加载情况未知";
		}
		var grip = new TextBlock
		{
			Text = "\uE76F",
			FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
			FontSize = 16,
			Padding = new Thickness(4, 6, 10, 6),
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.SizeNS,
			Background = Brushes.Transparent,
			ToolTip = "拖动调整顺序",
		};
		var box = new CheckBox
		{
			Content = new StackPanel { Children = { new TextBlock { Text = name }, new TextBlock { Style = (Style)FindResource("Hint"), Text = state } } },
			ToolTip = entry.Id,
			VerticalAlignment = VerticalAlignment.Center,
			IsChecked = entry.Enabled,
		};
		UIElement content = box;
		if (system)
		{
			// 点父项：子项没全勾时全勾上，全勾着时全取消（半选状态下点一下也是全勾上）
			box.Click += (_, _) => SetSystemOverlays(box.IsChecked == true);
			_systemBox = box;
			content = new StackPanel { Children = { box, CreateSystemChildren() } };
		}
		else
		{
			box.Click += (_, _) => SetOverlayEnabled(entry, box.IsChecked == true);
		}
		var row = new Border { Tag = entry, Padding = new Thickness(0, 2, 0, 2), Child = new DockPanel { Children = { grip, content } } };
		DockPanel.SetDock(grip, Dock.Left);
		grip.MouseLeftButtonDown += (_, e) =>
		{
			_draggingRow = row;
			row.Opacity = 0.6;
			// 由列表捕获鼠标：拖动中要把行移出再插回，捕获在行里的话一移出就丢了
			OverlayHandlerPanel.CaptureMouse();
			e.Handled = true;
		};
		return row;
	}

	void OverlayHandlerPanel_MouseMove(object sender, MouseEventArgs e)
	{
		if (_draggingRow != null)
		{
			MoveDraggingRow(e.GetPosition(OverlayHandlerPanel).Y);
		}
	}

	void OverlayHandlerPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OverlayHandlerPanel.ReleaseMouseCapture();

	void OverlayHandlerPanel_LostMouseCapture(object sender, MouseEventArgs e) => FinishOverlayDrag();

	/// <summary>
	/// 「系统角标」下面的三项，顺序固定，跟着父项一起拖动。快捷方式箭头就是「显示快捷方式小箭头」那个设置。
	/// </summary>
	UIElement CreateSystemChildren()
	{
		_arrowBox = CreateSystemChild("快捷方式箭头", "快捷方式、目录联接左下角的箭头", Settings.ShowShortcutArrows, enabled =>
		{
			Settings.ShowShortcutArrows = enabled;
			ShortcutArrowBox.IsChecked = enabled;
		});
		_shareBox = CreateSystemChild("共享", "共享出去的文件夹", Settings.ShowShareOverlay, enabled => Settings.ShowShareOverlay = enabled);
		_otherBox = CreateSystemChild("其他系统角标", "Windows 的其他内置角标，没法事先列全", Settings.ShowOtherSystemOverlays,
				enabled => Settings.ShowOtherSystemOverlays = enabled);
		((StackPanel)_otherBox.Content).Children.Add(CreateOtherOverlaysView());
		return new StackPanel { Margin = new Thickness(28, 0, 0, 0), Children = { _arrowBox, _shareBox, _otherBox } };
	}

	CheckBox CreateSystemChild(string name, string hint, bool value, Action<bool> set)
	{
		var box = new CheckBox
		{
			Content = new StackPanel { Children = { new TextBlock { Text = name }, new TextBlock { Style = (Style)FindResource("Hint"), Text = hint } } },
			Margin = new Thickness(0, 6, 0, 0),
			IsChecked = value,
		};
		box.Click += (_, _) =>
		{
			set(box.IsChecked == true);
			ApplyOverlayChange();
		};
		return box;
	}

	bool[] SystemOverlayStates() => [Settings.ShowShortcutArrows, Settings.ShowShareOverlay, Settings.ShowOtherSystemOverlays];

	void SetSystemOverlays(bool enabled)
	{
		Settings.ShowShortcutArrows = enabled;
		Settings.ShowShareOverlay = enabled;
		Settings.ShowOtherSystemOverlays = enabled;
		ShortcutArrowBox.IsChecked = enabled;
		ApplyOverlayChange();
	}

	/// <summary>
	/// 「系统角标」里认不出来的那些（没法事先列全，遇到时记下来）：列出序号、图像和第一次遇到的文件，没有遇到过时不显示。
	/// </summary>
	UIElement CreateOtherOverlaysView()
	{
		var others = IconOverlayHandlers.OtherOverlays.OrderBy(o => o.Key).ToList();
		var panel = new StackPanel();
		var hint = (Style)FindResource("Hint");
		if (others.Count == 0)
		{
			return panel;
		}
		panel.Children.Add(new TextBlock { Style = hint, Text = "遇到过：" });
		foreach (var (overlay, path) in others)
		{
			var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
			line.Children.Add(new Image { Source = ShellIconLoader.OverlayImage(overlay), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
			line.Children.Add(new TextBlock { Style = hint, Margin = new Thickness(0), Text = $"序号 {overlay}，例如 {path}" });
			panel.Children.Add(line);
		}
		return panel;
	}

	/// <summary>
	/// 拖动中：鼠标越过哪一行的中线，就把拖着的行挪到那里。
	/// </summary>
	void MoveDraggingRow(double y)
	{
		var rows = OverlayHandlerPanel.Children.OfType<Border>().ToList();
		int from = rows.IndexOf(_draggingRow!);
		int to = 0;
		for (int i = 0; i < rows.Count; i++)
		{
			if (rows[i] == _draggingRow)
			{
				continue;
			}
			double middle = rows[i].TranslatePoint(new Point(0, rows[i].ActualHeight / 2), OverlayHandlerPanel).Y;
			if (y > middle)
			{
				to = i < from ? i + 1 : i;
			}
		}
		if (to != from)
		{
			OverlayHandlerPanel.Children.RemoveAt(from);
			OverlayHandlerPanel.Children.Insert(to, _draggingRow!);
		}
	}

	void FinishOverlayDrag()
	{
		if (_draggingRow is not Border row)
		{
			return;
		}
		_draggingRow = null;
		row.Opacity = 1;
		var order = OverlayHandlerPanel.Children.OfType<Border>().Select(r => (OverlayHandlerSetting)r.Tag).ToList();
		if (order.SequenceEqual(Settings.CustomOverlayHandlers))
		{
			return;
		}
		Settings.CustomOverlayHandlers = order;
		Manager.ApplyOverlayHandlers();
		Manager.SaveSoon();
	}

	/// <summary>
	/// 不能勾选的项禁用，但已勾选的始终可以取消；「系统角标」按它下面三项显示全勾、全不勾或半选。
	/// </summary>
	void UpdateOverlayButtons()
	{
		var states = SystemOverlayStates();
		var rows = OverlayHandlerPanel.Children.OfType<Border>().ToList();
		foreach (var row in rows)
		{
			var entry = (OverlayHandlerSetting)row.Tag;
			if (entry.Id == IconOverlayHandlers.SystemId)
			{
				continue;
			}
			var box = ((DockPanel)row.Child).Children.OfType<CheckBox>().Single();
			box.IsEnabled = box.IsChecked == true || CanEnableOverlay(entry.Id);
		}
		if (_systemBox != null)
		{
			_systemBox.IsChecked = states.All(s => s) ? true : states.Any(s => s) ? null : false;
			_arrowBox!.IsChecked = states[0];
			_shareBox!.IsChecked = states[1];
			_otherBox!.IsChecked = states[2];
		}
		var entries = rows.Select(r => (OverlayHandlerSetting)r.Tag).Where(h => h.Id != IconOverlayHandlers.SystemId).ToList();
		bool hasSystem = _systemBox != null;
		OverlaySelectAllButton.IsEnabled = (hasSystem && !states.All(s => s)) || entries.Any(h => !h.Enabled && CanEnableOverlay(h.Id));
		OverlaySelectNoneButton.IsEnabled = (hasSystem && states.Any(s => s)) || entries.Any(h => h.Enabled);
	}

	void OverlaySelectAll_Click(object sender, RoutedEventArgs e) => SetAllOverlays(true);

	void OverlaySelectNone_Click(object sender, RoutedEventArgs e) => SetAllOverlays(false);

	void SetAllOverlays(bool enabled)
	{
		Settings.ShowShortcutArrows = enabled;
		Settings.ShowShareOverlay = enabled;
		Settings.ShowOtherSystemOverlays = enabled;
		ShortcutArrowBox.IsChecked = enabled;
		foreach (var row in OverlayHandlerPanel.Children.OfType<Border>())
		{
			var entry = (OverlayHandlerSetting)row.Tag;
			if (entry.Id == IconOverlayHandlers.SystemId || (enabled && !CanEnableOverlay(entry.Id)))
			{
				continue;
			}
			entry.Enabled = enabled;
			((DockPanel)row.Child).Children.OfType<CheckBox>().Single().IsChecked = enabled;
		}
		ApplyOverlayChange();
	}

	void SetOverlayEnabled(OverlayHandlerSetting entry, bool enabled)
	{
		entry.Enabled = enabled;
		ApplyOverlayChange();
	}

	void ApplyOverlayChange()
	{
		UpdateOverlayButtons();
		// 快捷方式箭头也可能变了，ApplyOverlayHandlers 连同它一起重新应用
		Manager.ApplyOverlayHandlers();
		Manager.SaveSoon();
	}
}
