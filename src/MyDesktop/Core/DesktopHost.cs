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
	/// 上次在 Progman 之外找到的图标视图宿主（WorkerW）。界面线程和钩子线程都会读写，只存窗口句柄。
	/// </summary>
	static IntPtr _workerHost;

	/// <summary>
	/// 查找承载桌面图标视图（SHELLDLL_DefView）的顶层窗口。通常是 Progman；
	/// 用过动态壁纸等软件后，图标视图可能被移到某个 WorkerW 中（Win10 换壁纸的淡入动画、幻灯片放映也会这样），直到资源管理器重启。
	/// 这时要枚举全部顶层窗口才找得到，而本方法在资源管理器的每个界面事件、每次层级调整里都要调用，
	/// 每次都枚举会让 CPU 一直居高不下（用户反馈 Win10 上持续占用、卡顿），所以记住找到的 WorkerW，它还承载着图标视图就直接用。
	/// </summary>
	public static IntPtr FindDesktopWindow()
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
		{
			return progman;
		}
		var cached = _workerHost;
		if (cached != IntPtr.Zero && FindWindowEx(cached, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
		{
			return cached;
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
		if (found == IntPtr.Zero)
		{
			return progman;
		}
		_workerHost = found;
		Log.Info($"桌面图标视图不在 Progman 里，改在 {GetClassName(found)} 窗口中");
		return found;
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
	/// 全部亚克力窗口（Views/AcrylicBackdrop），摆在图标层之上、分区之下。只在界面线程上读写。
	/// </summary>
	public static HashSet<IntPtr> Backdrops { get; } = [];

	/// <summary>
	/// 该点是否落在散放图标层的某个图标上；图标之外的透明处点击会穿透到桌面。
	/// </summary>
	public static bool HitsLayerIcon(POINT point) => _layers.IconRects.Any(r => r.Contains(point));

	/// <summary>
	/// 选框是否碰到散放图标层的图标：右键框选框住了图标时才接住松开、弹出图标的菜单。
	/// </summary>
	public static bool AnyLayerIconIn(RECT rect) => _layers.IconRects.Any(r => r.IntersectsWith(rect));

	/// <summary>
	/// 「显示桌面」把桌面提到最前后，桌面窗口每次被激活都会被系统再提到普通窗口的最上层，盖住紧贴其上的图标层和分区，
	/// 等层级被纠正才露出来，看上去闪一下。由本程序激活桌面时先把它们临时置顶（Lift），桌面提上来以后再放回去（Unlift）。
	/// 置顶期间不改写它们的层级；_lifting 表示正在置顶、撤销置顶或调整分区之间的上下，放行这期间的层级变化。只在界面线程上读写。
	/// </summary>
	static bool _lifted;
	static bool _lifting;

	/// <summary>
	/// 卷起的分区在悬停展开或拖放展开期间临时置顶的分区窗口。固定展开不进这里。和 <see cref="_lifted"/> 分开：
	/// 那边是全体图标层、亚克力和分区一起短暂置顶；这里只把正在展开的几个放到置顶层最下面（普通应用之上、任务栏之下），收起后移除。
	/// 只在界面线程上读写。
	/// </summary>
	static readonly HashSet<IntPtr> _expanded = [];

	/// <summary>
	/// 展开置顶的分区对应的亚克力窗口。半透明分区不带着它一起上去的话，展开的部分会透出盖在上面的应用。
	/// </summary>
	static readonly Dictionary<IntPtr, IntPtr> _expandedBackdrop = [];

	/// <summary>
	/// 正在临时置顶、撤销置顶或调整分区之间的上下（SetZOrder 进行中）。
	/// </summary>
	public static bool IsAdjustingZOrder => _lifting;

	/// <summary>
	/// 把图标层、亚克力窗口和分区临时置顶：按这个次序从下到上，分区仍压在最上面；分区之间按原来的上下次序。
	/// 亚克力窗口不跟着置顶的话，这期间会被提上来的桌面盖住，分区先变透明再恢复（实测）。
	/// </summary>
	public static void Lift(IEnumerable<IntPtr> fences)
	{
		TraceZOrder("临时置顶");
		SetZOrder(_layers.Windows.Concat(Backdrops).Concat(BottomToTop(fences)), HWND_TOPMOST);
		_lifted = true;
	}

	/// <summary>
	/// 撤销临时置顶：放到所有普通窗口之上，桌面此时在普通窗口的最上层，它们也就仍在桌面之上，之后由调用方按平时的规则摆好。
	/// 展开中的分区不跟着撤销，仍留在置顶层，这里再摆回置顶层最下面。
	/// </summary>
	public static void Unlift(IEnumerable<IntPtr> fences)
	{
		if (!_lifted)
		{
			return;
		}
		_lifted = false;
		TraceZOrder("撤销临时置顶");
		var raisedBackdrops = ExpandedBackdropSet();
		var ordinary = _layers.Windows.Concat(Backdrops.Where(h => !raisedBackdrops.Contains(h)))
				.Concat(BottomToTop(fences.Where(h => !_expanded.Contains(h))));
		SetZOrder(ordinary, HWND_NOTOPMOST);
		RestackExpanded(IntPtr.Zero);
	}

	/// <summary>
	/// 卷起的分区开始悬停展开或拖放展开：把它和它的亚克力放到置顶层最下面，盖住普通应用，任务栏和其他置顶窗口仍在上面。固定展开不调用。
	/// 全体临时置顶（<see cref="Lift"/>）进行中时只记下，等 <see cref="Unlift"/> 再摆。
	/// </summary>
	/// <param name="backdrop">该分区的亚克力窗口，没有时为 0。</param>
	public static void RaiseExpanded(IntPtr fence, IntPtr backdrop)
	{
		if (fence == IntPtr.Zero || !IsWindow(fence))
		{
			return;
		}
		bool added = _expanded.Add(fence);
		_expandedBackdrop.TryGetValue(fence, out var previous);
		bool hasBackdrop = backdrop != IntPtr.Zero && IsWindow(backdrop);
		if (hasBackdrop)
		{
			_expandedBackdrop[fence] = backdrop;
		}
		else
		{
			_expandedBackdrop.Remove(fence);
		}
		// 正在调整层级时只记下，避免 SetZOrder 重入把 _lifting 提前清掉；下次同步时若还没进置顶层会再摆
		bool placed = !added && previous == (hasBackdrop ? backdrop : IntPtr.Zero) && IsTopmost(fence);
		if (_lifted || _lifting || placed)
		{
			return;
		}
		RestackExpanded(added ? fence : IntPtr.Zero);
	}

	static bool IsTopmost(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd)
			&& (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

	/// <summary>
	/// 收起或悬停展开结束：撤掉这个分区的临时置顶，连同亚克力放回桌面层（普通应用之下）。没在展开置顶中时不做任何事。
	/// </summary>
	public static void LowerExpanded(IntPtr fence)
	{
		if (!_expanded.Contains(fence))
		{
			return;
		}
		// 全体置顶进行中：从名单去掉即可，Unlift 时和大家一起回到普通层。
		// 单次层级调整进行中先不挪，免得重入；调用方之后还会再同步
		if (_lifting)
		{
			return;
		}
		_expanded.Remove(fence);
		_expandedBackdrop.Remove(fence, out var backdrop);
		if (backdrop != IntPtr.Zero && !IsWindow(backdrop))
		{
			backdrop = IntPtr.Zero;
		}
		if (_lifted || !IsWindow(fence))
		{
			return;
		}
		ReturnToDesktopBand(fence, backdrop);
	}

	/// <summary>
	/// 展开中的分区或它的亚克力掉出置顶层时再摆回去。层级校正（见 FenceManager.KeepFencesAboveDesktop）里调用。
	/// </summary>
	public static void EnsureExpandedAboveApps()
	{
		if (_lifted || _lifting || _expanded.Count == 0)
		{
			return;
		}
		foreach (var fence in _expanded.ToArray())
		{
			if (!IsWindow(fence))
			{
				_expanded.Remove(fence);
				_expandedBackdrop.Remove(fence);
			}
		}
		if (_expanded.Count == 0)
		{
			return;
		}
		foreach (var fence in _expanded)
		{
			if (IsWindowVisible(fence) && (GetWindowLongPtr(fence, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0)
			{
				RestackExpanded(IntPtr.Zero);
				return;
			}
		}
		foreach (var backdrop in _expandedBackdrop.Values)
		{
			if (IsWindow(backdrop) && IsWindowVisible(backdrop) && (GetWindowLongPtr(backdrop, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0)
			{
				RestackExpanded(IntPtr.Zero);
				return;
			}
		}
	}

	static HashSet<IntPtr> ExpandedBackdropSet() => _expandedBackdrop.Values.Where(h => h != IntPtr.Zero).ToHashSet();

	/// <summary>
	/// 紧贴图标层的那一串亚克力。展开置顶的不在这串里（它们在置顶层）。没有展开中的分区时直接用 <see cref="Backdrops"/>，避免每次校正都分配。
	/// </summary>
	static IReadOnlySet<IntPtr> BackdropBand()
			=> _expandedBackdrop.Count == 0 ? Backdrops : Backdrops.Where(h => !IsExpandedBackdrop(h)).ToHashSet();

	static bool IsExpandedBackdrop(IntPtr hwnd)
	{
		foreach (var backdrop in _expandedBackdrop.Values)
		{
			if (backdrop == hwnd)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// 按从下到上重排展开中的分区：亚克力在下、分区在上，<paramref name="onTop"/> 放在最上面（为 0 时保持现有的上下次序）。
	/// 插在其他程序最下面的那个置顶窗口之后，于是进置顶层、紧挨在它下面：普通应用在下面，任务栏仍在上面。
	/// </summary>
	static void RestackExpanded(IntPtr onTop)
	{
		if (_lifted || _lifting || _expanded.Count == 0)
		{
			return;
		}
		var fences = BottomToTop(_expanded).Where(h => h != onTop && IsWindow(h)).ToList();
		if (onTop != IntPtr.Zero && IsWindow(onTop))
		{
			fences.Add(onTop);
		}
		if (fences.Count == 0)
		{
			return;
		}
		var backdrops = new List<IntPtr>();
		foreach (var fence in fences)
		{
			if (_expandedBackdrop.TryGetValue(fence, out var backdrop) && backdrop != IntPtr.Zero && IsWindow(backdrop))
			{
				backdrops.Add(backdrop);
			}
		}
		var moving = backdrops.Concat(fences).ToList();
		var anchor = LowestForeignTopmost(moving);
		TraceZOrder(onTop == IntPtr.Zero ? "展开置顶" : "展开的分区提到最前 {0}", onTop);
		SetZOrder(moving, anchor == IntPtr.Zero ? HWND_TOPMOST : anchor);
	}

	/// <summary>
	/// 把刚结束展开的分区和亚克力放回底座上方那一串：亚克力回到图标层之上，分区回到其他普通分区之上、普通应用之下。
	/// </summary>
	static void ReturnToDesktopBand(IntPtr fence, IntPtr backdrop)
	{
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			SetZOrder(NonZero(backdrop, fence), HWND_NOTOPMOST);
			return;
		}
		var skip = new HashSet<IntPtr>(_expanded) { fence };
		if (backdrop != IntPtr.Zero)
		{
			skip.Add(backdrop);
		}
		foreach (var handle in _expandedBackdrop.Values)
		{
			skip.Add(handle);
		}
		var floor = TopOfRun(desktop, _layers.Windows, IntPtr.Zero, out _);
		var backdropAnchor = NormalBandAnchor(AnchorAbove(floor, skip));
		var bandTop = floor;
		for (var above = GetWindow(floor, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			if (skip.Contains(above) || IsIgnorable(above))
			{
				continue;
			}
			if ((GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0 || IsBlocking(above))
			{
				break;
			}
			bandTop = above;
		}
		var placements = new List<(IntPtr Hwnd, IntPtr InsertAfter)>();
		if (backdrop != IntPtr.Zero)
		{
			placements.Add((backdrop, backdropAnchor));
		}
		placements.Add((fence, NormalBandAnchor(AnchorAbove(bandTop, skip))));
		TraceZOrder("展开结束，放回桌面层 {0}", fence);
		SetZOrder(placements);
	}

	/// <summary>
	/// 插在置顶窗口之后会跟着进入置顶层。要回到普通窗口层时，没有普通窗口可参照就用 HWND_NOTOPMOST（普通窗口的最上面）。
	/// </summary>
	static IntPtr NormalBandAnchor(IntPtr anchor)
	{
		if (anchor == IntPtr.Zero || (GetWindowLongPtr(anchor, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0)
		{
			return HWND_NOTOPMOST;
		}
		return anchor;
	}

	static IEnumerable<IntPtr> NonZero(params IntPtr[] handles) => handles.Where(h => h != IntPtr.Zero);

	/// <summary>
	/// 置顶层里最下面的、不属于本程序也不在 <paramref name="moving"/> 中的可见窗口。
	/// 插在它之后就进置顶层、紧挨在它下面。没有这样的窗口时返回 0。
	/// </summary>
	static IntPtr LowestForeignTopmost(IReadOnlyCollection<IntPtr> moving)
	{
		uint ownProcess = (uint)Environment.ProcessId;
		var lowest = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			if (moving.Contains(hwnd) || IsIgnorable(hwnd))
			{
				return true;
			}
			bool topmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
			GetWindowThreadProcessId(hwnd, out uint process);
			if (topmost && (process == ownProcess || IsIconic(hwnd) || IsCloaked(hwnd)))
			{
				return true;
			}
			if (!topmost)
			{
				return false;
			}
			lowest = hwnd;
			return true;
		}, IntPtr.Zero);
		return lowest;
	}

	/// <summary>
	/// 把分区提到其他分区之上（仍在其他程序的窗口之下）：正在操作、悬停展开的分区盖住旁边的分区，
	/// 否则半透明的分区叠在一起分不清哪个在上，鼠标还会落到看上去在下面的分区上。
	/// 不在桌面上方那一段里时不管，由 KeepAboveDesktop 摆回。展开置顶的分区只在同样展开的分区之间调整，仍在普通应用之上。
	/// </summary>
	public static void BringAboveFences(IntPtr hwnd, IReadOnlySet<IntPtr> fences)
	{
		if (_lifted)
		{
			return;
		}
		if (_expanded.Contains(hwnd))
		{
			BringExpandedToFront(hwnd);
			return;
		}
		if (GetInsertAfterAboveDesktop(hwnd) != null)
		{
			return;
		}
		// 从自己往上找这一段里最上面的分区，跨过本程序的其他窗口，遇到挡着的其他程序窗口为止
		var topFence = IntPtr.Zero;
		for (var above = GetWindow(hwnd, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			if (fences.Contains(above))
			{
				topFence = above;
				continue;
			}
			if (IsBlocking(above))
			{
				break;
			}
		}
		if (topFence == IntPtr.Zero)
		{
			return;
		}
		// 插到那个分区的上方，即它上面紧挨着的窗口之后
		var insertAfter = AnchorAbove(topFence);
		SetZOrder([hwnd], insertAfter == IntPtr.Zero ? HWND_TOP : insertAfter);
	}

	/// <summary>
	/// 展开置顶的分区盖住其他同样展开的分区；已经是最上面那个就不动，免得每次点击都重排、分区重画。
	/// </summary>
	static void BringExpandedToFront(IntPtr hwnd)
	{
		if (TopToBottom(_expanded).FirstOrDefault() == hwnd)
		{
			return;
		}
		RestackExpanded(hwnd);
	}

	/// <summary>
	/// 把窗口放到另一个窗口的正下方：切换标签时新标签先在当前标签下面画好，再隐藏当前标签。
	/// </summary>
	public static void PlaceBelow(IntPtr hwnd, IntPtr other) => SetZOrder([hwnd], other);

	/// <summary>
	/// 按当前层级从上到下排列这些窗口（EnumWindows 按层级从上到下枚举顶层窗口）。
	/// </summary>
	/// <summary>
	/// 要把一个窗口摆到 windows（连同图标层）里最上面那个的正上方时 SetWindowPos 用的 hWndInsertAfter：原本紧挨在它上面的窗口；
	/// 跟随最上面的分区所在的置顶或普通窗口层；重新排序时越过 moving 中的窗口，不能拿它们自身作参照。
	/// </summary>
	public static IntPtr InsertAfterAbove(IEnumerable<IntPtr> windows, IReadOnlyCollection<IntPtr> moving)
	{
		var top = TopToBottom(windows.Concat(_layers.Windows)).FirstOrDefault();
		if (top == IntPtr.Zero)
		{
			return HWND_NOTOPMOST;
		}
		if ((GetWindowLongPtr(top, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0)
		{
			return HWND_TOPMOST;
		}
		var above = AnchorAbove(top, moving);
		return above == IntPtr.Zero || (GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0 ? HWND_NOTOPMOST : above;
	}

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

	/// <summary>
	/// 在 SetZOrder 期间，各窗口要在 WM_WINDOWPOSCHANGING 里改写成的插入位置（见 ApplyPendingInsertAfter）。只在界面线程上读写。
	/// </summary>
	static readonly Dictionary<IntPtr, IntPtr> _pendingInsertAfter = [];

	/// <summary>
	/// 逐个放到 insertAfter 的位置，一次性生效（DeferWindowPos，结果和逐个 SetWindowPos 一样，实测）：逐个挪的话中间状态可能被 DWM 合成出来，
	/// 比如临时置顶时先挪上去的亚克力窗口压在还没挪的分区上面，分区被模糊、闪一下。
	/// insertAfter 是具体窗口时不直接传给系统，而是先传 HWND_TOP、再在各窗口的 WM_WINDOWPOSCHANGING 里改写成它（原生窗口探针实测）：
	/// 直接传的话，普通窗口插在置顶窗口之后调用成功却原地不动（显示桌面后切换标签，新标签留在最底下，只剩它的亚克力），
	/// 置顶窗口插在普通窗口之后也落不到它正下方，插在以管理员身份运行的程序的窗口之后还会被拒绝访问（错误码 5）；
	/// 在消息里改写则都对：普通窗口跟着进入置顶层、置顶窗口跟着回到普通层，都紧挨在它下面，也不受权限限制。
	/// 不能先传 HWND_BOTTOM：目标和参照都已置顶时，即使消息里改写了位置，调用仍会成功却原地不动（Win+D 后切换曾经置顶的隐藏标签，实测）。
	/// </summary>
	static void SetZOrder(IEnumerable<IntPtr> windows, IntPtr insertAfter)
			=> SetZOrder(windows.Where(hwnd => hwnd != IntPtr.Zero).Select(hwnd => (hwnd, insertAfter)).ToList());

	/// <summary>
	/// 和 <see cref="SetZOrder(IEnumerable{IntPtr}, IntPtr)"/> 一样，但每个窗口可以插在不同的位置：展开结束时亚克力回到底座上、分区回到普通分区之上。
	/// 先放的在下面。
	/// </summary>
	static void SetZOrder(List<(IntPtr Hwnd, IntPtr InsertAfter)> list)
	{
		if (list.Count == 0)
		{
			return;
		}
		const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
		var moving = list.Select(item => item.Hwnd).ToList();
		for (int i = 0; i < list.Count; i++)
		{
			list[i] = (list[i].Hwnd, NotOwnedBy(list[i].InsertAfter, moving));
		}
		bool sameAnchor = list.All(item => item.InsertAfter == list[0].InsertAfter);
		if (sameAnchor)
		{
			TraceZOrder($"调整层级：{string.Join("、", list.Select((_, i) => $"{{{i}}}"))} 插在 {{{list.Count}}} 之后", [.. moving, list[0].InsertAfter]);
		}
		else
		{
			TraceZOrder($"调整层级：{string.Join("、", list.Select((_, i) => $"{{{i}}} 插在 {{{list.Count + i}}} 之后"))}", [.. moving, .. list.Select(item => item.InsertAfter)]);
		}
		// 特殊值也登记：这批窗口自己的 WM_WINDOWPOSCHANGING 照传入的位置摆，不被 KeepAboveDesktop 等改写
		foreach (var (hwnd, insertAfter) in list)
		{
			_pendingInsertAfter[hwnd] = insertAfter;
		}
		_lifting = true;
		try
		{
			string? failure = null;
			var info = BeginDeferWindowPos(list.Count);
			if (info == IntPtr.Zero)
			{
				failure = $"BeginDeferWindowPos 错误码 {Marshal.GetLastWin32Error()}";
			}
			for (int i = 0; failure == null && i < list.Count; i++)
			{
				info = DeferWindowPos(info, list[i].Hwnd, PassedInsertAfter(list[i].InsertAfter), 0, 0, 0, 0, flags);
				if (info == IntPtr.Zero)
				{
					failure = $"DeferWindowPos 第 {i + 1} 个窗口（{GetClassName(list[i].Hwnd)}）错误码 {Marshal.GetLastWin32Error()}";
				}
			}
			if (failure == null && !EndDeferWindowPos(info))
			{
				failure = $"EndDeferWindowPos 错误码 {Marshal.GetLastWin32Error()}";
			}
			if (failure != null)
			{
				// 查原因用的日志：窗口个数、各窗口类名、插在哪个窗口之后
				string anchors = string.Join("、", list.Select(item => item.InsertAfter).Distinct().Select(DescribeInsertAfter));
				Log.Warn($"批量调整窗口层级失败，改为逐个调整：{failure}；共 {list.Count} 个窗口（{string.Join("、", moving.Select(GetClassName))}），插在 {anchors} 之后");
				foreach (var (hwnd, insertAfter) in list)
				{
					if (!SetWindowPos(hwnd, PassedInsertAfter(insertAfter), 0, 0, 0, 0, flags))
					{
						Log.Warn($"逐个调整窗口层级也失败：{GetClassName(hwnd)} 错误码 {Marshal.GetLastWin32Error()}");
						return;
					}
				}
			}
		}
		finally
		{
			foreach (var (hwnd, _) in list)
			{
				_pendingInsertAfter.Remove(hwnd);
			}
			_lifting = false;
		}
	}

	/// <summary>
	/// 具体窗口不直接传给 DeferWindowPos，先传 HWND_TOP，再在 WM_WINDOWPOSCHANGING 里改写（见 SetZOrder）。特殊值原样传。
	/// </summary>
	static IntPtr PassedInsertAfter(IntPtr insertAfter) => insertAfter.ToInt64() is >= -2 and <= 1 ? insertAfter : HWND_TOP;

	static string DescribeInsertAfter(IntPtr insertAfter)
			=> insertAfter.ToInt64() is >= -2 and <= 1 ? insertAfter.ToInt64().ToString() : GetClassName(insertAfter);

	/// <summary>
	/// 把本程序的原生分层窗口（飞行图标、截图顶替窗口）逐个放到 insertAfter 的位置，做法见 SetZOrder；
	/// 这些窗口要用 LayeredWindowClass 创建，窗口过程才会应用插入位置。
	/// </summary>
	public static void PlaceAfter(IReadOnlyList<IntPtr> windows, IntPtr insertAfter) => SetZOrder(windows, insertAfter);

	const string LayeredClassName = "MyDesktopLayeredWindow";
	// 窗口过程要一直被引用，免得被回收
	static readonly WindowProc LayeredWindowProc = (hwnd, msg, wParam, lParam) =>
	{
		if (msg == WM_WINDOWPOSCHANGING)
		{
			ApplyPendingInsertAfter(hwnd, lParam);
		}
		return DefWindowProc(hwnd, msg, wParam, lParam);
	};
	static bool _layeredClassRegistered;

	/// <summary>
	/// 飞行图标、截图顶替窗口这类原生分层窗口的窗口类（内容全由 UpdateLayeredWindow 提供）：窗口过程只应用 PlaceAfter 要求的插入位置。
	/// 以前借用系统的 Static 窗口类，没法在 WM_WINDOWPOSCHANGING 里改写插入位置，直接传又不可靠（见 SetZOrder）。
	/// </summary>
	public static string LayeredWindowClass()
	{
		if (!_layeredClassRegistered)
		{
			var windowClass = new WNDCLASS { lpfnWndProc = LayeredWindowProc, hInstance = GetModuleHandle(null), lpszClassName = LayeredClassName };
			if (RegisterClass(ref windowClass) == 0)
			{
				throw new InvalidOperationException($"注册窗口类失败（错误码 {Marshal.GetLastWin32Error()}）");
			}
			_layeredClassRegistered = true;
		}
		return LayeredClassName;
	}

	/// <summary>
	/// 查层级问题用：最近的层级操作，层级不对时连同快照一起记进日志（见 CheckZOrder）。只在界面线程上读写。
	/// </summary>
	static readonly Queue<(DateTime Time, string Text, IntPtr[] Windows)> _zOrderTrace = new();

	/// <summary>
	/// 记一条层级操作：text 里的 {0}、{1}… 依次是 windows 里的窗口，记日志时才换成名字。
	/// </summary>
	public static void TraceZOrder(string text, params IntPtr[] windows)
	{
		_zOrderTrace.Enqueue((DateTime.Now, text, windows));
		while (_zOrderTrace.Count > 100)
		{
			_zOrderTrace.Dequeue();
		}
	}

	static string NameOf(IntPtr hwnd, IReadOnlyDictionary<IntPtr, string> labels, IntPtr desktop) => hwnd.ToInt64() switch
	{
		0 => "HWND_TOP",
		1 => "HWND_BOTTOM",
		-1 => "HWND_TOPMOST",
		-2 => "HWND_NOTOPMOST",
		_ => labels.GetValueOrDefault(hwnd) ?? (hwnd == desktop ? "桌面" : _layers.Windows.Contains(hwnd) ? "图标层" : $"{GetClassName(hwnd)}(0x{hwnd.ToInt64():X})"),
	};

	static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

	/// <summary>
	/// 在本程序窗口（分区、图标层、亚克力窗口）的 WM_WINDOWPOSCHANGING 里最先调用：SetZOrder 要求的插入位置写进去。
	/// 返回 true 表示已按要求改写，调用方不要再改。
	/// </summary>
	static bool ApplyPendingInsertAfter(IntPtr hwnd, IntPtr lParam)
	{
		if (!_pendingInsertAfter.TryGetValue(hwnd, out var insertAfter))
		{
			return false;
		}
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((pos.flags & SWP_NOZORDER) == 0)
		{
			pos.hwndInsertAfter = insertAfter;
			Marshal.StructureToPtr(pos, lParam, false);
		}
		return true;
	}

	/// <summary>
	/// 查层级问题用（用户反馈：显示桌面后整个分区不见了，只剩它的亚克力）：检查每个显示着的分区都在桌面之上，
	/// 全部显示着的亚克力窗口在分区下面、桌面之上。都对时返回 null，不对时返回问题和当时的层级快照（从上到下）。
	/// </summary>
	/// <param name="labels">本程序窗口在快照里的名字。</param>
	public static string? CheckZOrder(IReadOnlyList<(IntPtr Fence, IntPtr Backdrop)> pairs, IReadOnlyDictionary<IntPtr, string> labels)
	{
		if (_lifted || _lifting)
		{
			return null;
		}
		var desktop = FindDesktopWindow();
		var order = new Dictionary<IntPtr, int>();
		var all = new List<IntPtr>();
		EnumWindows((hwnd, _) =>
		{
			order[hwnd] = all.Count;
			all.Add(hwnd);
			return true;
		}, IntPtr.Zero);
		if (!order.TryGetValue(desktop, out int desktopIndex))
		{
			return null;
		}
		string? problem = null;
		foreach (var (fence, backdrop) in pairs)
		{
			bool backdropShown = backdrop != IntPtr.Zero && IsWindowVisible(backdrop) && !IsCloaked(backdrop);
			if (!IsWindowVisible(fence))
			{
				if (backdropShown)
				{
					problem = $"{labels.GetValueOrDefault(fence)}没显示，{labels.GetValueOrDefault(backdrop)}却显示着";
					break;
				}
				continue;
			}
			if (IsCloaked(fence) && backdropShown)
			{
				problem = $"{labels.GetValueOrDefault(fence)}被 DWM 藏起来了（cloak），{labels.GetValueOrDefault(backdrop)}还显示着";
				break;
			}
			if (!order.TryGetValue(fence, out int fenceIndex))
			{
				continue;
			}
			if (fenceIndex > desktopIndex)
			{
				problem = $"{labels.GetValueOrDefault(fence)}在桌面下面";
				break;
			}
			// 展开置顶的亚克力跟着自己的分区在应用之上，盖住其他分区是预期的；盖住自己的分区才算错
			var coveringBackdrop = Backdrops.FirstOrDefault(h => IsWindowVisible(h) && order.TryGetValue(h, out int index) && index < fenceIndex
					&& ExpandedBackdropCovers(h, fence));
			if (coveringBackdrop != IntPtr.Zero)
			{
				problem = $"{labels.GetValueOrDefault(coveringBackdrop)}在{labels.GetValueOrDefault(fence)}上面";
				break;
			}
			if (backdrop != IntPtr.Zero && IsWindowVisible(backdrop) && order.TryGetValue(backdrop, out int backdropIndex) && backdropIndex > desktopIndex)
			{
				problem = $"{labels.GetValueOrDefault(backdrop)}在桌面下面";
				break;
			}
		}
		if (problem == null)
		{
			return null;
		}
		// 快照必须包含桌面下面的分区和亚克力，否则恰好漏掉发生异常的窗口
		int lastIndex = labels.Keys.Where(h => IsWindowVisible(h) && order.ContainsKey(h)).Select(h => order[h]).Append(desktopIndex).Max();
		uint ownProcess = (uint)Environment.ProcessId;
		var lines = new List<string>();
		bool started = false;
		int otherWindows = 0;
		for (int i = 0; i <= lastIndex; i++)
		{
			var hwnd = all[i];
			if (!IsWindowVisible(hwnd))
			{
				continue;
			}
			GetWindowThreadProcessId(hwnd, out uint process);
			bool own = process == ownProcess;
			started |= own || hwnd == desktop;
			if (!started)
			{
				continue;
			}
			// 限制其他程序窗口的数量，但不能截掉桌面或本程序的异常窗口
			if (!own && hwnd != desktop && otherWindows++ >= 40)
			{
				continue;
			}
			bool topmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
			string name = hwnd == desktop ? "桌面" : labels.GetValueOrDefault(hwnd) ?? (_layers.Windows.Contains(hwnd) ? "图标层" : own ? $"本程序 {GetClassName(hwnd)}" : $"其他程序 {GetClassName(hwnd)}");
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
			var rect = GetWindowRect(hwnd);
			lines.Add($"[{i}] {name} hwnd=0x{hwnd.ToInt64():X} pid={process} rect=({rect.Left},{rect.Top},{rect.Width},{rect.Height})"
					+ $"{(topmost ? "（置顶）" : "")}{(cloaked != 0 ? $"（DWM 隐藏 {cloaked}）" : "")}{(!own && IsIgnorable(hwnd) ? "（鼠标穿透）" : "")}");
		}
		var recent = _zOrderTrace.Where(e => DateTime.Now - e.Time < TimeSpan.FromSeconds(15))
				.Select(e => $"{e.Time:HH:mm:ss.fff} " + string.Format(e.Text, e.Windows.Select(h => (object)NameOf(h, labels, desktop)).ToArray()));
		return $"{problem}；层级从上到下：{string.Join(" > ", lines)}；最近的层级操作：{string.Join(" | ", recent)}";
	}

	/// <summary>
	/// 看不见也点不到的窗口：隐藏的（本程序的也算），或其他程序鼠标穿透的分层窗口（比如截图软件 PixPin 铺在桌面上方的透明窗口）。
	/// 排层级时越过它们：它们不挡着什么，用不着排在它们下面（用户的 PixPin 就在桌面上方铺着这样一个窗口）。
	/// 底座（桌面和紧贴其上的图标层）、亚克力窗口那一串里夹着的这类窗口也一并越过，否则那一串在它那里断开，
	/// 算出的插入位置会落到亚克力窗口自己身上（插在自己之后，SetWindowPos 整个失败，拖动分区后亚克力窗口留在原处，用户反馈）。
	/// </summary>
	static bool IsIgnorable(IntPtr hwnd)
	{
		// 本程序的隐藏窗口也越过：输入法给界面线程配的隐藏窗口（IME、CiceroUIWndFrame）归属于当前激活的窗口，
		// 刚点的分区插在它后面就掉到了最底下、桌面下面，只剩它的亚克力（用户反馈，日志实测：显示桌面后点桌面空白处再点分区）
		if (!IsWindowVisible(hwnd))
		{
			return true;
		}
		GetWindowThreadProcessId(hwnd, out uint process);
		if (process == (uint)Environment.ProcessId)
		{
			return false;
		}
		const long clickThrough = WS_EX_LAYERED | WS_EX_TRANSPARENT;
		return (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & clickThrough) == clickThrough;
	}

	/// <summary>
	/// 挡在本程序窗口上面的其他程序窗口：可见、不是鼠标穿透的。
	/// </summary>
	static bool IsBlocking(IntPtr hwnd)
	{
		GetWindowThreadProcessId(hwnd, out uint process);
		return process != (uint)Environment.ProcessId && !IsIgnorable(hwnd);
	}

	/// <summary>
	/// 要插到 hwnd 正上方时用作 hWndInsertAfter 的窗口：hwnd 上面第一个不该越过的窗口（越过 IsIgnorable 的窗口和 skip 里的窗口）；
	/// hwnd 已在最上面时为 0。
	/// </summary>
	static IntPtr AnchorAbove(IntPtr hwnd, IReadOnlyCollection<IntPtr>? skip = null)
	{
		var above = GetWindow(hwnd, GW_HWNDPREV);
		while (above != IntPtr.Zero && (IsIgnorable(above) || skip?.Contains(above) == true))
		{
			above = GetWindow(above, GW_HWNDPREV);
		}
		return above;
	}

	/// <summary>
	/// 从 start 往上越过紧挨着的一串 run 里的窗口（夹在其中的 IsIgnorable 窗口一并越过），返回这一串最上面的那个，一个都没越过时就是 start；
	/// 这一串里有 hwnd 时 contains 为 true。底座（桌面和紧贴其上的图标层）、底座上方那一串亚克力窗口都这样找。
	/// </summary>
	static IntPtr TopOfRun(IntPtr start, IReadOnlySet<IntPtr> run, IntPtr hwnd, out bool contains)
	{
		contains = false;
		var top = start;
		for (var above = GetWindow(start, GW_HWNDPREV); above != IntPtr.Zero && (run.Contains(above) || IsIgnorable(above)); above = GetWindow(above, GW_HWNDPREV))
		{
			contains |= above == hwnd;
			top = above;
		}
		return top;
	}

	/// <summary>
	/// 插入位置 insertAfter 不能是 moving 里的窗口名下的窗口（它们的提示、菜单等弹出窗口）：系统挪动窗口时名下的窗口跟着走，
	/// 插在自己名下的窗口之后摆不对，会被扔到最底下；这时改用再往上一个窗口。特殊值原样返回。
	/// </summary>
	static IntPtr NotOwnedBy(IntPtr insertAfter, IReadOnlyCollection<IntPtr> moving)
	{
		while (insertAfter.ToInt64() is < -2 or > 1 && IsOwnedBy(insertAfter, moving))
		{
			insertAfter = AnchorAbove(insertAfter);
		}
		return insertAfter;
	}

	static bool IsOwnedBy(IntPtr hwnd, IReadOnlyCollection<IntPtr> owners)
	{
		for (var owner = GetWindow(hwnd, GW_OWNER); owner != IntPtr.Zero; owner = GetWindow(owner, GW_OWNER))
		{
			if (owners.Contains(owner))
			{
				return true;
			}
		}
		return false;
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
	/// 展开置顶的亚克力是否该被当成盖住这个分区：只有盖住它自己的分区才算，盖住其他分区是它跟分区一起在应用之上。
	/// </summary>
	static bool ExpandedBackdropCovers(IntPtr backdrop, IntPtr fence)
			=> !IsExpandedBackdrop(backdrop) || (_expandedBackdrop.TryGetValue(fence, out var own) && own == backdrop);

	/// <summary>
	/// 计算把窗口放到桌面窗口正上方所需的 hwndInsertAfter；已在该位置、找不到桌面、正临时置顶（见 Lift）或因展开而单独置顶（见 RaiseExpanded）时返回 null。
	/// 平时桌面位于最底层，分区也就在所有应用窗口之下；Win+D 时桌面被提到最前，分区紧随其上依然可见。
	/// 散放图标层紧贴桌面，分区再压在图标层之上，与系统桌面上「窗口盖住图标」的关系一致。
	/// </summary>
	public static IntPtr? GetInsertAfterAboveDesktop(IntPtr hwnd)
	{
		if (_lifted || _expanded.Contains(hwnd))
		{
			return null;
		}
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return null;
		}
		// 紧贴桌面的一串图标层：图标层自己在其中即已就位；分区以其中最上面的一个为底
		var floor = TopOfRun(desktop, _layers.Windows, hwnd, out bool inFloor);
		if (inFloor)
		{
			return null;
		}
		if (_layers.Windows.Contains(hwnd))
		{
			return AnchorAbove(desktop);
		}
		// 图标层上面那一串亚克力窗口也算底座：分区插到它们上面，否则插到亚克力窗口下面的分区会被别的分区的亚克力（和阴影）盖住。
		// 展开置顶的亚克力在置顶层，不算进这串，免得底座一直延伸到任务栏下面
		floor = TopOfRun(floor, BackdropBand(), IntPtr.Zero, out _);
		// 从底座往上看，在遇到第一个挡着的其他程序窗口之前找到自己，说明已经和其他分区一起紧贴在底座上方
		for (var above = GetWindow(floor, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			if (above == hwnd)
			{
				return null;
			}
			if (IsBlocking(above))
			{
				break;
			}
		}
		// 插到紧挨底座的那个窗口之后；底座已是最顶层时该值为空，即 HWND_TOP。
		// 「显示桌面」时紧挨桌面的是任务栏这类置顶窗口，在 WM_WINDOWPOSCHANGING 里改写成插在它之后，分区就临时成为置顶窗口（探针实测），这是有意为之：
		// 被提起的桌面仍是普通窗口层的最上层，尽量不干扰 Explorer 对显示桌面状态的判断。
		// 桌面落回底层后，分区会被插到普通窗口之后，置顶状态随之自动解除（同样在消息里改写，实测）。
		return AnchorAbove(floor);
	}

	/// <summary>
	/// 亚克力窗口（Views/AcrylicBackdrop）的 hwndInsertAfter：紧贴在底座（桌面和紧贴其上的图标层）之上、所有分区之下。
	/// DWM 给圆角窗口加的阴影去不掉（实测），这样阴影只落在壁纸和图标层上，不会盖住旁边分区的内容。
	/// 已经在底座上方那一串亚克力窗口里、找不到桌面、正临时置顶（见 Lift）或跟着分区展开置顶时返回 null。
	/// </summary>
	public static IntPtr? GetBackdropInsertAfter(IntPtr hwnd)
	{
		// 临时置顶、撤销置顶和调整分区上下的过程中（_lifting）分区每挪一下都会来摆亚克力窗口，这时不动它：
		// 置顶时 _lifted 要等全部挪完才设上，之前按平时的规则摆，会把刚置顶的亚克力窗口摆回桌面上方、被提上来的桌面盖住（用户反馈）
		if (_lifted || _lifting || IsExpandedBackdrop(hwnd))
		{
			return null;
		}
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero)
		{
			return null;
		}
		var floor = TopOfRun(desktop, _layers.Windows, IntPtr.Zero, out _);
		TopOfRun(floor, BackdropBand(), hwnd, out bool inBand);
		// 不在底座上方那一串亚克力窗口里时，插到紧挨底座的那个窗口之后，即底座正上方
		return inBand ? null : AnchorAbove(floor);
	}

	/// <summary>
	/// 在亚克力窗口的 WM_WINDOWPOSCHANGING 中调用（它自己摆位置时都传 HWND_TOP，见 AcrylicBackdrop.Show）：
	/// 改写成紧贴底座之上（GetBackdropInsertAfter），已在那里或正在调整层级时不改层级。
	/// </summary>
	public static void KeepBackdropAboveFloor(IntPtr hwnd, IntPtr lParam)
	{
		if (ApplyPendingInsertAfter(hwnd, lParam))
		{
			return;
		}
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((pos.flags & SWP_NOZORDER) != 0)
		{
			return;
		}
		// 别的窗口正在调整层级时（_lifting）分区每挪一下都会来摆亚克力窗口，这时只改位置、不动层级，免得传进来的 HWND_TOP 把它提到分区上面；
		// 插在自己之后的话 SetWindowPos 整个失败，位置也不会变
		var insertAfter = _lifting ? null : GetBackdropInsertAfter(hwnd);
		if (insertAfter is IntPtr after && after != hwnd)
		{
			pos.hwndInsertAfter = after;
			TraceZOrder("{0} 改为插在 {1} 之后", hwnd, after);
		}
		else
		{
			pos.flags |= SWP_NOZORDER;
		}
		Marshal.StructureToPtr(pos, lParam, false);
	}

	/// <summary>
	/// 把 fences（按从上到下的次序）连同全部亚克力窗口一次性摆回底座（桌面和紧贴其上的图标层）正上方：分区在上、亚克力窗口在下。
	/// 逐个摆的话，分区摆好后它的亚克力窗口要等分区的 WM_WINDOWPOSCHANGED 才跟上，DWM 可能正好合成出分区没有亚克力的一帧，
	/// 分区暗一下（用户反馈：显示桌面后打开、关闭设置窗口时，桌面被提到分区上面、再由本程序摆回）。
	/// </summary>
	public static void PlaceAboveFloor(IReadOnlyList<IntPtr> fences)
	{
		var desktop = FindDesktopWindow();
		if (desktop == IntPtr.Zero || fences.Count == 0)
		{
			return;
		}
		// 展开置顶的分区和亚克力留在应用之上，不跟着这批回到底座；参照窗口也要越过它们，
		// 否则空桌面上紧挨着的就是这个置顶分区，整批会跟着它进入置顶层
		var raisedBackdrops = ExpandedBackdropSet();
		var movingFences = fences.Where(h => !_expanded.Contains(h)).ToList();
		var movingBackdrops = Backdrops.Where(h => !raisedBackdrops.Contains(h)).ToList();
		if (movingFences.Count == 0 && movingBackdrops.Count == 0)
		{
			return;
		}
		// 插到底座上方第一个不用挪的窗口之后
		var floor = TopOfRun(desktop, _layers.Windows, IntPtr.Zero, out _);
		var insertAfter = AnchorAbove(floor, movingFences.Concat(movingBackdrops).Concat(_expanded).Concat(raisedBackdrops).ToHashSet());
		// 都插到同一个窗口之后，后插的在上面：先亚克力窗口，再从下到上插分区。
		// 显示桌面时这个窗口是任务栏这类置顶窗口，插在它之后整批跟着进入置顶层（见 SetZOrder）
		// fences 按从上到下传入，倒过来才是从下到上
		SetZOrder(movingBackdrops.Concat(Enumerable.Reverse(movingFences)), insertAfter == IntPtr.Zero ? HWND_TOP : insertAfter);
	}

	/// <summary>
	/// 在 WM_WINDOWPOSCHANGING 中调用：任何层级变化（被点击激活、Show 等）都改写为「紧贴桌面窗口之上」，
	/// 平时位于所有应用窗口之下，Win+D 桌面被提到最前时依然可见。
	/// </summary>
	public static void KeepAboveDesktop(IntPtr hwnd, IntPtr lParam)
	{
		if (ApplyPendingInsertAfter(hwnd, lParam))
		{
			return;
		}
		var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((pos.flags & SWP_NOZORDER) != 0 || _lifting)
		{
			return;
		}
		if (GetInsertAfterAboveDesktop(hwnd) is IntPtr insertAfter)
		{
			insertAfter = NotOwnedBy(insertAfter, [hwnd]);
			pos.hwndInsertAfter = insertAfter;
			TraceZOrder("{0} 改为插在 {1} 之后", hwnd, insertAfter);
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
		TraceZOrder("{0} 摆回桌面上方", hwnd);
		SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
	/// 资源管理器是否显示受保护的操作系统文件（取消勾选了「隐藏受保护的操作系统文件」），即 desktop.ini 这类同时带隐藏和系统属性的文件。
	/// </summary>
	public static bool ShowsProtectedFiles()
	{
		using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
		return key?.GetValue("ShowSuperHidden") is int value && value != 0;
	}
}
