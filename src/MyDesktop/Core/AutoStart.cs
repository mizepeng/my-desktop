using Microsoft.Win32;

namespace MyDesktop.Core;

/// <summary>
/// 开机自启：写入当前用户的 Run 注册表项，无需管理员权限。
/// </summary>
internal static class AutoStart
{
	public const string Argument = "--autostart";
	const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string ValueName = "MyDesktop";

	static string ExePath => Environment.ProcessPath ?? string.Empty;

	public static bool IsEnabled()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKey);
		return key?.GetValue(ValueName) is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
	}

	public static void SetEnabled(bool enabled)
	{
		using var key = Registry.CurrentUser.CreateSubKey(RunKey);
		if (!enabled)
		{
			key.DeleteValue(ValueName, false);
			return;
		}
		var command = $"\"{ExePath}\" {Argument}";
		if (AppPaths.IsCustomDataDir)
		{
			command += $" --data \"{AppPaths.DataDir}\"";
		}
		key.SetValue(ValueName, command);
	}
}
