using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Views;

/// <summary>
/// 新建智能分区（自定义条件）或修改智能分区的名称、来源文件夹和筛选条件。
/// </summary>
internal partial class SmartFenceDialog : Window
{
	/// <summary>
	/// 点了确定后的结果。
	/// </summary>
	public sealed record Result(string Title, string Folder, SmartFilter Filter);

	Result? _result;

	SmartFenceDialog()
	{
		InitializeComponent();
	}

	/// <summary>
	/// 显示对话框，取消时返回 null。
	/// </summary>
	/// <param name="isNew">新建（按钮是「创建」）还是修改已有的分区。</param>
	public static Result? Show(string title, string folder, SmartFilter filter, bool isNew)
	{
		var dialog = new SmartFenceDialog { Title = isNew ? "新建智能分区" : "修改智能分区" };
		dialog.HeaderText.Text = dialog.Title;
		dialog.OkButton.Content = isNew ? "创建" : "确定";
		dialog.TitleBox.Text = title;
		dialog.FolderBox.Text = folder;
		dialog.DaysBox.Text = filter.ModifiedWithinDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
		dialog.ExtensionsBox.Text = filter.Extensions;
		dialog.NameBox.Text = filter.NamePatterns;
		dialog.MinSizeBox.Text = filter.MinSizeMB?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
		dialog.MaxSizeBox.Text = filter.MaxSizeMB?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
		dialog.FilterSubfoldersBox.IsChecked = filter.FilterSubfolders;
		dialog.Loaded += (_, _) =>
		{
			dialog.TitleBox.Focus();
			dialog.TitleBox.SelectAll();
		};
		dialog.ShowDialog();
		return dialog._result;
	}

	void Browse_Click(object sender, RoutedEventArgs e)
	{
		var picker = new OpenFolderDialog { Title = "选择智能分区的来源文件夹" };
		if (Directory.Exists(FolderBox.Text))
		{
			picker.InitialDirectory = FolderBox.Text;
		}
		if (picker.ShowDialog(this) == true)
		{
			FolderBox.Text = picker.FolderName;
		}
	}

	void Ok_Click(object sender, RoutedEventArgs e)
	{
		var error = Validate(out var result);
		if (error != null)
		{
			ErrorText.Text = error;
			ErrorText.Visibility = Visibility.Visible;
			return;
		}
		_result = result;
		DialogResult = true;
	}

	/// <summary>
	/// 检查输入，有误时返回说明（显示在按钮上方，不关闭对话框）。
	/// </summary>
	string? Validate(out Result? result)
	{
		result = null;
		var title = TitleBox.Text.Trim();
		if (title.Length == 0)
		{
			return "请填写分区名称。";
		}
		var folder = FolderBox.Text.Trim();
		if (!Directory.Exists(folder))
		{
			return $"来源文件夹不存在：{folder}";
		}
		if (PathUtil.AreEqual(folder, AppPaths.Desktop) || PathUtil.AreEqual(folder, AppPaths.CommonDesktop))
		{
			return "桌面上的图标已经由分区和桌面显示，来源文件夹不能选桌面。";
		}
		int? days = null;
		if (DaysBox.Text.Trim() is { Length: > 0 } daysText)
		{
			if (!int.TryParse(daysText, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
			{
				return "修改时间的天数要填正整数，不限就留空。";
			}
			days = value;
		}
		if (!TryParseSize(MinSizeBox.Text, out var min) || !TryParseSize(MaxSizeBox.Text, out var max))
		{
			return "大小要填不小于 0 的数字（单位 MB），不限就留空。";
		}
		if (min is double low && max is double high && low >= high)
		{
			return "「大于」要比「小于」的值小，否则没有文件符合。";
		}
		var filter = new SmartFilter
		{
			ModifiedWithinDays = days,
			Extensions = ExtensionsBox.Text.Trim(),
			NamePatterns = NameBox.Text.Trim(),
			MinSizeMB = min,
			MaxSizeMB = max,
			FilterSubfolders = FilterSubfoldersBox.IsChecked == true,
		};
		result = new Result(title, folder, filter);
		return null;
	}

	static bool TryParseSize(string text, out double? size)
	{
		size = null;
		text = text.Trim();
		if (text.Length == 0)
		{
			return true;
		}
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
		{
			return false;
		}
		size = value;
		return true;
	}
}
