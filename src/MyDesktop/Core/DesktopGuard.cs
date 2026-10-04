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
		return Process.Start(new ProcessStartInfo(exe, $"{Argument} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
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
		try
		{
			using var process = Process.GetProcessById(processId);
			process.WaitForExit();
		}
		catch (ArgumentException)
		{
			// 主程序已经退出
		}
		if (!DesktopHost.IconsHiddenBySystem())
		{
			DesktopHost.SetIconsVisible(true);
		}
	}
}
