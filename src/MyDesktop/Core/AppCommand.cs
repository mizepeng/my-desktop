using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 可从命令行（桌面右键菜单）触发的命令；数值用于在进程间传递，不要改动已有值。
/// </summary>
internal enum AppCommand
{
	ShowSettings = 0,
	NewFence = 1,
	Organize = 2,
	ToggleHidden = 3,
}

internal static class AppCommands
{
	/// <summary>
	/// 已有实例在运行时，新进程广播此消息（wParam 为命令值）把命令转交给它。
	/// </summary>
	public const string MessageName = "MyDesktop.Command";

	public static string ToArgument(this AppCommand command)
	{
		return command switch
		{
			AppCommand.ShowSettings => "settings",
			AppCommand.NewFence => "new-fence",
			AppCommand.Organize => "organize",
			AppCommand.ToggleHidden => "toggle",
		};
	}

	/// <summary>
	/// 把命令广播给正在运行的实例，并允许它把窗口带到前台（本进程须有前台权限才能转让）。
	/// </summary>
	public static void Broadcast(AppCommand command)
	{
		AllowSetForegroundWindow(ASFW_ANY);
		PostMessage(HWND_BROADCAST, RegisterWindowMessage(MessageName), new IntPtr((int)command), IntPtr.Zero);
	}

	public static AppCommand? Parse(string? argument)
	{
		foreach (var command in Enum.GetValues<AppCommand>())
		{
			if (string.Equals(command.ToArgument(), argument, StringComparison.OrdinalIgnoreCase))
			{
				return command;
			}
		}
		return null;
	}
}
