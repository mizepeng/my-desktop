using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyDesktop.Native;

namespace MyDesktop.Views;

/// <summary>
/// 和资源管理器一样：单击已经选中的图标的名称，过了双击的时间还没有第二下，就开始改名。分区和桌面图标层共用。
/// </summary>
internal sealed class ClickToRename
{
	readonly DispatcherTimer _timer;
	FenceItem? _pressed;
	FenceItem? _pending;

	/// <param name="beginRename">开始改名。</param>
	/// <param name="canStart">计时结束时还能不能开始改名：所在窗口仍在前台、这个图标仍是唯一选中的。</param>
	public ClickToRename(Action<FenceItem> beginRename, Func<FenceItem, bool> canStart)
	{
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime()) };
		_timer.Tick += (_, _) =>
		{
			_timer.Stop();
			var item = _pending;
			_pending = null;
			if (item != null && !item.IsRenaming && Mouse.LeftButton == MouseButtonState.Released && canStart(item))
			{
				beginRename(item);
			}
		};
	}

	/// <summary>
	/// 左键按下：先取消还在等的改名；第一下按在唯一选中的那个图标的名称上时记下它，等抬起。
	/// </summary>
	/// <param name="soleSelected">按下前这个图标是不是唯一选中的（分区和桌面一起算）。</param>
	public void Press(MouseButtonEventArgs e, FenceItem? item, bool soleSelected)
	{
		Cancel();
		if (item != null && soleSelected && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && IsName(e.OriginalSource, item))
		{
			_pressed = item;
		}
	}

	/// <summary>
	/// 左键抬起：抬起时按着的还是这个图标（中间没有拖动）才开始计时。
	/// </summary>
	public void Release(FenceItem? item)
	{
		if (item is { CanRename: true } && item == _pressed)
		{
			_pending = item;
			_timer.Start();
		}
		_pressed = null;
	}

	public void Cancel()
	{
		_timer.Stop();
		_pressed = null;
		_pending = null;
	}

	/// <summary>
	/// 按在图标名称的文字上（图标视图选中后展开的完整名称也算）；按在图标图像或格子空白处不算。
	/// </summary>
	static bool IsName(object source, FenceItem item) => source is TextBlock { DataContext: FenceItem owner } text && owner == item && text.Text == item.DisplayName;
}
