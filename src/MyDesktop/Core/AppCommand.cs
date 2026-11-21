using System.Runtime.InteropServices;
using MyDesktop.Models;
using MyDesktop.Native;
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
	NewSmartFence = 9,
	// 开关隐私模式
	TogglePrivacy = 10,
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
	const int StatePrivacy = 0x40;
	const int StateDesktopMenu = 0x20;
	const int StateDoubleClickEnabled = 0x10;
	const int StateTargetMask = 0xF;

	/// <summary>
	/// 编码菜单需要的状态：双击隐藏的对象、是否启用双击隐藏、是否在桌面右键菜单中显示、隐私模式是否开着；带有效位，区分「查询失败」返回的 0。
	/// </summary>
	public static int EncodeState(HideTarget target, bool doubleClickEnabled, bool desktopMenu, bool privacy)
	{
		return StateValid | (privacy ? StatePrivacy : 0) | (desktopMenu ? StateDesktopMenu : 0) | (doubleClickEnabled ? StateDoubleClickEnabled : 0) | (int)target;
	}

	public static bool TryDecodeState(int value, out HideTarget target, out bool doubleClickEnabled, out bool desktopMenu, out bool privacy)
	{
		target = (HideTarget)(value & StateTargetMask);
		doubleClickEnabled = (value & StateDoubleClickEnabled) != 0;
		desktopMenu = (value & StateDesktopMenu) != 0;
		privacy = (value & StatePrivacy) != 0;
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
			AppCommand.NewSmartFence => "new-smart-fence",
			AppCommand.TogglePrivacy => "toggle-privacy",
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

	/// <summary>
	/// 把文件夹映射成分区：「发送到」快捷方式和右键菜单「映射到桌面分区」用，后面跟一个或多个文件夹路径。
	/// </summary>
	public const string MapFolderArgument = "--map-folder";

	/// <summary>
	/// WM_COPYDATA 的 dwData：内容是「数据目录\0文件夹1\0文件夹2…」，各实例只处理数据目录和自己相同的。
	/// </summary>
	public const int MapFolderData = 0x4D44_0001;

	/// <summary>
	/// 用文件夹新建智能分区：「发送到」快捷方式和文件夹右键菜单「用这个文件夹新建智能分区」用，后面跟一个或多个文件夹路径。
	/// </summary>
	public const string SmartFolderArgument = "--smart-folder";

	/// <summary>
	/// WM_COPYDATA 的 dwData：用文件夹新建智能分区，内容格式同 MapFolderData。
	/// </summary>
	public const int SmartFolderData = 0x4D44_0002;

	const uint CopyDataTimeoutMs = 2000;

	/// <summary>
	/// 把文件夹交给正在运行的实例（数据目录相同的那个）；有实例接收时返回 true。
	/// 托盘窗口要等实例接管桌面前才建好，刚启动的实例可能还收不到，由调用方重试。
	/// </summary>
	/// <param name="dataId">MapFolderData（映射成分区）或 SmartFolderData（新建智能分区）。</param>
	public static bool SendFolders(int dataId, string dataDir, IReadOnlyList<string> folders)
	{
		var text = string.Join("\0", [dataDir, .. folders]);
		var buffer = Marshal.StringToHGlobalUni(text);
		try
		{
			var data = new COPYDATASTRUCT { dwData = new IntPtr(dataId), cbData = text.Length * 2, lpData = buffer };
			AllowSetForegroundWindow(ASFW_ANY);
			for (var window = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, DesktopMenuServer.MainWindowTitle); window != IntPtr.Zero;
					window = FindWindowEx(IntPtr.Zero, window, null, DesktopMenuServer.MainWindowTitle))
			{
				if (SendCopyDataTimeout(window, WM_COPYDATA, IntPtr.Zero, ref data, SMTO_ABORTIFHUNG, CopyDataTimeoutMs, out var result) != IntPtr.Zero
						&& result != IntPtr.Zero)
				{
					return true;
				}
			}
			return false;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	/// <summary>
	/// 解析 SendFolders 发来的内容；不是发给数据目录 dataDir 的实例时返回 null。
	/// </summary>
	public static List<string>? ParseFolders(string text, string dataDir)
	{
		var parts = text.Split('\0');
		return PathUtil.AreEqual(parts[0], dataDir) ? parts.Skip(1).Where(p => p.Length > 0).ToList() : null;
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
