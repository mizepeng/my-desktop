using Microsoft.Win32;

namespace MyDesktop.Core;

/// <summary>
/// 在桌面右键菜单中提供 MyDesktop 子菜单，优先用已签名的扩展包（见 DesktopMenuPackage），
/// Windows 11 的新式菜单和「显示更多选项」中都会出现；没有扩展包或注册失败时，
/// 退回写当前用户注册表的静态菜单（只出现在「显示更多选项」里，部分预览版 Windows 会忽略）。
/// </summary>
internal static class DesktopMenu
{
	const string KeyPath = @"Software\Classes\DesktopBackground\Shell\MyDesktop";
	const int SeparatorBefore = 0x20;
	static readonly object Gate = new();

	/// <summary>
	/// 在后台线程执行：注册扩展包要调用 PowerShell，耗时一两秒。
	/// </summary>
	public static void Apply(bool enabled)
	{
		Task.Run(() =>
		{
			lock (Gate)
			{
				try
				{
					bool packaged = false;
					if (enabled)
					{
						packaged = DesktopMenuPackage.Register();
					}
					else
					{
						DesktopMenuPackage.Unregister();
					}
					// 两种方式同时存在时「显示更多选项」里会出现两个 MyDesktop
					if (enabled && !packaged)
					{
						Register();
					}
					else
					{
						Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
					}
				}
				catch (Exception ex)
				{
					Log.Warn("更新桌面右键菜单失败", ex);
				}
			}
		});
	}

	static void Register()
	{
		var exe = Environment.ProcessPath;
		if (string.IsNullOrEmpty(exe))
		{
			return;
		}
		var icon = $"\"{exe}\",0";
		using var root = Registry.CurrentUser.CreateSubKey(KeyPath);
		root.SetValue("MUIVerb", "MyDesktop");
		root.SetValue("Icon", icon);
		// 值为空字符串表示子命令定义在下面的 shell 子项里
		root.SetValue("SubCommands", string.Empty);
		using var shell = root.CreateSubKey("shell");
		foreach (var name in shell.GetSubKeyNames())
		{
			shell.DeleteSubKeyTree(name, false);
		}
		// 子项按名称排序显示，用数字前缀固定顺序
		AddItem(shell, "1NewFence", "新建分区", AppCommand.NewFence, exe, 0);
		AddItem(shell, "2Organize", "一键整理桌面…", AppCommand.Organize, exe, 0);
		AddItem(shell, "3Settings", "设置…", AppCommand.ShowSettings, exe, SeparatorBefore);
	}

	static void AddItem(RegistryKey shell, string key, string text, AppCommand command, string exe, int flags)
	{
		using var item = shell.CreateSubKey(key);
		item.SetValue("MUIVerb", text);
		if (flags != 0)
		{
			item.SetValue("CommandFlags", flags, RegistryValueKind.DWord);
		}
		using var commandKey = item.CreateSubKey("command");
		var line = $"\"{exe}\" --command {command.ToArgument()}";
		if (AppPaths.IsCustomDataDir)
		{
			line += $" --data \"{AppPaths.DataDir}\"";
		}
		commandKey.SetValue(string.Empty, line);
	}
}
