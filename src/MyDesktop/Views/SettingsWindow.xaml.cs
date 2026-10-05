using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Models;
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
		"· 在桌面空白处按住右键拖出一个框，松开即在该位置新建分区",
		"· 拖动标题栏移动分区、拖动边缘调整大小（会按整行整列吸附），双击标题栏卷起/展开",
		"· 右键分区空白处或点击标题栏的「⋯」打开分区菜单，可修改颜色、排序、视图等",
		"· 右键文件弹出系统右键菜单；F2 重命名，Delete 删除到回收站",
		"· 双击桌面空白处可以一键隐藏/显示图标和分区；隐藏哪些可以在这里、托盘菜单或桌面右键菜单里选",
		"· 桌面空白处的右键菜单里有 MyDesktop 子菜单",
	];

	readonly FenceManager _manager;
	readonly ObservableCollection<OrganizeRule> _rules;
	bool _loading = true;

	public SettingsWindow(FenceManager manager)
	{
		_manager = manager;
		InitializeComponent();
		_rules = new ObservableCollection<OrganizeRule>(manager.Settings.Rules);
		RulesList.ItemsSource = _rules;
		LoadValues();
	}

	AppSettings Settings => _manager.Settings;

	void LoadValues()
	{
		_loading = true;
		AutoStartBox.IsChecked = AutoStart.IsEnabled();
		DoubleClickBox.IsChecked = Settings.DoubleClickToHide;
		DoubleClickTargetBox.ItemsSource = Enum.GetValues<HideTarget>().Select(t => t.DisplayName()).ToList();
		DoubleClickTargetBox.SelectedIndex = (int)Settings.DoubleClickTarget;
		DrawToCreateBox.IsChecked = Settings.DrawToCreate;
		DesktopMenuBox.IsChecked = Settings.DesktopContextMenu;
		ExpandOnHoverBox.IsChecked = Settings.ExpandOnHover;
		SnapBox.IsChecked = Settings.SnapToEdges;
		SnapGridBox.IsChecked = Settings.SnapToGrid;
		ShowHiddenBox.IsChecked = Settings.ShowHiddenFiles;
		AutoOrganizeBox.IsChecked = Settings.AutoOrganize;
		TextShadowBox.IsChecked = Settings.TextShadow;
		ShortcutArrowBox.IsChecked = Settings.ShowShortcutArrows;
		AutoUpdateBox.IsChecked = Settings.AutoCheckUpdates;
		OpacitySlider.Value = Math.Round(Settings.DefaultOpacity * 100);
		RadiusSlider.Value = Settings.CornerRadius;
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
		_loading = false;
	}

	protected override void OnClosed(EventArgs e)
	{
		SaveRules();
		base.OnClosed(e);
	}

	void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// InitializeComponent 期间导航先于页面创建，此时页面还是 null
		if (GeneralPage == null)
		{
			return;
		}
		var pages = new[] { GeneralPage, AppearancePage, RulesPage, BackupPage, AboutPage };
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
		try
		{
			AutoStart.SetEnabled(AutoStartBox.IsChecked == true);
		}
		catch (Exception ex)
		{
			Log.Warn("设置开机自启失败", ex);
			MessageDialog.Show("开机自动启动", $"设置失败：{ex.Message}", "确定");
			AutoStartBox.IsChecked = AutoStart.IsEnabled();
		}
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
		bool arrows = ShortcutArrowBox.IsChecked == true;
		bool arrowsChanged = Settings.ShowShortcutArrows != arrows;
		bool desktopMenu = DesktopMenuBox.IsChecked == true;
		bool desktopMenuChanged = Settings.DesktopContextMenu != desktopMenu;
		Settings.DoubleClickToHide = DoubleClickBox.IsChecked == true;
		Settings.DrawToCreate = DrawToCreateBox.IsChecked == true;
		Settings.ExpandOnHover = ExpandOnHoverBox.IsChecked == true;
		Settings.SnapToEdges = SnapBox.IsChecked == true;
		Settings.SnapToGrid = SnapGridBox.IsChecked == true;
		Settings.AutoOrganize = AutoOrganizeBox.IsChecked == true;
		Settings.ShowHiddenFiles = showHidden;
		Settings.TextShadow = textShadow;
		Settings.ShowShortcutArrows = arrows;
		Settings.DesktopContextMenu = desktopMenu;
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
		if (shadowChanged)
		{
			_manager.RefreshAllAppearance();
		}
		if (arrowsChanged)
		{
			_manager.ApplyShortcutArrows();
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

	void UpdateSliderTexts()
	{
		OpacityText.Text = $"{OpacitySlider.Value:0}%";
		RadiusText.Text = $"{RadiusSlider.Value:0}";
		SnapGapText.Text = SnapGapSlider.Value == 0 ? "紧贴" : $"{SnapGapSlider.Value:0}";
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
		if (MessageDialog.Show("重置外观", "所有分区单独设置的颜色、不透明度和图标大小都将清除，改用默认外观。", "全部重置", "取消") == 0)
		{
			_manager.ResetAllAppearance();
		}
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
