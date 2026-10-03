using Microsoft.Win32;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

internal static class SystemTheme
{
	const int ForceDark = 2;
	const int ForceLight = 3;

	public static bool IsDark()
	{
		using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
		return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
	}

	/// <summary>
	/// 让 Win32 原生菜单（托盘菜单、分区菜单、系统文件右键菜单）跟随系统深浅色。
	/// </summary>
	public static void ApplyToMenus()
	{
		try
		{
			SetPreferredAppMode(IsDark() ? ForceDark : ForceLight);
			FlushMenuThemes();
		}
		catch (Exception ex)
		{
			Log.Warn("设置菜单主题失败", ex);
		}
	}
}
