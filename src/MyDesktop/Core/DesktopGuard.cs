using System.Diagnostics;

namespace MyDesktop.Core;

/// <summary>
/// 守护进程：接管桌面图标期间随主程序启动，等主程序退出后恢复资源管理器的桌面图标。
/// 主程序正常退出时会先结束守护进程，只有主程序被强制结束（任务管理器等）时才由它恢复，桌面图标不会一直消失。
/// </summary>
internal static class DesktopGuard
{
	public const string Argument = "--guard";

	public static Process? Start()
	{
		var exe = Environment.ProcessPath;
		if (exe == null)
		{
			return null;
		}
		var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
		info.ArgumentList.Add(Argument);
		info.ArgumentList.Add(Environment.ProcessId.ToString());
		// 主程序意外退出时守护进程要把经过写进同一份日志
		info.ArgumentList.Add(AppPaths.DataDir);
		return Process.Start(info);
	}

	/// <summary>
	/// 守护进程的入口，不加载 WPF。
	/// </summary>
	public static void Run(string[] args)
	{
		int index = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
		if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
		{
			return;
		}
		string? dataDir = index + 2 < args.Length ? args[index + 2] : null;
		int? exitCode = null;
		try
		{
			using var process = Process.GetProcessById(processId);
			process.WaitForExit();
			exitCode = process.ExitCode;
		}
		catch (ArgumentException)
		{
			// 主程序已经退出
		}
		bool restore = !DesktopHost.IconsHiddenBySystem();
		if (restore)
		{
			DesktopHost.SetIconsVisible(true);
		}
		// 主程序正常退出前会先结束守护进程，走到这里说明它是崩溃或被强制结束的：等它退出后再打开日志，不和它抢写
		if (dataDir != null)
		{
			Log.Init(dataDir);
			string code = exitCode is int value ? $"0x{value:X8}" : "未知（守护进程启动时它已经退出）";
			Log.Warn($"主程序（进程 {processId}）意外退出，退出码 {code}；{(restore ? "已恢复资源管理器的桌面图标" : "系统设置为不显示桌面图标，未恢复")}");
		}
	}
}
