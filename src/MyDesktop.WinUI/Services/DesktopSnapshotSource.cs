using System.Text.Json;
using Microsoft.UI.Dispatching;
using MyDesktop.Core;
using MyDesktop.WinUI.Native;

namespace MyDesktop.WinUI.Services;

/// <summary>
/// 在带消息循环的独立 STA 线程上读取桌面；测试模式只读本地夹具，不连接真实 Explorer 的 COM 服务。
/// </summary>
internal sealed class DesktopSnapshotSource : IDisposable
{
	readonly DispatcherQueue _ui;
	readonly Windows.System.DispatcherQueueController _worker = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
	readonly bool _fixture;
	readonly string _fixtureFile;
	ExplorerDesktopReader? _reader;
	Windows.System.DispatcherQueueTimer? _timer;
	volatile bool _disposed;
	int _queued;

	public DesktopSnapshotSource(DispatcherQueue ui, bool fixture, string dataDirectory)
	{
		_ui = ui;
		_fixture = fixture;
		_fixtureFile = Path.Combine(dataDirectory, "desktop-snapshot.json");
	}

	public event Action<DesktopSnapshot?, string?>? Observed;

	public void Start()
	{
		_worker.DispatcherQueue.TryEnqueue(() =>
		{
			_reader = _fixture ? null : new ExplorerDesktopReader();
			_timer = _worker.DispatcherQueue.CreateTimer();
			_timer.Interval = TimeSpan.FromSeconds(3);
			_timer.Tick += (_, _) => Read();
			_timer.Start();
			Read();
		});
	}

	public void Refresh()
	{
		if (!_disposed && Interlocked.Exchange(ref _queued, 1) == 0)
		{
			_worker.DispatcherQueue.TryEnqueue(Read);
		}
	}

	void Read()
	{
		Interlocked.Exchange(ref _queued, 0);
		if (_disposed)
		{
			return;
		}
		DesktopSnapshot? snapshot = null;
		string? error = null;
		try
		{
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
			{
				throw new InvalidOperationException("Explorer 桌面读取线程必须是 STA。");
			}
			if (_fixture)
			{
				if (DesktopIconTarget.Find(true) != null)
				{
					snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(_fixtureFile))
						?? throw new InvalidDataException("测试桌面快照为空。");
				}
			}
			else
			{
				snapshot = _reader!.Read();
			}
		}
		catch (Exception ex)
		{
			_reader?.Reset();
			error = ex.Message;
			Log.Error("读取桌面快照失败", ex);
		}
		_ui.TryEnqueue(() =>
		{
			if (!_disposed)
			{
				Observed?.Invoke(snapshot, error);
			}
		});
	}

	public Task ClearSelectionAsync()
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_worker.DispatcherQueue.TryEnqueue(() =>
		{
			try
			{
				_reader?.ClearSelection();
				completion.SetResult();
			}
			catch (Exception ex)
			{
				completion.SetException(ex);
			}
		}))
		{
			completion.SetException(new InvalidOperationException("桌面读取线程已关闭。"));
		}
		return completion.Task;
	}

	public void Dispose()
	{
		_disposed = true;
		_worker.DispatcherQueue.TryEnqueue(() =>
		{
			_timer?.Stop();
			_reader?.Dispose();
		});
		_ = _worker.ShutdownQueueAsync();
	}
}
