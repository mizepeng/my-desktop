using System.Runtime.InteropServices;
using Microsoft.Win32;
using MyDesktop.Native;
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
	/// 散放图标层窗口，分区要摆在它们之上。只在界面线程上修改；钩子线程通过 <see cref="IsLayer"/> 读取不可变快照。
	/// </summary>
	static volatile LayerState _layers = new([], []);

	sealed record LayerState(HashSet<IntPtr> Windows, RECT[] IconRects);

	/// <summary>
	/// 登记散放图标层窗口及其上图标的屏幕范围（物理像素），供层级计算和鼠标钩子判断「是否点在桌面空白处」。
	/// </summary>
	public static void SetLayers(IEnumerable<IntPtr> windows, IEnumerable<RECT> iconRects)
	{
		_layers = new LayerState(windows.Where(h => h != IntPtr.Zero).ToHashSet(), iconRects.ToArray());
	}

	public static bool IsLayer(IntPtr hwnd) => _layers.Windows.Contains(hwnd);

	/// <summary>
	/// 该点是否落在散放图标层的某个图标上；图标之外的透明处点击会穿透到桌面。
	/// </summary>
	public static bool HitsLayerIcon(POINT point) => _layers.IconRects.Any(r => r.Contains(point));

	/// <summary>
	/// 「显示桌面」把桌面提到最前后，桌面窗口每次被激活都会被系统再提到普通窗口的最上层，盖住紧贴其上的图标层和分区，
	/// 等层级被纠正才露出来，看上去闪一下。由本程序激活桌面时先把它们临时置顶（Lift），桌面提上来以后再放回去（Unlift）。
	/// 置顶期间不改写它们的层级；_lifting 表示正在置顶、撤销置顶或调整分区之间的上下，放行这期间的层级变化。只在界面线程上读写。
	/// </summary>
	static bool _lifted;
	static bool _lifting;

	/// <summary>
	/// 把图标层和分区临时置顶：先图标层后分区，分区仍压在图标层之上；分区之间按原来的上下次序。
	/// </summary>
	public static void Lift(IEnumerable<IntPtr> fences)
	{
		SetZOrder(_layers.Windows.Concat(BottomToTop(fences)), HWND_TOPMOST);
		_lifted = true;
	}

	/// <summary>
	/// 撤销临时置顶：放到所有普通窗口之上，桌面此时在普通窗口的最上层，它们也就仍在桌面之上，之后由调用方按平时的规则摆好。
	/// </summary>
	public static void Unlift(IEnumerable<IntPtr> fences)
	{
		if (!_lifted)
		{
			return;
		}
		_lifted = false;
		SetZOrder(_layers.Windows.Concat(BottomToTop(fences)), HWND_NOTOPMOST);
	}

	/// <summary>
	/// 把分区提到其他分区之上（仍在其他程序的窗口之下）：正在操作、悬停展开的分区盖住旁边的分区，
	/// 否则半透明的分区叠在一起分不清哪个在上，鼠标还会落到看上去在下面的分区上。
	/// 不在桌面上方那一段里时不管，由 KeepAboveDesktop 摆回。
	/// </summary>
	public static void BringAboveFences(IntPtr hwnd, IReadOnlySet<IntPtr> fences)
	{
		if (_lifted || GetInsertAfterAboveDesktop(hwnd) != null)
		{
			return;
		}
		// 从自己往上找这一段里最上面的分区，跨过本程序的其他窗口，遇到其他程序的可见窗口为止
		var topFence = IntPtr.Zero;
		uint ownProcess = (uint)Environment.ProcessId;
		for (var above = GetWindow(hwnd, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			if (fences.Contains(above))
			{
				topFence = above;
				continue;
			}
			GetWindowThreadProcessId(above, out uint process);
			if (process != ownProcess && IsWindowVisible(above))
			{
				break;
			}
		}
		if (topFence == IntPtr.Zero)
		{
			return;
		}
		// 插到那个分区的上方，即它上面紧挨着的窗口之后
		var insertAfter = GetWindow(topFence, GW_HWNDPREV);
		SetZOrder([hwnd], insertAfter == IntPtr.Zero ? HWND_TOP : insertAfter);
	}

	/// <summary>
	/// 按当前层级从上到下排列这些窗口（EnumWindows 按层级从上到下枚举顶层窗口）。
	/// </summary>
	public static List<IntPtr> TopToBottom(IEnumerable<IntPtr> windows)
	{
		var set = windows.Where(h => h != IntPtr.Zero).ToHashSet();
		var ordered = new List<IntPtr>(set.Count);
		EnumWindows((hwnd, _) =>
		{
			if (set.Contains(hwnd))
			{
				ordered.Add(hwnd);
			}
			return ordered.Count < set.Count;
		}, IntPtr.Zero);
		return ordered;
	}

	/// <summary>
	/// 逐个放到同一位置（如置顶窗口的最上层）时按从下到上的次序放，上下次序就保持不变。
	/// </summary>
	static IEnumerable<IntPtr> BottomToTop(IEnumerable<IntPtr> windows) => Enumerable.Reverse(TopToBottom(windows));

	static void SetZOrder(IEnumerable<IntPtr> windows, IntPtr insertAfter)
	{
		_lifting = true;
		try
		{
			foreach (var hwnd in windows)
			{
				SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
			}
		}
		finally
		{
			_lifting = false;
		}
	}

	/// <summary>
	/// 桌面窗口是否在其他程序的所有普通窗口之上（「显示桌面」之后，或者没有打开的窗口）。这时激活桌面会把它提到最前。
	/// 只读窗口属性、不发消息，可以在钩子回调里调用。
	/// </summary>
	public static bool IsDesktopOnTop()
	{
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return false;
		}
		uint ownProcess = (uint)Environment.ProcessId;
		for (var above = GetWindow(desktop, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			// 再往上都是任务栏这类置顶窗口，本来就在桌面之上
			if ((GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0)
			{
				return true;
			}
			GetWindowThreadProcessId(above, out uint process);
			if (process == ownProcess || !IsWindowVisible(above) || IsIconic(above))
			{
				continue;
			}
			if (DwmGetWindowAttribute(above, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
			{
				continue;
			}
			return false;
		}
		return true;
	}

	/// <summary>
	/// 计算把窗口放到桌面窗口正上方所需的 hwndInsertAfter；已在该位置、找不到桌面或正临时置顶（见 Lift）时返回 null。
	/// 平时桌面位于最底层，分区也就在所有应用窗口之下；Win+D 时桌面被提到最前，分区紧随其上依然可见。
	/// 散放图标层紧贴桌面，分区再压在图标层之上，与系统桌面上「窗口盖住图标」的关系一致。
	/// </summary>
	public static IntPtr? GetInsertAfterAboveDesktop(IntPtr hwnd)
	{
		if (_lifted)
		{
			return null;
		}
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return null;
		}
		var layers = _layers.Windows;
		// 紧贴桌面的一串图标层：图标层自己在其中即已就位；分区以其中最上面的一个为底
		var floor = desktop;
		for (var above = GetWindow(desktop, GW_HWNDPREV); above != IntPtr.Zero && layers.Contains(above); above = GetWindow(above, GW_HWNDPREV))
		{
			if (above == hwnd)
			{
				return null;
			}
			floor = above;
		}
		if (layers.Contains(hwnd))
		{
			return GetWindow(desktop, GW_HWNDPREV);
		}
		// 从底座往上看，在遇到第一个其他程序的可见窗口之前找到自己，说明已经和其他分区一起紧贴在底座上方
		uint ownProcess = (uint)Environment.ProcessId;
		for (var above = GetWindow(floor, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
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
		// 插到紧挨底座的那个窗口之后；底座已是最顶层时该值为空，即 HWND_TOP。
		// 「显示桌面」时紧挨桌面的是任务栏这类置顶窗口，插在其后会让分区临时成为置顶窗口，这是有意为之：
		// 被提起的桌面仍是普通窗口层的最上层，尽量不干扰 Explorer 对显示桌面状态的判断。
		// 桌面落回底层后，分区会被插到普通窗口之后，置顶状态随之自动解除。
		return GetWindow(floor, GW_HWNDPREV);
	}

	/// <summary>
	/// 在 WM_WINDOWPOSCHANGING 中调用：任何层级变化（被点击激活、Show 等）都改写为「紧贴桌面窗口之上」，
	/// 平时位于所有应用窗口之下，Win+D 桌面被提到最前时依然可见。
	/// </summary>
	public static void KeepAboveDesktop(IntPtr hwnd, IntPtr lParam)
	{
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((pos.flags & SWP_NOZORDER) != 0 || _lifting)
		{
			return;
		}
		if (GetInsertAfterAboveDesktop(hwnd) is IntPtr insertAfter)
		{
			pos.hwndInsertAfter = insertAfter;
		}
		else
		{
			pos.flags |= SWP_NOZORDER;
		}
		Marshal.StructureToPtr(pos, lParam, false);
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
}
