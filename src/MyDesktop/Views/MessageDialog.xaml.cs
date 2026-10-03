using System.Windows;
using System.Windows.Controls;

namespace MyDesktop.Views;

/// <summary>
/// 可自定义按钮文字的消息对话框（Win11 风格）。
/// </summary>
internal partial class MessageDialog : Window
{
	int _result = -1;

	MessageDialog()
	{
		InitializeComponent();
	}

	/// <summary>
	/// 显示对话框并返回被点击按钮的序号，直接关闭窗口返回 -1。
	/// 第一个按钮是主按钮（回车触发），最后一个按钮响应 Esc。
	/// </summary>
	public static int Show(string title, string message, params string[] buttons)
	{
		var dialog = new MessageDialog { Title = title };
		dialog.HeaderText.Text = title;
		dialog.MessageText.Text = message;
		for (int i = 0; i < buttons.Length; i++)
		{
			int index = i;
			var button = new Button
			{
				Content = buttons[i],
				MinWidth = 96,
				Margin = new Thickness(8, 0, 0, 0),
				IsDefault = i == 0,
				IsCancel = i == buttons.Length - 1,
			};
			if (i == 0)
			{
				button.SetResourceReference(StyleProperty, "AccentButtonStyle");
			}
			button.Click += (_, _) =>
			{
				dialog._result = index;
				dialog.Close();
			};
			dialog.ButtonPanel.Children.Add(button);
		}
		dialog.ShowDialog();
		return dialog._result;
	}
}
