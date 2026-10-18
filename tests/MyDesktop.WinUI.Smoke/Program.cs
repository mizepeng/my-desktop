using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.Verification;
using static MyDesktop.Native.NativeMethods;

internal static class Program
{
	static readonly WindowProc FloorProc = FloorMessage;
	static nint _floor, _app, _taskbar, _iconList;

	[STAThread]
	static int Main(string[] args)
	{
		if (args is ["--recovery", var executable, var dataDirectory, var reportFile, var verificationDesktop])
		{
			try
			{
				var controller = FindWindowEx(0, 0, DesktopFixtureProtocol.ControlClass, null);
				if (controller == 0)
				{
					throw new InvalidOperationException("恢复检查只能在独立测试桌面运行。");
				}
				RunRecoveryChecks(executable, dataDirectory, reportFile, verificationDesktop, controller);
				return 0;
			}
			catch (Exception ex)
			{
				File.AppendAllText(reportFile, "恢复验证失败：" + ex + Environment.NewLine);
				return 1;
			}
		}
		if (args is ["--read-desktop"])
		{
			using var reader = new MyDesktop.Core.ExplorerDesktopReader();
			var snapshot = reader.Read() ?? throw new InvalidOperationException("没有读到 Explorer 桌面视图。");
			Console.WriteLine($"只读快照：{snapshot.Items.Count} 个项目，图标 {snapshot.IconSize}，间距 {snapshot.Spacing.X}×{snapshot.Spacing.Y}，范围 {snapshot.ListBounds}。");
			return 0;
		}
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
		var recovery = default(ProcessInfo);
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
			if (code == 0)
			{
				CloseHandle(app.Thread);
				CloseHandle(app.Process);
				app = default;
				recovery = Launch(Environment.ProcessPath!, $"--recovery \"{Path.GetFullPath(args[0])}\" \"{data}\" \"{reportPath}\" {desktopName}", desktopName);
				if (WaitForSingleObject(recovery.Process, 40000) != 0 || !GetExitCodeProcess(recovery.Process, out code))
				{
					throw new InvalidOperationException("隔离恢复检查没有完成。");
				}
			}
			Console.WriteLine(File.Exists(reportPath) ? File.ReadAllText(reportPath) : "WinUI 验证进程没有生成报告，启动失败。");
			Console.WriteLine($"WinUI 验证退出码：{code}");
			return File.Exists(reportPath) ? code : 1;
		}
		finally
		{
			foreach (var process in new[] { app, recovery, floor })
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

	static void RunRecoveryChecks(string executable, string data, string report, string desktopName, nint controller)
	{
		WaitUntil(() => ProbeCommand(controller, DesktopProbeCommand.IconsVisible) == 1, "正常退出没有恢复测试列表。");
		File.AppendAllText(report, "通过：正常退出恢复隔离桌面的系统图标列表。" + Environment.NewLine);
		RunQueuedVisibilityCheck(executable, report, desktopName, controller);
		foreach (bool hiddenByUser in new[] { false, true })
		{
			string marker = report + (hiddenByUser ? ".hidden-crash.json" : ".crash.json");
			if (File.Exists(marker))
			{
				File.Delete(marker);
			}
			var process = Launch(executable, $"--probe-crash --data \"{data}\" --report \"{marker}\"", desktopName);
			try
			{
				WaitUntil(() => File.Exists(marker) && new FileInfo(marker).Length > 0, "恢复验证进程未就绪。");
				var state = JsonSerializer.Deserialize<RecoveryProbeState>(File.ReadAllText(marker))
					?? throw new InvalidOperationException("恢复验证缺少主进程和恢复进程信息。");
				if (state.ParentProcessId != process.ProcessId || state.ListHandle != ProbeCommand(controller, DesktopProbeCommand.GetIconList).ToInt64()
					|| ProbeCommand(controller, DesktopProbeCommand.IconsVisible) != 2)
				{
					throw new InvalidOperationException("恢复验证没有接管本次测试图标列表。");
				}
				if (hiddenByUser)
				{
					ProbeCommand(controller, DesktopProbeCommand.HideIconsBySystem);
				}
				if (!TerminateProcess(process.Process, 137) || WaitForSingleObject(process.Process, 5000) != 0)
				{
					throw new InvalidOperationException("未能结束本次隔离测试主进程。");
				}
				WaitUntil(() => HasExited(state.GuardProcessId), "恢复进程没有完成恢复并退出。");
				bool visible = ProbeCommand(controller, DesktopProbeCommand.IconsVisible) == 1;
				if (visible == hiddenByUser)
				{
					throw new InvalidOperationException("强制结束后的图标恢复状态不符合用户的显示设置。");
				}
				File.AppendAllText(report, hiddenByUser
					? "通过：主进程强制结束后保留用户主动隐藏图标的选择。" + Environment.NewLine
					: "通过：主进程强制结束后由独立恢复进程恢复测试图标列表。" + Environment.NewLine);
			}
			finally
			{
				if (WaitForSingleObject(process.Process, 0) != 0)
				{
					TerminateProcess(process.Process, 1);
					WaitForSingleObject(process.Process, 5000);
				}
				CloseHandle(process.Thread);
				CloseHandle(process.Process);
				ProbeCommand(controller, DesktopProbeCommand.ShowIconsBySystem);
			}
		}
	}

	static void RunQueuedVisibilityCheck(string executable, string report, string desktopName, nint controller)
	{
		var list = ProbeCommand(controller, DesktopProbeCommand.GetIconList);
		var root = GetAncestor(list, 2);
		GetWindowThreadProcessId(root, out uint owner);
		string token = "Local\\MyDesktop-WinUI-Recovery-" + Guid.NewGuid().ToString("N");
		using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
		using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-stop");
		string queueToken = DesktopFixtureProtocol.QueueEventPrefix + owner;
		using var blocked = new EventWaitHandle(false, EventResetMode.ManualReset, queueToken + "-ready");
		using var resume = new EventWaitHandle(false, EventResetMode.ManualReset, queueToken + "-resume");
		var guard = Launch(executable, $"--guard {Environment.ProcessId} {root.ToInt64()} {list.ToInt64()} {owner} 1 {token}", desktopName);
		try
		{
			if (!ready.WaitOne(5000) || !PostMessage(controller, DesktopFixtureProtocol.DesktopProbeMessage, (nint)DesktopProbeCommand.PauseMessages, 0)
				|| !blocked.WaitOne(3000) || !ShowWindowAsync(list, SW_HIDE) || !IsWindowVisible(list))
			{
				throw new InvalidOperationException("未能模拟尚未执行的图标隐藏请求。");
			}
			stop.Set();
			if (WaitForSingleObject(guard.Process, 3000) != 0 || !GetExitCodeProcess(guard.Process, out int code) || code != 0)
			{
				throw new InvalidOperationException("排队恢复请求的测试进程没有正常退出。");
			}
			resume.Set();
			// 让测试窗口处理已排队的隐藏和恢复请求，再核对最后状态。
			Thread.Sleep(500);
			if (ProbeCommand(controller, DesktopProbeCommand.IconsVisible) != 1)
			{
				throw new InvalidOperationException("恢复进程退出后，先前排队的隐藏请求又把图标藏起来。");
			}
			File.AppendAllText(report, "通过：Explorer 消息队列滞后时，恢复请求仍覆盖先前排队的隐藏请求。" + Environment.NewLine);
		}
		finally
		{
			resume.Set();
			stop.Set();
			if (WaitForSingleObject(guard.Process, 1000) != 0)
			{
				TerminateProcess(guard.Process, 1);
				WaitForSingleObject(guard.Process, 5000);
			}
			CloseHandle(guard.Thread);
			CloseHandle(guard.Process);
			ProbeCommand(controller, DesktopProbeCommand.ShowIconsBySystem);
		}
	}

	static bool HasExited(int id)
	{
		try
		{
			using var process = Process.GetProcessById(id);
			return process.HasExited;
		}
		catch (ArgumentException)
		{
			return true;
		}
	}

	static nint ProbeCommand(nint controller, DesktopProbeCommand command)
	{
		if (SendMessageTimeout(controller, DesktopFixtureProtocol.DesktopProbeMessage, (nint)command, 0, 2, 1000, out var result) == 0 || result == 0)
		{
			throw new InvalidOperationException("隔离恢复控制失败：" + command);
		}
		return result;
	}

	static void WaitUntil(Func<bool> condition, string error)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(12))
			{
				throw new TimeoutException(error);
			}
			Thread.Sleep(20);
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
		File.WriteAllText(Path.Combine(directory, "散放一.txt"), "散放图标一");
		File.WriteAllText(Path.Combine(directory, "散放二.txt"), "散放图标二");
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
		int originY = Math.Max((int)(470 * scale), area.Height / 2);
		var snapshot = new DesktopSnapshot(true, new POINT((int)(96 * scale), (int)(104 * scale)), 48, area,
		[
			new DesktopEntry(Path.Combine(directory, "工作.txt"), "工作", true, false, new POINT((int)(32 * scale), originY)),
			new DesktopEntry(Path.Combine(directory, "散放一.txt"), "散放一", true, false, new POINT((int)(32 * scale), originY + (int)(104 * scale))),
			new DesktopEntry(Path.Combine(directory, "散放二.txt"), "散放二", true, false, new POINT((int)(32 * scale), originY + (int)(208 * scale))),
		]);
		File.WriteAllText(Path.Combine(directory, "desktop-snapshot.json"), JsonSerializer.Serialize(snapshot));
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
				DesktopProbeCommand.CloseApplication => CloseApplication(),
				DesktopProbeCommand.GetIconList => _iconList,
				DesktopProbeCommand.IconsVisible => IsWindowVisible(_iconList) ? 1 : 2,
				DesktopProbeCommand.HideIconsBySystem => SetHiddenIcons(true),
				DesktopProbeCommand.ShowIconsBySystem => SetHiddenIcons(false),
				DesktopProbeCommand.PauseMessages => PauseMessages(),
			};
		}
		return DefWindowProc(hwnd, message, wParam, lParam);
	}

	static nint CreateFloor()
	{
		var (area, _) = DesktopFixtureProtocol.PrimaryArea();
		_floor = CreateWindowEx(0, DesktopFixtureProtocol.FloorClass, "跨进程底层测试窗口", (uint)WS_POPUP,
			area.Left, area.Top, area.Width, area.Height, 0, 0, GetModuleHandle(null), 0);
		var view = CreateWindowEx(0, "SHELLDLL_DefView", "隔离桌面图标视图", 0x50000000,
			0, 0, area.Width, area.Height, _floor, 0, GetModuleHandle(null), 0);
		var controls = new CommonControls { Size = 8, Classes = 1 };
		if (_floor == 0 || view == 0 || !InitCommonControlsEx(ref controls))
		{
			throw new Win32Exception();
		}
		_iconList = CreateWindowEx(0, "SysListView32", "FolderView", 0x50000000,
			0, 0, area.Width, area.Height, view, 0, GetModuleHandle(null), 0);
		if (_iconList == 0)
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

	static nint SetHiddenIcons(bool hidden)
	{
		if (!SetProp(_floor, DesktopFixtureProtocol.HiddenIconsProperty, hidden ? 1 : 0))
		{
			throw new Win32Exception();
		}
		DesktopFixtureProtocol.ShowWindow(_iconList, hidden ? SW_HIDE : SW_SHOWNA);
		return 1;
	}

	static nint PauseMessages()
	{
		string token = DesktopFixtureProtocol.QueueEventPrefix + Environment.ProcessId;
		using var ready = EventWaitHandle.OpenExisting(token + "-ready");
		using var resume = EventWaitHandle.OpenExisting(token + "-resume");
		ready.Set();
		// 使用原生等待，避免 STA 的托管等待在阻塞期间仍分派窗口消息。
		if (WaitForSingleObject(resume.SafeWaitHandle.DangerousGetHandle(), 10000) != 0)
		{
			throw new TimeoutException("隔离桌面的消息队列没有恢复。");
		}
		return 1;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct CommonControls
	{
		public int Size;
		public uint Classes;
	}

	[DllImport("comctl32.dll", SetLastError = true)]
	static extern bool InitCommonControlsEx(ref CommonControls controls);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool SetProp(nint hwnd, string name, nint value);

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
