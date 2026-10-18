using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

/// <summary>
/// 接收安装程序与正式版本共用的退出消息，先恢复桌面图标并保存配置，再释放实例。
/// </summary>
internal sealed class InstanceCommands : IDisposable
{
	readonly nint _window;
	readonly WindowApi.SubclassProc _procedure;

	public InstanceCommands(Action exit)
	{
		int commandMessage = RegisterWindowMessage(AppCommands.MessageName);
		if (commandMessage == 0) { throw new Win32Exception(); }
		var dispatcher = DispatcherQueue.GetForCurrentThread();
		_procedure = (hwnd, message, command, parameter, id, data) =>
		{
			if (message == commandMessage && command == (nint)AppCommand.Exit)
			{
				dispatcher.TryEnqueue(() => exit());
				return 0;
			}
			return WindowApi.DefSubclassProc(hwnd, message, command, parameter);
		};
		// 使用隐藏的顶层窗口接收广播，欢迎页和分区宿主的生命周期都不会影响安装退出。
		_window = CreateWindowEx((uint)WS_EX_TOOLWINDOW, "Static", "MyDesktop.WinUI.Command", (uint)WS_POPUP, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
		if (_window == 0) { throw new Win32Exception(); }
		if (!WindowApi.SetWindowSubclass(_window, _procedure, 1, 0))
		{
			var error = new Win32Exception();
			DestroyWindow(_window);
			throw error;
		}
	}

	public static void ExitRunning(string? dataDirectory)
	{
		if (dataDirectory != null) { AppPaths.UseDataDir(Path.GetFullPath(dataDirectory)); }
		AppCommands.Broadcast(AppCommand.Exit);
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToUpperInvariant())))[..16];
		using var mutex = new Mutex(false, $"Local\\MyDesktop-{hash}");
		bool acquired;
		try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
		catch (AbandonedMutexException) { acquired = true; }
		if (!acquired) { throw new TimeoutException("等待 MyDesktop 正常退出超时，未能确认桌面图标已经恢复。"); }
		mutex.ReleaseMutex();
	}

	public void Dispose()
	{
		WindowApi.RemoveWindowSubclass(_window, _procedure, 1);
		DestroyWindow(_window);
	}
}
