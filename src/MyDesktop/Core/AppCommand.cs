using MyDesktop.Models;
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
	DoubleClickHidesAll = 4,
	DoubleClickHidesIcons = 5,
	DoubleClickHidesFences = 6,
	// 安装程序升级、卸载前让正在运行的实例正常退出
	Exit = 7,
	NewPortalFence = 8,
}

internal static class AppCommands
{
	/// <summary>
	/// 已有实例在运行时，新进程广播此消息（wParam 为命令值）把命令转交给它。
	/// </summary>
	public const string MessageName = "MyDesktop.Command";

	/// <summary>
	/// 桌面右键菜单服务向主程序查询状态（SendMessage 到托盘窗口），返回值由 EncodeState 生成。
	/// </summary>
	public const string StateMessageName = "MyDesktop.State";

	const int StateValid = 0x100;
	const int StateDoubleClickEnabled = 0x10;
	const int StateTargetMask = 0xF;

	/// <summary>
	/// 编码菜单需要的状态：双击隐藏的对象、是否启用双击隐藏；带有效位，区分「查询失败」返回的 0。
	/// </summary>
	public static int EncodeState(HideTarget target, bool doubleClickEnabled)
	{
		return StateValid | (doubleClickEnabled ? StateDoubleClickEnabled : 0) | (int)target;
	}

	public static bool TryDecodeState(int value, out HideTarget target, out bool doubleClickEnabled)
	{
		target = (HideTarget)(value & StateTargetMask);
		doubleClickEnabled = (value & StateDoubleClickEnabled) != 0;
		return (value & StateValid) != 0 && Enum.IsDefined(target);
	}

	public static string ToArgument(this AppCommand command)
	{
		return command switch
		{
			AppCommand.ShowSettings => "settings",
			AppCommand.NewFence => "new-fence",
			AppCommand.Organize => "organize",
			AppCommand.ToggleHidden => "toggle",
			AppCommand.DoubleClickHidesAll => "double-click-all",
			AppCommand.DoubleClickHidesIcons => "double-click-icons",
			AppCommand.DoubleClickHidesFences => "double-click-fences",
			AppCommand.Exit => "exit",
			AppCommand.NewPortalFence => "new-portal-fence",
		};
	}

	/// <summary>
	/// 「双击桌面隐藏」选择某个对象对应的命令。
	/// </summary>
	public static AppCommand ForDoubleClickTarget(HideTarget target)
	{
		return target switch
		{
			HideTarget.All => AppCommand.DoubleClickHidesAll,
			HideTarget.Icons => AppCommand.DoubleClickHidesIcons,
			HideTarget.Fences => AppCommand.DoubleClickHidesFences,
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
