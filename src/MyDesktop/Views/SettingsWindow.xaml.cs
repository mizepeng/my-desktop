using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;

namespace MyDesktop.Views;

internal partial class SettingsWindow : Window
{
	/// <summary>
	/// GitHub 上新建 Issue 的页面，先选「问题反馈」或「功能建议」模板。
	/// </summary>
	const string IssuesPage = "https://github.com/mizepeng/my-desktop/issues/new/choose";

	static readonly string[] Tips =
	[
		"· 把桌面上的图标拖进分区即可收纳，从分区拖回桌面即可还原；文件始终留在桌面文件夹里，退出后桌面保持原样",
		"· 拖入时按住 Ctrl 为复制，按住 Alt 为创建快捷方式",
		"· 在分区内拖动图标可以自由调整顺序",
		"· 用左键在桌面空白处拖出选框，松开后在选框旁点「创建分区」或「创建映射分区」，框住的图标会一起收进新分区",
		"· 拖动标题栏移动分区、拖动边缘调整大小（会按整行整列吸附），双击标题栏卷起/展开",
		"· 把一个分区拖到另一个分区的标题栏上松手，就合并成标签页；按住 Shift 拖动标签可以调整顺序，拖出标题栏就拆成独立分区",
		"· 右键分区空白处或点击标题栏的「⋯」打开分区菜单，可修改颜色、排序、视图等",
		"· 右键文件弹出系统右键菜单；F2 重命名，Delete 删除到回收站",
		"· 双击桌面空白处可以一键隐藏/显示图标和分区；隐藏哪些可以在这里、托盘菜单或桌面右键菜单里选",
		"· 按搜索快捷键（默认 Alt+Space，可在「常规」里修改）弹出搜索框，输入名称、全拼或首字母找到桌面上的图标，回车打开",
		"· 桌面空白处的右键菜单里有 MyDesktop 子菜单",
	];

	readonly FenceManager _manager;
	readonly ObservableCollection<OrganizeRule> _rules;
	bool _loading = true;
	// 正在录入搜索快捷键：这期间 Alt+空格等组合键不能弹出窗口的系统菜单
	bool _capturingHotkey;

	public SettingsWindow(FenceManager manager)
	{
		_manager = manager;
		InitializeComponent();
		_rules = new ObservableCollection<OrganizeRule>(manager.Settings.Rules);
		RulesList.ItemsSource = _rules;
		LoadValues();
		BlockComboBoxWheel();
	}

	/// <summary>
	/// 下拉框没展开时滚轮不改选项（鼠标停在下拉框上滚动页面，很容易误改设置），交给页面滚动；展开后照常滚动选项列表。
	/// </summary>
	void BlockComboBoxWheel()
	{
		foreach (var box in new[] { DoubleClickTargetBox, StartupVisibilityBox, StartupHideTargetBox, TabAlignmentBox,
				BackgroundBox, RadiusChoiceBox, IconSizeBox, FenceFontBox, DesktopFontBox })
		{
			box.PreviewMouseWheel += ComboBox_PreviewMouseWheel;
		}
	}

	AppSettings Settings => _manager.Settings;

	void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (sender is ComboBox { IsDropDownOpen: false } box)
		{
			e.Handled = true;
			// 阻止下拉框改选项，把滚轮交给父级，让设置页面照常滚动
			if (VisualTreeHelper.GetParent(box) is UIElement parent)
			{
				parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
				{
					RoutedEvent = MouseWheelEvent,
					Source = box,
				});
			}
		}
	}

	void LoadValues()
	{
		_loading = true;
		AutoStartBox.IsChecked = AutoStart.IsEnabled();
		HotkeyBox.Text = HotkeyText();
		DoubleClickBox.IsChecked = Settings.DoubleClickToHide;
		DoubleClickTargetBox.ItemsSource = Enum.GetValues<HideTarget>().Select(t => t.DisplayName()).ToList();
		DoubleClickTargetBox.SelectedIndex = (int)Settings.DoubleClickTarget;
		StartupVisibilityBox.ItemsSource = Enum.GetValues<StartupVisibility>().Select(v => v.DisplayName()).ToList();
		StartupVisibilityBox.SelectedIndex = (int)Settings.StartupVisibility;
		StartupHideTargetBox.ItemsSource = Enum.GetValues<HideTarget>().Select(t => t.DisplayName()).ToList();
		StartupHideTargetBox.SelectedIndex = (int)Settings.StartupHideTarget;
		UpdateStartupHideTargetBox();
		TabsFillBox.IsChecked = Settings.TabsFillWidth;
		TabAlignmentBox.ItemsSource = Enum.GetValues<TabAlignment>().Select(a => a.DisplayName()).ToList();
		TabAlignmentBox.SelectedIndex = (int)Settings.TabAlignment;
		TabAlignmentCard.IsEnabled = !Settings.TabsFillWidth;
		SwitchTabOnHoverBox.IsChecked = Settings.SwitchTabOnHover;
		TabHoverDelaySlider.Value = Settings.TabHoverDelay;
		CollapseDelaySlider.Value = Settings.CollapseDelay;
		DrawToCreateBox.IsChecked = Settings.DrawToCreate;
		DesktopMenuBox.IsChecked = Settings.DesktopContextMenu;
		ExpandOnHoverBox.IsChecked = Settings.ExpandOnHover;
		SnapBox.IsChecked = Settings.SnapToEdges;
		SnapGridBox.IsChecked = Settings.SnapToGrid;
		ShowHiddenBox.IsChecked = Settings.ShowHiddenFiles;
		AutoOrganizeBox.IsChecked = Settings.AutoOrganize;
		TextShadowBox.IsChecked = Settings.TextShadow;
		BackgroundBox.ItemsSource = Enum.GetValues<BackgroundEffect>().Select(b => b.DisplayName()).ToList();
		BackgroundBox.SelectedIndex = (int)Settings.DefaultBackground;
		BorderBox.IsChecked = Settings.DefaultBorder;
		LoadFonts();
		FenceFontSizeSlider.Value = Settings.FenceFontSize;
		DesktopFontSizeSlider.Value = Settings.DesktopFontSize;
		ShortcutArrowBox.IsChecked = Settings.ShowShortcutArrows;
		ShowIconNamesBox.IsChecked = Settings.ShowIconNames;
		HardwareAccelerationBox.IsChecked = Settings.HardwareAcceleration;
		FlightAnimationBox.IsChecked = Settings.FlightAnimation;
		AutoUpdateBox.IsChecked = Settings.AutoCheckUpdates;
		OpacitySlider.Value = Math.Round(Settings.DefaultOpacity * 100);
		RadiusSlider.Value = Settings.CornerRadius;
		RadiusChoiceBox.ItemsSource = RadiusChoices.Select(c => c.Name).ToList();
		BlurRadiusSlider.Value = Settings.BlurRadius;
		UpdateBlurControls();
		SnapGapSlider.Value = Settings.SnapGap;
		IconSizeBox.ItemsSource = Enum.GetValues<IconSizeMode>().Select(s => s.DisplayName()).ToList();
		IconSizeBox.SelectedIndex = (int)Settings.DefaultIconSize;
		BuildColorSwatches();
		UpdateColorPreview();
		var version = typeof(App).Assembly.GetName().Version;
		VersionText.Text = $"版本 {version?.ToString(3)}";
		DataDirText.Text = AppPaths.DataDir;
		TipsText.Text = string.Join("\n", Tips);
		UpdateSliderTexts();
		// 快捷键被其他程序占用时，打开设置就能看到提示
		ApplyHotkey();
		_loading = false;
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
	}

	protected override void OnClosed(EventArgs e)
	{
		SaveRules();
		base.OnClosed(e);
	}

	IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		// 录入快捷键时按下 Alt+某键，系统会接着发 WM_SYSCHAR 弹出窗口菜单或发出提示音，这里吞掉
		if (_capturingHotkey && msg == NativeMethods.WM_SYSCHAR)
		{
			handled = true;
		}
		return IntPtr.Zero;
	}

	void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// InitializeComponent 期间导航先于页面创建，此时页面还是 null
		if (GeneralPage == null)
		{
			return;
		}
		var pages = new[] { GeneralPage, DesktopPage, FencesPage, AppearancePage, RulesPage, BackupPage, AboutPage };
		for (int i = 0; i < pages.Length; i++)
		{
			pages[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
		}
		// 设置窗口开着时也可能自动备份（如删除分区），每次切过来都重新列出
		if (BackupPage.Visibility == Visibility.Visible)
		{
			LoadBackups();
		}
	}

	#region 常规

	void AutoStartBox_Click(object sender, RoutedEventArgs e)
	{
		if (!_manager.SetAutoStart(AutoStartBox.IsChecked == true))
		{
			AutoStartBox.IsChecked = AutoStart.IsEnabled();
		}
	}

	string HotkeyText() => string.IsNullOrEmpty(Settings.SearchHotkey) ? "未设置" : Settings.SearchHotkey;

	void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		_capturingHotkey = true;
		_manager.SuspendSearchHotkey();
		HotkeyBox.Text = "请按下组合键";
	}

	/// <summary>
	/// 录完（或点了别处）重新注册快捷键，被其他程序占用时在下面提示。
	/// </summary>
	void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		_capturingHotkey = false;
		HotkeyBox.Text = HotkeyText();
		ApplyHotkey();
	}

	void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		// 单按 Tab 照常切换焦点，免得困在录入框里
		if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
		{
			return;
		}
		e.Handled = true;
		if (key == Key.Escape)
		{
			EndHotkeyCapture();
			return;
		}
		var hotkey = new Hotkey(Keyboard.Modifiers, key);
		if (!hotkey.IsValid)
		{
			// 只按了修饰键，或少了 Ctrl、Alt、Win：先显示已按下的修饰键，等用户按全
			HotkeyBox.Text = Keyboard.Modifiers == ModifierKeys.None ? "请按下组合键" : Hotkey.ModifiersText(Keyboard.Modifiers);
			return;
		}
		Settings.SearchHotkey = hotkey.ToString();
		_manager.SaveSoon();
		EndHotkeyCapture();
	}

	void ClearHotkey_Click(object sender, RoutedEventArgs e)
	{
		Settings.SearchHotkey = string.Empty;
		_manager.SaveSoon();
		HotkeyBox.Text = HotkeyText();
		ApplyHotkey();
	}

	/// <summary>
	/// 把焦点从录入框移开，触发重新注册。
	/// </summary>
	void EndHotkeyCapture() => Keyboard.Focus(NavList);

	void ApplyHotkey()
	{
		bool ok = _manager.ApplySearchHotkey();
		HotkeyStatus.Text = $"{Settings.SearchHotkey} 已被其他程序占用，请换一个组合键";
		HotkeyStatus.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
	}

	void Setting_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		bool showHidden = ShowHiddenBox.IsChecked == true;
		bool textShadow = TextShadowBox.IsChecked == true;
		bool hiddenChanged = Settings.ShowHiddenFiles != showHidden;
		bool shadowChanged = Settings.TextShadow != textShadow;
		bool border = BorderBox.IsChecked == true;
		bool borderChanged = Settings.DefaultBorder != border;
		bool arrows = ShortcutArrowBox.IsChecked == true;
		bool arrowsChanged = Settings.ShowShortcutArrows != arrows;
		bool desktopMenu = DesktopMenuBox.IsChecked == true;
		bool desktopMenuChanged = Settings.DesktopContextMenu != desktopMenu;
		bool names = ShowIconNamesBox.IsChecked == true;
		bool namesChanged = Settings.ShowIconNames != names;
		bool hardware = HardwareAccelerationBox.IsChecked == true;
		bool hardwareChanged = Settings.HardwareAcceleration != hardware;
		Settings.FlightAnimation = FlightAnimationBox.IsChecked == true;
		Settings.DoubleClickToHide = DoubleClickBox.IsChecked == true;
		Settings.DrawToCreate = DrawToCreateBox.IsChecked == true;
		Settings.ExpandOnHover = ExpandOnHoverBox.IsChecked == true;
		Settings.SnapToEdges = SnapBox.IsChecked == true;
		Settings.SnapToGrid = SnapGridBox.IsChecked == true;
		Settings.AutoOrganize = AutoOrganizeBox.IsChecked == true;
		Settings.ShowHiddenFiles = showHidden;
		Settings.TextShadow = textShadow;
		Settings.DefaultBorder = border;
		Settings.ShowShortcutArrows = arrows;
		Settings.DesktopContextMenu = desktopMenu;
		Settings.ShowIconNames = names;
		Settings.HardwareAcceleration = hardware;
		Settings.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
		_manager.ApplyMouseHookSettings();
		_manager.Organizer.ApplyWatchSetting();
		if (desktopMenuChanged)
		{
			DesktopMenu.Apply(desktopMenu);
		}
		if (hiddenChanged)
		{
			_manager.RefreshAllItems();
		}
		if (shadowChanged || borderChanged)
		{
			_manager.RefreshAllAppearance();
		}
		if (arrowsChanged)
		{
			_manager.ApplyShortcutArrows();
		}
		if (namesChanged)
		{
			_manager.RefreshAllViewMode();
		}
		if (hardwareChanged)
		{
			FenceManager.ApplyRenderMode(hardware);
		}
		_manager.SaveSoon();
	}

	void DoubleClickTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || DoubleClickTargetBox.SelectedIndex < 0)
		{
			return;
		}
		_manager.SetDoubleClickTarget((HideTarget)DoubleClickTargetBox.SelectedIndex);
	}

	/// <summary>
	/// 在托盘菜单或桌面右键菜单里改了「双击桌面隐藏」时同步下拉框。
	/// </summary>
	public void RefreshDoubleClickTarget()
	{
		_loading = true;
		DoubleClickTargetBox.SelectedIndex = (int)Settings.DoubleClickTarget;
		_loading = false;
	}

	void StartupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || StartupVisibilityBox.SelectedIndex < 0 || StartupHideTargetBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.StartupVisibility = (StartupVisibility)StartupVisibilityBox.SelectedIndex;
		Settings.StartupHideTarget = (HideTarget)StartupHideTargetBox.SelectedIndex;
		UpdateStartupHideTargetBox();
		_manager.SaveSoon();
	}

	/// <summary>
	/// 隐藏哪些只在启动时「隐藏」时可选；「和上次退出时一样」按上次实际隐藏着的来。
	/// </summary>
	void UpdateStartupHideTargetBox() => StartupHideTargetBox.IsEnabled = Settings.StartupVisibility == StartupVisibility.Hide;

	void TabSetting_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		Settings.TabsFillWidth = TabsFillBox.IsChecked == true;
		Settings.SwitchTabOnHover = SwitchTabOnHoverBox.IsChecked == true;
		TabAlignmentCard.IsEnabled = !Settings.TabsFillWidth;
		_manager.RefreshAllTabs();
		_manager.SaveSoon();
	}

	void TabAlignmentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || TabAlignmentBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.TabAlignment = (TabAlignment)TabAlignmentBox.SelectedIndex;
		_manager.RefreshAllTabs();
		_manager.SaveSoon();
	}

	void CollapseDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || CollapseDelayText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.CollapseDelay = (int)CollapseDelaySlider.Value;
		_manager.ApplyCollapseDelay();
		_manager.SaveSoon();
	}

	void TabHoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || TabHoverDelayText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.TabHoverDelay = (int)TabHoverDelaySlider.Value;
		_manager.SaveSoon();
	}

	void NewFence_Click(object sender, RoutedEventArgs e) => _manager.CreateFence(editTitle: true);

	void NewPortal_Click(object sender, RoutedEventArgs e) => _manager.CreatePortalFence();

	void Organize_Click(object sender, RoutedEventArgs e)
	{
		SaveRules();
		_manager.Organizer.OrganizeInteractive();
	}

	#endregion

	#region 外观

	void BuildColorSwatches()
	{
		ColorPanel.Children.Clear();
		foreach (var (name, hex) in Appearance.ColorPresets)
		{
			var swatch = new Border
			{
				Width = 26,
				Height = 26,
				CornerRadius = new CornerRadius(4),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
				Background = new SolidColorBrush(Appearance.ParseColor(hex, Colors.Black)),
			};
			var button = new Button
			{
				Content = swatch,
				Padding = new Thickness(4),
				Margin = new Thickness(0, 0, 8, 8),
				ToolTip = name,
			};
			button.Click += (_, _) => SetDefaultColor(hex);
			ColorPanel.Children.Add(button);
		}
	}

	void SetDefaultColor(string hex)
	{
		Settings.DefaultColor = hex;
		UpdateColorPreview();
		_manager.RefreshAllAppearance();
		_manager.SaveSoon();
	}

	void UpdateColorPreview()
	{
		var color = Appearance.ParseColor(Settings.DefaultColor, Colors.Black);
		ColorPreview.Background = new SolidColorBrush(color);
		ColorBox.Text = Appearance.ToHex(color);
	}

	void ColorBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			ApplyColorText();
		}
	}

	void ColorBox_LostFocus(object sender, RoutedEventArgs e) => ApplyColorText();

	void ApplyColorText()
	{
		var text = ColorBox.Text.Trim();
		if (!text.StartsWith('#'))
		{
			text = "#" + text;
		}
		if (Appearance.TryParseColor(text, out var color))
		{
			SetDefaultColor(Appearance.ToHex(color));
		}
		else
		{
			UpdateColorPreview();
		}
	}

	void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		// 构造过程中控件可能尚未全部创建
		if (_loading || OpacityText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.DefaultOpacity = OpacitySlider.Value / 100.0;
		_manager.RefreshAllAppearance();
		_manager.SaveSoon();
	}

	void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || RadiusText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.CornerRadius = RadiusSlider.Value;
		_manager.RefreshAllAppearance();
		_manager.SaveSoon();
	}

	/// <summary>
	/// 系统亚克力的三档圆角（DWM 窗口圆角）：名称和对应的圆角大小。
	/// </summary>
	static readonly (string Name, double Radius)[] RadiusChoices = [("直角", 0), ("小圆角", 4), ("圆角", 8)];

	void RadiusChoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || RadiusChoiceBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.CornerRadius = RadiusChoices[RadiusChoiceBox.SelectedIndex].Radius;
		_loading = true;
		RadiusSlider.Value = Settings.CornerRadius;
		_loading = false;
		UpdateSliderTexts();
		_manager.RefreshAllAppearance();
		_manager.SaveSoon();
	}

	void BlurRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || BlurRadiusText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.BlurRadius = BlurRadiusSlider.Value;
		_manager.Wallpaper.BlurRadius = Settings.BlurRadius;
		_manager.Wallpaper.Refresh();
		_manager.SaveSoon();
	}

	void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || BackgroundBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.DefaultBackground = (BackgroundEffect)BackgroundBox.SelectedIndex;
		UpdateBlurControls();
		_manager.RefreshAllAppearance();
		_manager.SaveSoon();
	}

	/// <summary>
	/// 背景效果的说明跟着选项换；系统亚克力时圆角改成三选一；不是毛玻璃时模糊程度拉条变灰。
	/// </summary>
	void UpdateBlurControls()
	{
		BackgroundHint.Text = Settings.DefaultBackground switch
		{
			BackgroundEffect.Translucent => "只有背景颜色，后面的壁纸原样透出来",
			BackgroundEffect.Blur => "分区后面的壁纸模糊后透出来，不透明度越低越通透；用动态壁纸或视频壁纸时自动改为半透明",
			BackgroundEffect.Acrylic => "由系统实时模糊，动态壁纸、视频壁纸也能透出来；用的是系统未公开的功能，可能有显示问题，以后的 Windows 版本也可能失效；"
					+ "圆角只有直角、小圆角、圆角三档。系统关掉透明效果或开着节电模式时没有模糊",
		};
		bool acrylic = Settings.DefaultBackground == BackgroundEffect.Acrylic;
		RadiusSliderPanel.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
		RadiusChoicePanel.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
		bool loading = _loading;
		_loading = true;
		RadiusChoiceBox.SelectedIndex = Settings.CornerRadius <= 0 ? 0 : Settings.CornerRadius <= 4 ? 1 : 2;
		_loading = loading;
		BlurRadiusCard.IsEnabled = Settings.DefaultBackground == BackgroundEffect.Blur;
		BlurRadiusHint.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
	}

	void SnapGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || SnapGapText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.SnapGap = SnapGapSlider.Value;
		_manager.SaveSoon();
	}

	// 字体下拉框各项对应的字体族名称，第一项「系统默认」为 null
	List<string?> _fontSources = [];

	/// <summary>
	/// 列出本机已安装的字体，按中文名称（没有时用英文名）排序。
	/// </summary>
	void LoadFonts()
	{
		var zh = XmlLanguage.GetLanguage("zh-cn");
		var en = XmlLanguage.GetLanguage("en-us");
		var fonts = Fonts.SystemFontFamilies
				.Select(f => (Source: f.Source, Name: f.FamilyNames.TryGetValue(zh, out var name) || f.FamilyNames.TryGetValue(en, out name) ? name : f.Source))
				.DistinctBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
				.OrderBy(f => f.Name, StringComparer.CurrentCulture)
				.ToList();
		_fontSources = [null, .. fonts.Select(f => (string?)f.Source)];
		var names = new[] { "系统默认" }.Concat(fonts.Select(f => f.Name)).ToList();
		FenceFontBox.ItemsSource = names;
		DesktopFontBox.ItemsSource = names;
		FenceFontBox.SelectedIndex = FontIndex(Settings.FenceFontFamily);
		DesktopFontBox.SelectedIndex = FontIndex(Settings.DesktopFontFamily);
	}

	int FontIndex(string? family) => Math.Max(0, _fontSources.FindIndex(s => string.Equals(s, family, StringComparison.OrdinalIgnoreCase)));

	void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || FenceFontBox.SelectedIndex < 0 || DesktopFontBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.FenceFontFamily = _fontSources[FenceFontBox.SelectedIndex];
		Settings.DesktopFontFamily = _fontSources[DesktopFontBox.SelectedIndex];
		_manager.ApplyFonts();
		_manager.SaveSoon();
	}

	void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loading || FenceFontSizeText == null || DesktopFontSizeText == null)
		{
			return;
		}
		UpdateSliderTexts();
		Settings.FenceFontSize = FenceFontSizeSlider.Value;
		Settings.DesktopFontSize = DesktopFontSizeSlider.Value;
		_manager.ApplyFonts();
		_manager.SaveSoon();
	}

	void UpdateSliderTexts()
	{
		FenceFontSizeText.Text = $"{FenceFontSizeSlider.Value:0}";
		DesktopFontSizeText.Text = $"{DesktopFontSizeSlider.Value:0}";
		OpacityText.Text = $"{OpacitySlider.Value:0}%";
		RadiusText.Text = $"{RadiusSlider.Value:0}";
		BlurRadiusText.Text = $"{BlurRadiusSlider.Value:0}";
		SnapGapText.Text = SnapGapSlider.Value == 0 ? "紧贴" : $"{SnapGapSlider.Value:0}";
		TabHoverDelayText.Text = TabHoverDelaySlider.Value == 0 ? "立即" : $"{TabHoverDelaySlider.Value:0} 毫秒";
		CollapseDelayText.Text = $"{CollapseDelaySlider.Value:0} 毫秒";
	}

	void IconSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || IconSizeBox.SelectedIndex < 0)
		{
			return;
		}
		Settings.DefaultIconSize = (IconSizeMode)IconSizeBox.SelectedIndex;
		_manager.RefreshAllViewMode();
		_manager.SaveSoon();
	}

	void ResetAppearance_Click(object sender, RoutedEventArgs e)
	{
		if (MessageDialog.Show("重置外观", "所有分区单独设置的颜色、不透明度、毛玻璃和图标大小都将清除，改用默认外观。", "全部重置", "取消") == 0)
		{
			_manager.ResetAllAppearance();
		}
	}

	/// <summary>
	/// 「常规」「桌面」「分区」「外观」各页底部的「恢复默认」：这一页的设置改回新装时的默认值（取自新建的 AppSettings），
	/// 照各项改动时的做法生效，再重新载入界面。「外观」页只恢复默认外观，不动各分区单独的设置（那是「全部重置」）。
	/// </summary>
	void ResetPage_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string page }
				|| MessageDialog.Show("恢复默认", $"把「{page}」页的设置恢复成默认值吗？", "恢复默认", "取消") != 0)
		{
			return;
		}
		var defaults = new AppSettings();
		switch (page)
		{
			case "常规":
				_manager.SetAutoStart(true);
				Settings.SearchHotkey = defaults.SearchHotkey;
				Settings.FlightAnimation = defaults.FlightAnimation;
				if (Settings.HardwareAcceleration != defaults.HardwareAcceleration)
				{
					Settings.HardwareAcceleration = defaults.HardwareAcceleration;
					FenceManager.ApplyRenderMode(defaults.HardwareAcceleration);
				}
				break;
			case "桌面":
				Settings.DoubleClickToHide = defaults.DoubleClickToHide;
				Settings.StartupVisibility = defaults.StartupVisibility;
				Settings.StartupHideTarget = defaults.StartupHideTarget;
				Settings.DrawToCreate = defaults.DrawToCreate;
				if (Settings.DesktopContextMenu != defaults.DesktopContextMenu)
				{
					Settings.DesktopContextMenu = defaults.DesktopContextMenu;
					DesktopMenu.Apply(defaults.DesktopContextMenu);
				}
				_manager.SetDoubleClickTarget(defaults.DoubleClickTarget);
				_manager.ApplyMouseHookSettings();
				break;
			case "分区":
				bool hiddenChanged = Settings.ShowHiddenFiles != defaults.ShowHiddenFiles;
				Settings.ExpandOnHover = defaults.ExpandOnHover;
				Settings.CollapseDelay = defaults.CollapseDelay;
				Settings.TabsFillWidth = defaults.TabsFillWidth;
				Settings.TabAlignment = defaults.TabAlignment;
				Settings.SwitchTabOnHover = defaults.SwitchTabOnHover;
				Settings.TabHoverDelay = defaults.TabHoverDelay;
				Settings.SnapToEdges = defaults.SnapToEdges;
				Settings.SnapToGrid = defaults.SnapToGrid;
				Settings.SnapGap = defaults.SnapGap;
				Settings.ShowHiddenFiles = defaults.ShowHiddenFiles;
				_manager.ApplyCollapseDelay();
				_manager.RefreshAllTabs();
				if (hiddenChanged)
				{
					_manager.RefreshAllItems();
				}
				break;
			case "外观":
				bool arrowsChanged = Settings.ShowShortcutArrows != defaults.ShowShortcutArrows;
				Settings.DefaultColor = defaults.DefaultColor;
				Settings.DefaultOpacity = defaults.DefaultOpacity;
				Settings.DefaultBackground = defaults.DefaultBackground;
				Settings.BlurRadius = defaults.BlurRadius;
				_manager.Wallpaper.BlurRadius = defaults.BlurRadius;
				_manager.Wallpaper.Refresh();
				Settings.DefaultBorder = defaults.DefaultBorder;
				Settings.CornerRadius = defaults.CornerRadius;
				Settings.DefaultIconSize = defaults.DefaultIconSize;
				Settings.FenceFontFamily = defaults.FenceFontFamily;
				Settings.FenceFontSize = defaults.FenceFontSize;
				Settings.DesktopFontFamily = defaults.DesktopFontFamily;
				Settings.DesktopFontSize = defaults.DesktopFontSize;
				Settings.ShowIconNames = defaults.ShowIconNames;
				Settings.TextShadow = defaults.TextShadow;
				Settings.ShowShortcutArrows = defaults.ShowShortcutArrows;
				_manager.ApplyFonts();
				_manager.RefreshAllAppearance();
				_manager.RefreshAllViewMode();
				if (arrowsChanged)
				{
					_manager.ApplyShortcutArrows();
				}
				break;
		}
		_manager.SaveSoon();
		LoadValues();
	}

	#endregion

	#region 整理规则

	void Rule_LostFocus(object sender, RoutedEventArgs e) => SaveRules();

	void SaveRules()
	{
		Settings.Rules = _rules.ToList();
		_manager.SaveSoon();
	}

	void AddRule_Click(object sender, RoutedEventArgs e)
	{
		var rule = new OrganizeRule { Name = "新规则", Extensions = ".ext" };
		// 新规则放在兜底规则之前，否则永远匹配不到
		int fallbackIndex = _rules.ToList().FindIndex(r => r.IsFallback);
		if (fallbackIndex >= 0)
		{
			_rules.Insert(fallbackIndex, rule);
		}
		else
		{
			_rules.Add(rule);
		}
		SaveRules();
	}

	void DeleteRule_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is OrganizeRule rule)
		{
			_rules.Remove(rule);
			SaveRules();
		}
	}

	void ResetRules_Click(object sender, RoutedEventArgs e)
	{
		if (MessageDialog.Show("恢复默认规则", "当前规则将被替换为默认规则，确定吗？", "恢复默认", "取消") != 0)
		{
			return;
		}
		_rules.Clear();
		foreach (var rule in OrganizeRule.CreateDefaults())
		{
			_rules.Add(rule);
		}
		SaveRules();
	}

	#endregion

	#region 备份

	void LoadBackups()
	{
		var backups = SettingsBackup.List();
		BackupList.ItemsSource = backups;
		NoBackupText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	void BackupNow_Click(object sender, RoutedEventArgs e)
	{
		SaveRules();
		SettingsBackup.Create(Settings, BackupReason.Manual);
		LoadBackups();
	}

	void ExportSettings_Click(object sender, RoutedEventArgs e)
	{
		SaveRules();
		var dialog = new SaveFileDialog
		{
			Title = "导出当前配置",
			FileName = $"MyDesktop 配置 {DateTime.Now:yyyy-MM-dd}.json",
			Filter = "MyDesktop 配置 (*.json)|*.json",
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			SettingsStore.SaveTo(Settings, dialog.FileName);
		}
		catch (Exception ex)
		{
			Log.Warn($"导出配置失败：{dialog.FileName}", ex);
			MessageDialog.Show("导出当前配置", $"导出失败：{ex.Message}", "确定");
		}
	}

	void ImportSettings_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog
		{
			Title = "从文件恢复配置",
			Filter = "MyDesktop 配置 (*.json)|*.json",
		};
		if (dialog.ShowDialog(this) == true)
		{
			_manager.RestoreSettings(dialog.FileName);
		}
	}

	void RestoreBackup_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is BackupEntry entry)
		{
			_manager.RestoreSettings(entry.File);
		}
	}

	void DeleteBackup_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is BackupEntry entry)
		{
			SettingsBackup.Delete(entry);
			LoadBackups();
		}
	}

	#endregion

	void OpenDataDir_Click(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.DataDir);

	void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = _manager.Updater.CheckAsync(true);

	void Feedback_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo(IssuesPage) { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			Log.Warn("打开反馈页面失败", ex);
		}
	}

	static void OpenInExplorer(string folder)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			Log.Warn($"打开文件夹失败：{folder}", ex);
		}
	}
}
