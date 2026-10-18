using Microsoft.Win32;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

internal static class ExplorerDesktopWindow
{
	public static nint FindRoot()
	{
		var root = FindWindowEx(0, 0, "Progman", null);
		if (root != 0 && FindWindowEx(root, 0, "SHELLDLL_DefView", null) != 0)
		{
			return root;
		}
		root = 0;
		EnumWindows((window, _) =>
		{
			if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) == 0)
			{
				return true;
			}
			root = window;
			return false;
		}, 0);
		return root;
	}

	public static nint FindListView()
	{
		var view = FindWindowEx(FindRoot(), 0, "SHELLDLL_DefView", null);
		return view == 0 ? 0 : FindWindowEx(view, 0, "SysListView32", null);
	}

	public static bool IconsHiddenBySystem()
	{
		using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
		return key?.GetValue("HideIcons") is int value && value != 0;
	}
}
