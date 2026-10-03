using Microsoft.Win32;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 与 Explorer 桌面窗口交互：维护分区窗口的层级、显示/隐藏桌面图标。
/// 注意只能做不阻塞的查询或异步调用：不要把分区窗口的所有者设为桌面窗口，
/// 跨进程的所有者关系会让本程序与 Explorer 共享输入队列，右键菜单等场景下会互相等待而死锁。
/// </summary>
internal static class DesktopHost
{
	/// <summary>
	/// 查找承载桌面图标视图（SHELLDLL_DefView）的顶层窗口。通常是 Progman；
	/// 用过动态壁纸等软件后，图标视图可能被移到某个 WorkerW 中。
	/// </summary>
	public static IntPtr FindDesktopWindow()
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
		{
			return progman;
		}
		var found = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
			{
				return true;
			}
			found = hwnd;
			return false;
		}, IntPtr.Zero);
		return found != IntPtr.Zero ? found : progman;
	}

	/// <summary>
	/// 桌面图标列表控件（SysListView32 "FolderView"）。
	/// </summary>
	public static IntPtr FindFolderView()
	{
		var defView = FindWindowEx(FindDesktopWindow(), IntPtr.Zero, "SHELLDLL_DefView", null);
		return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
	}

	/// <summary>
	/// 计算把窗口放到桌面窗口正上方所需的 hwndInsertAfter；已在该位置或找不到桌面时返回 null。
	/// 平时桌面位于最底层，分区也就在所有应用窗口之下；Win+D 时桌面被提到最前，分区紧随其上依然可见。
	/// </summary>
	public static IntPtr? GetInsertAfterAboveDesktop(IntPtr hwnd)
	{
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return null;
		}
		// 从桌面往上看，在遇到第一个其他程序的可见窗口之前找到自己，说明已经和其他分区一起紧贴在桌面上方
		uint ownProcess = (uint)Environment.ProcessId;
		for (var above = GetWindow(desktop, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			if (above == hwnd)
			{
				return null;
			}
			GetWindowThreadProcessId(above, out uint process);
			if (process != ownProcess && IsWindowVisible(above))
			{
				break;
			}
		}
		// 插到紧挨桌面的那个窗口之后；桌面已是最顶层时该值为空，即 HWND_TOP。
		// 「显示桌面」时紧挨桌面的是任务栏这类置顶窗口，插在其后会让分区临时成为置顶窗口，这是有意为之：
		// 被提起的桌面仍是普通窗口层的最上层，尽量不干扰 Explorer 对显示桌面状态的判断。
		// 桌面落回底层后，分区会被插到普通窗口之后，置顶状态随之自动解除。
		return GetWindow(desktop, GW_HWNDPREV);
	}

	/// <summary>
	/// 把窗口放到桌面窗口正上方（分区窗口的 WM_WINDOWPOSCHANGING 会把位置改写为桌面正上方）。
	/// </summary>
	public static void PlaceAboveDesktop(IntPtr hwnd)
	{
		SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
	}

	/// <summary>
	/// 判断窗口是否为桌面背景或桌面图标视图（用于双击空白处检测）。
	/// </summary>
	public static bool IsDesktopSurface(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		return GetClassName(hwnd) switch
		{
			"SysListView32" => GetClassName(GetParent(hwnd)) == "SHELLDLL_DefView",
			"SHELLDLL_DefView" => true,
			"Progman" => true,
			"WorkerW" => FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero,
			_ => false,
		};
	}

	/// <summary>
	/// 只隐藏图标控件本身，不改动系统「显示桌面图标」设置，Explorer 重启后自动恢复。
	/// 用异步版本，避免 Explorer 无响应时拖住本程序。
	/// </summary>
	public static void SetIconsVisible(bool visible)
	{
		var view = FindFolderView();
		if (view != IntPtr.Zero)
		{
			ShowWindowAsync(view, visible ? SW_SHOWNA : SW_HIDE);
		}
	}

	public static bool AreIconsVisible()
	{
		var view = FindFolderView();
		return view != IntPtr.Zero && IsWindowVisible(view);
	}

	/// <summary>
	/// 用户是否在系统里关闭了「显示桌面图标」。
	/// </summary>
	public static bool IconsHiddenBySystem()
	{
		using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
		return key?.GetValue("HideIcons") is int value && value != 0;
	}

	/// <summary>
	/// 桌面上是否正在重命名图标（存在可见的内联编辑框）。
	/// </summary>
	public static bool IsRenamingOnDesktop()
	{
		var view = FindFolderView();
		var edit = view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "Edit", null);
		return edit != IntPtr.Zero && IsWindowVisible(edit);
	}
}
