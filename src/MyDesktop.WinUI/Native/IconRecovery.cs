using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MyDesktop.WinUI.Native;

/// <summary>
/// 隐藏图标前先启动恢复进程并等它就绪；恢复目标始终绑定到明确的桌面窗口和所属进程。
/// </summary>
internal sealed class IconRecovery : IDisposable
{
	readonly EventWaitHandle _stop;
	readonly Process _process;
	bool _disposed;

	IconRecovery(EventWaitHandle stop, Process process)
	{
		_stop = stop;
		_process = process;
	}

	public bool IsAlive => !_disposed && !_process.HasExited;
	internal int ProcessId => _process.Id;

	public static async Task<IconRecovery> ArmAsync(DesktopIconTarget target)
	{
		string token = "Local\\MyDesktop-WinUI-Recovery-" + Guid.NewGuid().ToString("N");
		using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
		var stop = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-stop");
		Process process;
		try
		{
			string arguments = string.Join(' ', "--guard", Environment.ProcessId, target.Root.ToInt64(), target.List.ToInt64(), target.ProcessId, target.Fixture ? 1 : 0, token);
			process = StartOnCurrentDesktop(arguments);
		}
		catch
		{
			stop.Dispose();
			throw;
		}
		var recovery = new IconRecovery(stop, process);
		if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(5))) || process.HasExited)
		{
			recovery.Dispose();
			throw new InvalidOperationException("桌面图标恢复进程未就绪，已保留系统图标。");
		}
		return recovery;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_stop.Set();
		_stop.Dispose();
		_process.Dispose();
	}

	public static int Run(string[] args)
	{
		if (args is not ["--guard", var parentText, var rootText, var listText, var processText, var fixtureText, var token])
		{
			throw new ArgumentException("图标恢复进程缺少明确的恢复目标。");
		}
		var target = new DesktopIconTarget((nint)long.Parse(rootText, CultureInfo.InvariantCulture), (nint)long.Parse(listText, CultureInfo.InvariantCulture),
			uint.Parse(processText, CultureInfo.InvariantCulture), fixtureText switch
			{
				"1" => true,
				"0" => false,
				_ => throw new ArgumentException("恢复进程的桌面模式无效。"),
			});
		if (!target.IsCurrent || target.Fixture && DesktopInput.IsInputDesktop())
		{
			throw new InvalidOperationException("恢复目标已经失效或不在隔离测试桌面。");
		}
		using var ready = EventWaitHandle.OpenExisting(token + "-ready");
		using var stop = EventWaitHandle.OpenExisting(token + "-stop");
		Process? parent = null;
		try
		{
			parent = Process.GetProcessById(int.Parse(parentText, CultureInfo.InvariantCulture));
		}
		catch (ArgumentException)
		{
			// 主进程可能在恢复进程刚启动时就退出，仍需恢复已绑定的目标。
		}
		using (parent)
		{
			ready.Set();
			if (parent != null)
			{
				using var exited = new ProcessExitWait(parent);
				WaitHandle.WaitAny([exited, stop]);
			}
		}
		// 即使当前仍可见也要排入恢复请求，隐藏请求可能还在 Explorer 的队列里。
		// Explorer 暂时无响应时保留恢复进程重试；窗口重建或用户主动隐藏图标时停止。
		while (target.IsCurrent && !target.HiddenBySystem)
		{
			target.Show(true);
			if (target.Visible)
			{
				break;
			}
			Thread.Sleep(100);
		}
		return 0;
	}

	static Process StartOnCurrentDesktop(string arguments)
	{
		string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定桌面图标恢复程序的路径。");
		var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = DesktopInput.CurrentDesktopName };
		var command = new StringBuilder('"' + executable + "\" " + arguments);
		if (!CreateProcess(executable, command, 0, 0, false, 0x08000000, 0, null, ref startup, out var process))
		{
			throw new Win32Exception();
		}
		try
		{
			return Process.GetProcessById((int)process.ProcessId);
		}
		finally
		{
			CloseHandle(process.Thread);
			CloseHandle(process.Process);
		}
	}

	sealed class ProcessExitWait : WaitHandle
	{
		public ProcessExitWait(Process process) => SafeWaitHandle = new SafeWaitHandle(process.Handle, false);
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct StartupInfo
	{
		public int Size;
		public string? Reserved, Desktop, Title;
		public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
		public short ShowWindow, ReservedSize;
		public nint ReservedPointer, Input, Output, Error;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct ProcessInfo
	{
		public nint Process, Thread;
		public uint ProcessId, ThreadId;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool CreateProcess(string path, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit,
		uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(nint handle);
}
