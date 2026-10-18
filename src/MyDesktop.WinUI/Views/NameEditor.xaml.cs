using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyDesktop.Core;
using Windows.System;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Views;

public sealed partial class NameEditor : UserControl
{
	Action<string>? _commit;
	bool _committing, _compositionEnded;
	int _version;

	public NameEditor() => InitializeComponent();

	internal bool IsEditing => Visibility == Visibility.Visible;
	internal bool IsComposing { get; private set; }
	internal TextBox NameInput => Input;
	internal string ErrorText => Error.Text;
	internal event Action? Opened;
	internal event Action? Closed;
	internal event Func<bool>? ActivationRequested;
	internal event Action? CompositionFinished;

	internal void Begin(string caption, string name, Action<string> commit)
	{
		Cancel();
		int version = ++_version;
		_commit = commit;
		Caption.Text = caption;
		Input.Text = name;
		Error.Text = "";
		IsComposing = _compositionEnded = false;
		Visibility = Visibility.Visible;
		Opened?.Invoke();
		// 等右键菜单关闭后再请求焦点；取消或切换编辑后，排队请求必须失效。
		DispatcherQueue.TryEnqueue(() =>
		{
			if (!IsEditing || version != _version)
			{
				return;
			}
			var activate = ActivationRequested ?? throw new InvalidOperationException("改名输入框尚未连接到桌面宿主。");
			bool activated = activate();
			UpdateLayout();
			if (!activated || !Input.Focus(FocusState.Programmatic))
			{
				Error.Text = "请点击输入框开始输入。";
			}
			Input.SelectAll();
		});
	}

	internal bool Commit()
	{
		if (!IsEditing || IsComposing || _committing)
		{
			return false;
		}
		_committing = true;
		try
		{
			(_commit ?? throw new InvalidOperationException("改名操作尚未设置。"))(Input.Text);
		}
		catch (Exception ex)
		{
			Error.Text = ex.Message;
			Log.Error("重命名失败", ex);
			return false;
		}
		finally
		{
			_committing = false;
		}
		Cancel();
		return true;
	}

	internal void Cancel()
	{
		if (!IsEditing || _committing)
		{
			return;
		}
		Visibility = Visibility.Collapsed;
		_version++;
		_commit = null;
		IsComposing = _compositionEnded = false;
		Closed?.Invoke();
	}

	void Confirm_Click(object sender, RoutedEventArgs args) => Commit();
	void Cancel_Click(object sender, RoutedEventArgs args) => Cancel();
	void Name_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => IsComposing = true;

	void Name_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
	{
		IsComposing = false;
		_compositionEnded = GetAsyncKeyState(0x0D) < 0 || GetAsyncKeyState(0x1B) < 0;
		CompositionFinished?.Invoke();
	}

	void Name_KeyDown(object sender, KeyRoutedEventArgs args)
	{
		// 输入法确认候选词和取消组词的按键不能同时结束改名。
		if (IsComposing || _compositionEnded)
		{
			return;
		}
		if (args.Key == VirtualKey.Enter)
		{
			Commit();
			args.Handled = true;
		}
		else if (args.Key == VirtualKey.Escape)
		{
			Cancel();
			args.Handled = true;
		}
	}

	void Name_KeyUp(object sender, KeyRoutedEventArgs args) => _compositionEnded = false;
}
