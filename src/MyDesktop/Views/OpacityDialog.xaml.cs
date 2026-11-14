using System.Windows;

namespace MyDesktop.Views;

/// <summary>
/// 单个分区的不透明度：0～100% 的滑块，拖动时实时预览；非模态，调整时分区照常能拖动。
/// </summary>
internal partial class OpacityDialog : Window
{
	readonly Action<double>? _preview;
	bool _accepted;

	OpacityDialog(double initial, Action<double> preview)
	{
		InitializeComponent();
		ValueSlider.Value = Math.Round(Math.Clamp(initial, 0, 1) * 100);
		UpdateText();
		// 设好初始值以后才开始预览，打开对话框本身不改动分区
		_preview = preview;
		Loaded += (_, _) => ValueSlider.Focus();
	}

	/// <summary>
	/// 打开对话框（非模态：开着时分区照常能拖动、调大小），拖动滑块时以 0～1 的不透明度调用 preview；
	/// 对话框关闭时调用 closed，点「确定」传选定的值，取消或关闭窗口传 null。不设 owner，绝不能挂到资源管理器的窗口上。
	/// </summary>
	public static OpacityDialog Open(double initial, Action<double> preview, Action<double?> closed)
	{
		var dialog = new OpacityDialog(initial, preview);
		dialog.Closed += (_, _) => closed(dialog._accepted ? dialog.ValueSlider.Value / 100.0 : null);
		dialog.Show();
		dialog.Activate();
		return dialog;
	}

	void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		// 构造过程中控件可能尚未全部创建
		if (ValueText == null)
		{
			return;
		}
		UpdateText();
		_preview?.Invoke(ValueSlider.Value / 100.0);
	}

	void UpdateText() => ValueText.Text = $"{ValueSlider.Value:0}%";

	// 非模态窗口不会因 IsCancel 自动关闭，Esc 和「取消」都在这里关
	void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

	void OkButton_Click(object sender, RoutedEventArgs e)
	{
		_accepted = true;
		Close();
	}
}
