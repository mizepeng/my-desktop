using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 通过低级鼠标钩子检测「双击桌面空白处」。
/// 钩子运行在独立线程上：界面线程偶尔忙碌时也不会拖慢全系统的鼠标响应。
/// </summary>
internal sealed class DesktopDoubleClickWatcher : IDisposable
{
	readonly LowLevelMouseProc _callback;
	readonly Dispatcher _uiDispatcher;
	Thread? _thread;
	Dispatcher? _hookDispatcher;
	IntPtr _hook;
	uint _lastTime;
	POINT _lastPoint;
	bool _checking;

	public event Action? DoubleClicked;

	public DesktopDoubleClickWatcher(Dispatcher uiDispatcher)
	{
		_uiDispatcher = uiDispatcher;
		// 委托必须由字段持有，否则被 GC 回收后钩子回调会崩溃
		_callback = HookCallback;
	}

	public void Start()
	{
		if (_thread != null)
		{
			return;
		}
		using var ready = new ManualResetEventSlim();
		_thread = new Thread(() =>
		{
			_hookDispatcher = Dispatcher.CurrentDispatcher;
			_hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(null), 0);
			if (_hook == IntPtr.Zero)
			{
				Log.Warn($"安装鼠标钩子失败：{Marshal.GetLastWin32Error()}");
			}
			ready.Set();
			// 低级钩子要求安装线程持续泵送消息
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "DesktopMouseHook",
		};
		_thread.Start();
		ready.Wait();
	}

	public void Stop()
	{
		var dispatcher = _hookDispatcher;
		if (dispatcher == null)
		{
			return;
		}
		dispatcher.Invoke(() =>
		{
			if (_hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_hook);
				_hook = IntPtr.Zero;
			}
		});
		dispatcher.InvokeShutdown();
		_thread?.Join(1000);
		_thread = null;
		_hookDispatcher = null;
	}

	public void Dispose() => Stop();

	IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
	{
		// 钩子回调超时会被系统摘除，这里只做时间与距离判断，其余检测异步进行；
		// 回调由系统直接调用，异常必须就地捕获，否则会终止进程
		try
		{
			if (code >= 0 && (int)wParam == WM_LBUTTONDOWN)
			{
				var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				uint elapsed = unchecked(info.time - _lastTime);
				bool near = Math.Abs(info.pt.X - _lastPoint.X) * 2 <= GetSystemMetrics(SM_CXDOUBLECLK)
						&& Math.Abs(info.pt.Y - _lastPoint.Y) * 2 <= GetSystemMetrics(SM_CYDOUBLECLK);
				if (_lastTime != 0 && elapsed <= GetDoubleClickTime() && near)
				{
					_lastTime = 0;
					var point = info.pt;
					_uiDispatcher.BeginInvoke(() => OnDoubleClick(point));
				}
				else
				{
					_lastTime = info.time;
					_lastPoint = info.pt;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("鼠标钩子处理失败", ex);
		}
		return CallNextHookEx(_hook, code, wParam, lParam);
	}

	async void OnDoubleClick(POINT point)
	{
		if (_checking)
		{
			return;
		}
		_checking = true;
		try
		{
			// WindowFromPoint 会向目标窗口发消息，UI Automation 也是跨进程调用，都放到后台线程，Explorer 无响应时不拖住界面
			if (await Task.Run(() => IsBlankDesktopArea(point)))
			{
				DoubleClicked?.Invoke();
			}
		}
		catch (Exception ex)
		{
			Log.Warn("处理桌面双击失败", ex);
		}
		finally
		{
			_checking = false;
		}
	}

	static bool IsBlankDesktopArea(POINT point)
	{
		if (!DesktopHost.IsDesktopSurface(WindowFromPoint(point)))
		{
			return false;
		}
		// 图标可见时要排除「双击图标打开文件」的情况
		return !DesktopHost.AreIconsVisible() || !IsOverDesktopIcon(point);
	}

	static bool IsOverDesktopIcon(POINT point)
	{
		try
		{
			var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
			var walker = TreeWalker.RawViewWalker;
			for (int depth = 0; element != null && depth < 4; depth++)
			{
				var type = element.Current.ControlType;
				if (type == ControlType.ListItem)
				{
					return true;
				}
				if (type == ControlType.List || type == ControlType.Pane || type == ControlType.Window)
				{
					return false;
				}
				element = walker.GetParent(element);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("UI Automation 检测桌面图标失败", ex);
		}
		return false;
	}
}
