using Microsoft.Win32;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 查 Win10 上点「显示桌面」后桌面背景变成纯黑用的诊断日志（用户反馈：分区和任务栏正常，再点两下显示桌面恢复）。
/// 用户日志里每次点显示桌面，图标视图都在 Progman 和 WorkerW 之间搬一次家，所以在 DesktopHost.FindDesktopWindow 发现换宿主时：
/// 记一份桌面窗口结构的快照（限频），并在之后几秒里从屏幕取几个露出桌面的点，全是纯黑时记警告、恢复时再记一条。
/// 只记日志，不做任何补救；只在 Win10 上由 FindDesktopWindow 调用。
/// </summary>
internal static class DesktopDiagnostics
{
	/// <summary>
	/// 两次快照至少隔这么久，连点显示桌面时不刷屏；期间跳过的次数记在下一份快照里。
	/// </summary>
	static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 换宿主后检测背景的时刻（毫秒，从换宿主算起）。
	/// </summary>
	static readonly int[] ProbeDelays = [300, 1000, 3000, 10000];

	static readonly object Gate = new();
	static DateTime _lastSnapshot;
	static int _skippedSnapshots;
	static int _probeGeneration;
	static DateTime? _blackSince;

	public static string Describe(IntPtr hwnd)
			=> hwnd == IntPtr.Zero ? "无" : $"{GetClassName(hwnd)}(0x{hwnd.ToInt64():X})";

	/// <summary>
	/// 图标视图换了宿主。可能在钩子线程上调用，快照和检测都放到线程池上做，不拖住调用方。
	/// </summary>
	public static void OnHostChanged(IntPtr previous, IntPtr current)
	{
		int skipped = -1;
		lock (Gate)
		{
			var now = DateTime.Now;
			if (now - _lastSnapshot >= SnapshotInterval)
			{
				_lastSnapshot = now;
				skipped = _skippedSnapshots;
				_skippedSnapshots = 0;
			}
			else
			{
				_skippedSnapshots++;
			}
		}
		int generation = Interlocked.Increment(ref _probeGeneration);
		_ = Task.Run(async () =>
		{
			try
			{
				if (skipped >= 0)
				{
					string note = skipped > 0 ? $"（上一份快照之后另有 {skipped} 次换宿主没记）" : "";
					Log.Info($"桌面宿主变化：{Describe(previous)} → {Describe(current)}{note}；{Snapshot()}");
				}
				await ProbeAsync(generation);
			}
			catch (Exception ex)
			{
				Log.Warn("记录桌面窗口诊断信息失败", ex);
			}
		});
	}

	/// <summary>
	/// 按 ProbeDelays 依次检测背景；又换了宿主（generation 变了）就交给新的那一轮。
	/// </summary>
	static async Task ProbeAsync(int generation)
	{
		int elapsed = 0;
		foreach (int delay in ProbeDelays)
		{
			await Task.Delay(delay - elapsed);
			elapsed = delay;
			if (Volatile.Read(ref _probeGeneration) != generation)
			{
				return;
			}
			var result = SampleDesktop();
			if (result == null)
			{
				continue;
			}
			var (black, sampled, covering) = result.Value;
			DateTime? since;
			// 前后两轮检测可能短暂重叠，变黑的起始时刻在锁里读写
			lock (Gate)
			{
				since = _blackSince;
				_blackSince = black ? since ?? DateTime.Now : null;
			}
			if (black && since == null)
			{
				Log.Warn($"疑似桌面背景变成纯黑：换宿主后 {delay} 毫秒，{sampled} 个露出桌面的采样点都是纯黑（采样点上的窗口：{covering}）；{Snapshot()}");
			}
			else if (!black && since is DateTime start)
			{
				Log.Info($"桌面背景已不再是纯黑（距发现变黑 {(DateTime.Now - start).TotalSeconds:F1} 秒，换宿主后 {delay} 毫秒）");
			}
			else if (black && delay == ProbeDelays[^1])
			{
				Log.Warn($"换宿主 {delay} 毫秒后桌面背景仍是纯黑；{Snapshot()}");
			}
		}
	}

	/// <summary>
	/// 从屏幕取每个显示器工作区里 4×4 个点，只留露出桌面的（WindowFromPoint 落在桌面表面上，分区、图标层上的图标、其他窗口挡住的不算）。
	/// 动态壁纸（GDI 截屏是黑的，见 constraints）、纯黑的纯色背景、露出桌面的点不到 3 个时返回 null，不下结论。
	/// </summary>
	static (bool Black, int Sampled, string Covering)? SampleDesktop()
	{
		if (WallpaperBlur.HasLiveWallpaper() || IsBlackSolidBackground())
		{
			return null;
		}
		var points = new List<POINT>();
		var covering = new HashSet<string>();
		EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
		{
			var (work, _) = GetMonitorWorkArea(monitor);
			for (int row = 0; row < 4; row++)
			{
				for (int column = 0; column < 4; column++)
				{
					var point = new POINT(work.Left + work.Width * (2 * column + 1) / 8, work.Top + work.Height * (2 * row + 1) / 8);
					var hwnd = WindowFromPoint(point);
					if (DesktopHost.IsDesktopSurface(hwnd) || DesktopHost.IsDesktopSurface(GetAncestor(hwnd, GA_ROOT)))
					{
						points.Add(point);
						covering.Add(GetClassName(GetAncestor(hwnd, GA_ROOT)));
					}
				}
			}
			return true;
		}, IntPtr.Zero);
		if (points.Count < 3)
		{
			return null;
		}
		var screen = GetDC(IntPtr.Zero);
		if (screen == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			int sampled = 0;
			foreach (var point in points)
			{
				uint color = GetPixel(screen, point.X, point.Y);
				if (color == CLR_INVALID)
				{
					continue;
				}
				sampled++;
				if ((color & 0xFFFFFF) != 0)
				{
					return (false, sampled, string.Join("、", covering));
				}
			}
			return sampled < 3 ? null : (true, sampled, string.Join("、", covering));
		}
		finally
		{
			ReleaseDC(IntPtr.Zero, screen);
		}
	}

	/// <summary>
	/// 没设壁纸图片、背景色就是黑色：屏幕本来就是黑的。
	/// </summary>
	static bool IsBlackSolidBackground()
	{
		using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
		if (desktop?.GetValue("WallPaper") is string path && path.Length > 0)
		{
			return false;
		}
		using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
		return colors?.GetValue("Background") is string background && background.Trim() == "0 0 0";
	}

	/// <summary>
	/// 桌面窗口结构：所有 Progman、资源管理器可见或装着图标视图的顶层 WorkerW（按 Z 序从上到下，[序号] 是在全部顶层窗口里的位置），
	/// 图标视图的父窗口、图标列表是否可见、前台窗口。只读窗口属性、不发消息。
	/// </summary>
	static string Snapshot()
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		GetWindowThreadProcessId(progman, out uint explorer);
		var lines = new List<string>();
		int index = 0;
		int hiddenWorkers = 0;
		var defViewParent = IntPtr.Zero;
		var listView = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			int position = index++;
			string className = GetClassName(hwnd);
			if (className is not ("Progman" or "WorkerW"))
			{
				return true;
			}
			var defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (defView != IntPtr.Zero)
			{
				defViewParent = hwnd;
				listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
			}
			GetWindowThreadProcessId(hwnd, out uint process);
			bool visible = IsWindowVisible(hwnd);
			if (className == "WorkerW" && !visible && defView == IntPtr.Zero)
			{
				hiddenWorkers++;
				return true;
			}
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
			var rect = GetWindowRect(hwnd);
			long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
			lines.Add($"[{position}] {className} 0x{hwnd.ToInt64():X} pid={process}{(process != explorer ? "（不是资源管理器）" : "")}"
					+ $" {(visible ? "可见" : "隐藏")}{(cloaked != 0 ? $" DWM 隐藏 {cloaked}" : "")} rect=({rect.Left},{rect.Top},{rect.Width},{rect.Height})"
					+ $" exstyle=0x{exStyle:X}{(defView != IntPtr.Zero ? " 装着图标视图" : "")}{DescribeChildWorkers(hwnd)}");
			return true;
		}, IntPtr.Zero);
		var foreground = GetForegroundWindow();
		GetWindowThreadProcessId(foreground, out uint foregroundProcess);
		string list = listView == IntPtr.Zero ? "找不到" : IsWindowVisible(listView) ? "可见" : "隐藏";
		return $"桌面窗口（共 {index} 个顶层窗口）：{string.Join(" > ", lines)}；另有 {hiddenWorkers} 个隐藏的 WorkerW；"
				+ $"图标视图在 {Describe(defViewParent)}，图标列表{list}；前台 {Describe(foreground)}（pid={foregroundProcess}）";
	}

	/// <summary>
	/// 挂在这个窗口下面的 WorkerW（Win11 和部分 Win10 上画壁纸的 WorkerW 是 Progman 的子窗口）。
	/// </summary>
	static string DescribeChildWorkers(IntPtr hwnd)
	{
		var children = new List<string>();
		for (var child = FindWindowEx(hwnd, IntPtr.Zero, "WorkerW", null); child != IntPtr.Zero; child = FindWindowEx(hwnd, child, "WorkerW", null))
		{
			children.Add($"0x{child.ToInt64():X}{(IsWindowVisible(child) ? "可见" : "隐藏")}");
		}
		return children.Count == 0 ? "" : $" 子 WorkerW：{string.Join("、", children)}";
	}
}
