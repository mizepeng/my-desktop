using Microsoft.Win32;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 监视一个注册表键下的值的增删改，变化时在后台线程触发 Changed。
/// </summary>
internal sealed class RegistryWatcher : IDisposable
{
	readonly RegistryKey _key;
	readonly ManualResetEvent _stop = new(false);
	readonly Thread _thread;

	/// <param name="key">要监视的键，由本对象负责释放。</param>
	public RegistryWatcher(RegistryKey key)
	{
		_key = key;
		_thread = new Thread(Run) { IsBackground = true, Name = "RegistryWatcher" };
		_thread.Start();
	}

	public event Action? Changed;

	void Run()
	{
		using var changed = new AutoResetEvent(false);
		WaitHandle[] handles = [changed, _stop];
		// 异步通知在注册它的线程退出时失效，所以每次触发后都在本线程重新注册
		while (RegNotifyChangeKeyValue(_key.Handle, false, REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true) == 0
				&& WaitHandle.WaitAny(handles) == 0)
		{
			Changed?.Invoke();
		}
	}

	public void Dispose()
	{
		_stop.Set();
		_thread.Join();
		_key.Dispose();
		_stop.Dispose();
	}
}
