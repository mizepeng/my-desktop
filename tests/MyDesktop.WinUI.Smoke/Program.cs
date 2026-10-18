using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.Verification;
using static MyDesktop.Native.NativeMethods;

internal static class Program
{
	static readonly WindowProc FloorProc = FloorMessage;
	static nint _floor, _app, _taskbar;

	[STAThread]
	static int Main(string[] args)
	{
		if (args is ["--floor", var readyName])
		{
			return RunFloor(readyName);
		}
		if (args.Length != 2)
		{
			Console.Error.WriteLine("用法：MyDesktop.WinUI.Smoke <WinUI exe 绝对路径> <报告 log 绝对路径>");
			return 1;
		}
		var desktopName = $"MyDesktopVerification-{Environment.ProcessId}";
		var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x1FF, 0);
		if (desktop == 0)
		{
			throw new Win32Exception();
		}
		var floor = default(ProcessInfo);
		var app = default(ProcessInfo);
		try
		{
			using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, desktopName);
			floor = Launch(Environment.ProcessPath!, $"--floor {desktopName}", desktopName);
			if (!ready.WaitOne(10000))
			{
				throw new TimeoutException("隔离桌面的底层窗口没有就绪");
			}
			var reportPath = Path.GetFullPath(args[1]);
			Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
			if (File.Exists(reportPath))
			{
				File.Delete(reportPath);
			}
			var data = CreateFixture(Path.GetDirectoryName(reportPath)!);
			app = Launch(Path.GetFullPath(args[0]), $"--probe --data \"{data}\" --report \"{reportPath}\"", desktopName);
			if (WaitForSingleObject(app.Process, 30000) != 0)
			{
				throw new TimeoutException("WinUI 验证进程运行超过 30 秒");
			}
			if (!GetExitCodeProcess(app.Process, out int code))
			{
				throw new Win32Exception();
			}
			Console.WriteLine(File.Exists(reportPath) ? File.ReadAllText(reportPath) : "WinUI 验证进程没有生成报告，启动失败。");
			Console.WriteLine($"WinUI 验证退出码：{code}");
			return File.Exists(reportPath) ? code : 1;
		}
		finally
		{
			foreach (var process in new[] { app, floor })
			{
				if (process.Process != 0)
				{
					if (WaitForSingleObject(process.Process, 0) != 0)
					{
						TerminateProcess(process.Process, 1);
						WaitForSingleObject(process.Process, 5000);
					}
					CloseHandle(process.Thread);
					CloseHandle(process.Process);
				}
			}
			CloseDesktop(desktop);
		}
	}

	static string CreateFixture(string reportDirectory)
	{
		var directory = Path.Combine(reportDirectory, "data-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "工作.txt"), "真实文件图标验证");
		var portal = Path.Combine(directory, "映射");
		Directory.CreateDirectory(portal);
		File.WriteAllText(Path.Combine(portal, "资料一.txt"), "资料一");
		File.WriteAllText(Path.Combine(portal, "资料二.txt"), "资料二");
		var (area, scale) = DesktopFixtureProtocol.PrimaryArea();
		var first = new FenceSettings
		{
			Title = "工作", X = area.Left + (int)(100 * scale), Y = area.Top + (int)(100 * scale),
			Width = (int)(400 * scale), Height = (int)(300 * scale), LayoutDpi = (int)(96 * scale),
			Members = [Path.Combine(directory, "工作.txt")],
		};
		var second = new FenceSettings { Title = "资料", IsPortal = true, FolderPath = portal };
		var third = new FenceSettings
		{
			Title = "其他", X = area.Left + (int)(600 * scale), Y = area.Top + (int)(100 * scale),
			Width = (int)(400 * scale), Height = (int)(300 * scale), LayoutDpi = (int)(96 * scale),
		};
		var settings = new AppSettings
		{
			Fences = [first, second, third],
			Groups = [new FenceGroup { Members = [first.Id, second.Id], Active = first.Id }],
		};
		SettingsStore.SaveTo(settings, Path.Combine(directory, "settings.json"));
		return directory;
	}

	static ProcessInfo Launch(string path, string arguments, string desktop)
	{
		var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = desktop };
		var command = new StringBuilder($"\"{path}\" {arguments}");
		if (!CreateProcess(path, command, 0, 0, false, 0x08000000, 0, Path.GetDirectoryName(path), ref startup, out var process))
		{
			throw new Win32Exception();
		}
		return process;
	}

	static int RunFloor(string readyName)
	{
		foreach (var name in new[] { DesktopFixtureProtocol.FloorClass, DesktopFixtureProtocol.ControlClass, "SHELLDLL_DefView" })
		{
			var windowClass = new WNDCLASS { lpfnWndProc = FloorProc, hInstance = GetModuleHandle(null), lpszClassName = name };
			if (RegisterClass(ref windowClass) == 0)
			{
				throw new Win32Exception();
			}
		}
		var control = CreateWindowEx(0, DesktopFixtureProtocol.ControlClass, "隔离桌面测试控制器", (uint)WS_POPUP, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
		if (control == 0)
		{
			throw new Win32Exception();
		}
		CreateFloor();
		using var ready = EventWaitHandle.OpenExisting(readyName);
		ready.Set();
		while (DesktopFixtureProtocol.GetMessage(out var message, 0, 0, 0) > 0)
		{
			DesktopFixtureProtocol.TranslateMessage(message);
			DesktopFixtureProtocol.DispatchMessage(message);
		}
		return 0;
	}

	static nint FloorMessage(nint hwnd, uint message, nint wParam, nint lParam)
	{
		if (message == DesktopFixtureProtocol.DesktopProbeMessage)
		{
			return (DesktopProbeCommand)wParam.ToInt32() switch
			{
				DesktopProbeCommand.OpenApplication => OpenApplication(),
				DesktopProbeCommand.RaiseDesktop => MoveFloor(HWND_TOP),
				DesktopProbeCommand.LowerDesktop => MoveFloor(HWND_BOTTOM),
				DesktopProbeCommand.RemoveDesktop => RemoveFloor(),
				DesktopProbeCommand.RecreateDesktop => CreateFloor(),
				DesktopProbeCommand.CloseApplication => CloseApplication()
			};
		}
		return DefWindowProc(hwnd, message, wParam, lParam);
	}

	static nint CreateFloor()
	{
		var (area, _) = DesktopFixtureProtocol.PrimaryArea();
		_floor = CreateWindowEx(0, DesktopFixtureProtocol.FloorClass, "跨进程底层测试窗口", (uint)WS_POPUP,
			area.Left, area.Top, area.Width, area.Height, 0, 0, GetModuleHandle(null), 0);
		if (_floor == 0 || CreateWindowEx(0, "SHELLDLL_DefView", "隔离桌面图标视图", 0x50000000,
			0, 0, area.Width, area.Height, _floor, 0, GetModuleHandle(null), 0) == 0)
		{
			throw new Win32Exception();
		}
		DesktopFixtureProtocol.ShowWindow(_floor, SW_SHOWNA);
		return MoveFloor(HWND_BOTTOM);
	}

	static nint MoveFloor(nint after)
	{
		if (!SetWindowPos(_floor, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
		{
			throw new Win32Exception();
		}
		return _floor;
	}

	static nint RemoveFloor()
	{
		if (!DestroyWindow(_floor))
		{
			throw new Win32Exception();
		}
		_floor = 0;
		return 1;
	}

	static nint OpenApplication()
	{
		var (area, _) = DesktopFixtureProtocol.PrimaryArea();
		const uint style = (uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
		_app = CreateWindowEx(style, "Static", "隔离普通应用", (uint)WS_POPUP,
			area.Left, area.Top, area.Width, area.Height, 0, 0, GetModuleHandle(null), 0);
		_taskbar = CreateWindowEx(style, "Static", "隔离置顶窗口", (uint)WS_POPUP,
			area.Right - 30, area.Bottom - 30, 30, 30, 0, 0, GetModuleHandle(null), 0);
		if (_app == 0 || _taskbar == 0)
		{
			throw new Win32Exception();
		}
		DesktopFixtureProtocol.ShowWindow(_app, SW_SHOWNA);
		if (!SetWindowPos(_taskbar, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
		{
			throw new Win32Exception();
		}
		return _app;
	}

	static nint CloseApplication()
	{
		if (!DestroyWindow(_app) || !DestroyWindow(_taskbar))
		{
			throw new Win32Exception();
		}
		_app = _taskbar = 0;
		return 1;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
	[DllImport("user32.dll")]
	static extern bool CloseDesktop(nint desktop);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool CreateProcess(string path, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit,
		uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
	[DllImport("kernel32.dll")]
	static extern uint WaitForSingleObject(nint handle, uint milliseconds);
	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetExitCodeProcess(nint process, out int code);
	[DllImport("kernel32.dll")]
	static extern bool TerminateProcess(nint process, uint code);
	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(nint handle);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct StartupInfo
	{
		public int Size;
		public string? Reserved, Desktop, Title;
		public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
		public short ShowWindow, ReservedSize;
		public nint ReservedData, Input, Output, Error;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct ProcessInfo
	{
		public nint Process, Thread;
		public int ProcessId, ThreadId;
	}
}
