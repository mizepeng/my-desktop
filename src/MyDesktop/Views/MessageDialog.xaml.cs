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

	/// <summary>
	/// 显示带进度条和「取消」按钮的对话框，执行 work 直到完成；点取消或关闭窗口会取消它。
	/// 返回是否顺利完成；出错时由 error 带回异常，取消时 error 为空。
	/// </summary>
	public static bool ShowProgress(string title, string message, Func<IProgress<double>, CancellationToken, Task> work, out Exception? error)
	{
		var dialog = new MessageDialog { Title = title };
		dialog.HeaderText.Text = title;
		dialog.MessageText.Text = message;
		dialog.ProgressBar.Visibility = Visibility.Visible;
		using var cancel = new CancellationTokenSource();
		var button = new Button { Content = "取消", MinWidth = 96, IsCancel = true };
		button.Click += (_, _) => dialog.Close();
		dialog.ButtonPanel.Children.Add(button);
		bool completed = false;
		bool closed = false;
		Exception? failure = null;
		// 完成后由这里关闭窗口时不算取消
		dialog.Closing += (_, _) =>
		{
			if (!completed)
			{
				cancel.Cancel();
			}
		};
		dialog.Closed += (_, _) => closed = true;
		dialog.Loaded += async (_, _) =>
		{
			try
			{
				await work(new Progress<double>(value => dialog.ProgressBar.Value = value), cancel.Token);
				completed = true;
			}
			catch (OperationCanceledException) when (cancel.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			if (!closed)
			{
				dialog.Close();
			}
		};
		dialog.ShowDialog();
		error = failure;
		return completed;
	}
}
