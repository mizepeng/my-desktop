using System.Diagnostics;

namespace MyDesktop.Core;

/// <summary>
/// 守护进程：接管桌面图标期间随主程序启动，等主程序退出后恢复资源管理器的桌面图标。
/// 主程序正常退出并完成恢复后才结束守护进程；被强制结束或恢复超时时，由它接着恢复。
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
		var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
		start.ArgumentList.Add(Argument);
		start.ArgumentList.Add(Environment.ProcessId.ToString());
		start.ArgumentList.Add("--data");
		start.ArgumentList.Add(AppPaths.DataDir);
		return Process.Start(start);
	}

	/// <summary>
	/// 守护进程的入口，不加载 WPF 窗口。
	/// </summary>
	public static void Run(string[] args)
	{
		int index = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
		if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
		{
			return;
		}
		int dataIndex = Array.FindIndex(args, a => string.Equals(a, "--data", StringComparison.OrdinalIgnoreCase));
		if (dataIndex >= 0)
		{
			if (dataIndex + 1 >= args.Length)
			{
				throw new ArgumentException("守护进程缺少数据目录。");
			}
			AppPaths.UseDataDir(args[dataIndex + 1]);
		}
		Log.Init(AppPaths.DataDir);
		try
		{
			using var process = Process.GetProcessById(processId);
			process.WaitForExit();
		}
		catch (ArgumentException)
		{
			// 主程序已经退出
		}
		if (DesktopHost.IsWindows10)
		{
			if (!ShellDesktopIcons.Restore(TimeSpan.FromSeconds(10)))
			{
				Environment.ExitCode = 1;
			}
		}
		else
		{
			DesktopHost.RestoreIcons();
		}
	}
}
