using System.Windows.Threading;
using MyDesktop.Native;

namespace MyDesktop.Core;

/// <summary>
/// 跨进程读取资源管理器的桌面视图（IShellWindows → IShellBrowser → IFolderView2），并把图标位置写回。
/// 散放图标的排列、顺序、位置、间距和图标大小都以它为准，在桌面右键菜单「查看」「排序方式」里的改动也就自然跟随。
/// 资源管理器的 COM 代理只能在创建它的线程上使用，资源管理器无响应时调用会一直等待，
/// 所以全部放在独立的 STA 线程上；界面线程只拿到读取完成的快照，不会被拖住。
/// </summary>
internal sealed class ExplorerDesktopView : IDisposable
{
	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

	readonly Dispatcher _ui;
	readonly Dispatcher _worker;
	readonly ExplorerDesktopReader _reader = new();
	string? _lastSignature;
	int _refreshQueued;

	public ExplorerDesktopView(Dispatcher ui)
	{
		_ui = ui;
		Dispatcher? worker = null;
		using var ready = new ManualResetEventSlim();
		var thread = new Thread(() =>
		{
			worker = Dispatcher.CurrentDispatcher;
			ready.Set();
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "ExplorerDesktopView",
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		ready.Wait();
		_worker = worker!;
		// 兜底轮询：资源管理器的有些变化（比如在「查看」里改图标大小）没有能直接收到的通知
		_worker.BeginInvoke(() => new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Read(), _worker));
	}

	/// <summary>
	/// 读取到与上次不同的快照时触发，在界面线程上执行。
	/// </summary>
	public event Action<DesktopSnapshot>? Changed;

	/// <summary>
	/// 请求重新读取；排队中的多次请求合并为一次。
	/// </summary>
	public void Refresh()
	{
		if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
		{
			return;
		}
		_worker.BeginInvoke(() =>
		{
			Interlocked.Exchange(ref _refreshQueued, 0);
			Read();
		});
	}

	/// <summary>
	/// 把图标移到资源管理器列表坐标中的位置（只改位置，不选中、不抢焦点），完成后重新读取。
	/// </summary>
	public void Move(IReadOnlyList<string> keys, IReadOnlyList<POINT> points)
	{
		var keyArray = keys.ToArray();
		var pointArray = points.ToArray();
		_worker.BeginInvoke(() =>
		{
			_reader.Move(keyArray, pointArray);
			Read();
		});
	}

	/// <summary>
	/// 查询资源管理器桌面视图当前的焦点项目，结果在界面线程回调。
	/// </summary>
	public void QueryFocused(Action<string?> callback)
	{
		_worker.BeginInvoke(() =>
		{
			string? key = null;
			try
			{
				key = _reader.QueryFocused();
			}
			catch (Exception ex)
			{
				Log.Warn("读取资源管理器焦点项目失败", ex);
				_reader.Reset();
			}
			_ui.BeginInvoke(() => callback(key));
		});
	}

	/// <summary>
	/// 取消资源管理器隐藏视图里的选择。键盘钩子之外再加一道保险：即使有按键绕过钩子直接送到资源管理器，
	/// 也没有看不见的选中项可供删除。
	/// </summary>
	public void ClearSelection()
	{
		_worker.BeginInvoke(() =>
		{
			try
			{
				_reader.ClearSelection();
			}
			catch (Exception ex)
			{
				Log.Warn("取消资源管理器的选择失败", ex);
				_reader.Reset();
			}
		});
	}

	public void Dispose() => _worker.BeginInvoke(() =>
	{
		_reader.Dispose();
		_worker.BeginInvokeShutdown(DispatcherPriority.Normal);
	});

	void Read()
	{
		DesktopSnapshot? snapshot;
		try
		{
			snapshot = _reader.Read();
		}
		catch (Exception ex)
		{
			// 资源管理器重启后旧代理全部失效，下次读取时重新获取；工作线程上的异常不能抛出，否则整个进程会退出
			Log.Warn("读取资源管理器桌面视图失败", ex);
			_reader.Reset();
			return;
		}
		if (snapshot == null || snapshot.Signature == _lastSignature)
		{
			return;
		}
		_lastSignature = snapshot.Signature;
		_ui.BeginInvoke(() => Changed?.Invoke(snapshot));
	}

}
