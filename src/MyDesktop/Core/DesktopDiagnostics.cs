using System.Windows.Threading;
using Microsoft.Win32;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 查 Win10 上桌面背景变成纯黑（用户反馈：多在点「显示桌面」时看到，分区和任务栏正常，再点两下显示桌面恢复；不开本程序不黑）。
/// 推测和资源管理器临时建出的两层 WorkerW 布局（图标视图所在的 WorkerW 下面另有一个鼠标穿透、画壁纸的 WorkerW）以及本程序藏起图标列表有关，原因还没查清。
/// 本类只记日志、不改任何窗口状态，也不做补救（Max 的决定：查清原因从根上解决，不加机械的兜底）：
/// - 换宿主、布局变化、鼠标穿透的 WorkerW 出现和消失时记桌面窗口结构的快照（限频）；
/// - 在换宿主、显示桌面、解锁、唤醒、显示器和壁纸变化、鼠标穿透的 WorkerW 消失后，以及两层布局下定时，从屏幕取露出桌面的点检测是否纯黑，
///   没下结论也记原因和次数（限频）。
/// 只在 Win10 上使用（调用方按 DesktopHost.IsWindows10 判断）。
/// </summary>
internal static class DesktopDiagnostics
{
	/// <summary>
	/// 两次完整快照至少隔这么久，连点显示桌面时不刷屏；期间跳过的次数记在下一份快照里。
	/// </summary>
	static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 触发检测的事件先等这么久再动手：连点显示桌面、唤醒时连着来的几个通知合成一轮，也不在事件回调里做重活。
	/// </summary>
	const int DebounceMilliseconds = 300;

	/// <summary>
	/// 一轮检测里各次采样的时刻（毫秒，从防抖结束算起，约为事件后 0.3、1、3、10 秒）。
	/// </summary>
	static readonly int[] ProbeDelays = [0, 700, 2700, 9700];

	/// <summary>
	/// 定时检查桌面窗口布局的间隔；两层布局下顺便采样一次（只在变黑、恢复时记日志）。
	/// </summary>
	static readonly TimeSpan LayoutInterval = TimeSpan.FromSeconds(60);

	/// <summary>
	/// 不黑的检测结果至多隔这么久记一条；同一种没下结论的原因至多隔 SkipLogInterval 记一条。
	/// </summary>
	static readonly TimeSpan ResultLogInterval = TimeSpan.FromSeconds(60);
	static readonly TimeSpan SkipLogInterval = TimeSpan.FromMinutes(10);

	static readonly object Gate = new();
	static DateTime _lastSnapshot;
	static int _skippedSnapshots;
	static int _probeGeneration;
	static bool _stopped;
	static Timer? _debounce;
	static Timer? _layoutTimer;
	static readonly SortedSet<string> _pendingReasons = [];
	// 待做的这一轮是否只由「显示桌面」触发：是的话动手前先确认桌面确实在最上层
	static bool _pendingNeedsDesktopOnTop = true;
	static Layout? _lastLayout;
	static DateTime? _blackSince;
	static DateTime _lastResultLog;
	static readonly Dictionary<string, DateTime> _skipLogged = [];
	static readonly Dictionary<string, int> _skipSuppressed = [];
	// 最近一次各类触发（显示桌面、换宿主、会话解锁等）的时刻，特殊 WorkerW 出现时一起记下，看它跟着什么出现
	static readonly Dictionary<string, DateTime> _lastEvents = [];

	/// <summary>
	/// 盯资源管理器顶层 WorkerW 显示、隐藏、销毁的 WinEvent 钩子（界面线程上装，回调也在界面线程上）。
	/// 两层布局里画壁纸的那个 WorkerW（鼠标穿透、不激活，用户日志里 style=0x9E000000 exstyle=0x80000A0）往往只存在不到 1 秒，
	/// 60 秒一次的布局检查抓不到，要靠事件记下它出现、消失的时间和当时的情况。只读、只记日志。
	/// </summary>
	static Dispatcher? _dispatcher;
	static IntPtr _workerHook;
	static uint _hookedExplorer;
	static readonly WinEventProc WorkerEventCallback = OnWorkerEvent;
	// 正显示着的特殊 WorkerW 和它出现的时刻，只在界面线程上读写
	static readonly Dictionary<IntPtr, DateTime> _specialWorkers = [];
	static DateTime _lastSpecialSnapshot;

	enum LayoutKind
	{
		Unknown,
		Progman,
		SimpleWorker,
		TwoLayer,
	}

	/// <summary>
	/// 桌面窗口布局：图标视图的宿主，两层布局时还有它下面画壁纸的 WorkerW。
	/// </summary>
	sealed record Layout(LayoutKind Kind, IntPtr Host, IntPtr Wallpaper)
	{
		public override string ToString() => Kind switch
		{
			LayoutKind.Unknown => "找不到图标视图",
			LayoutKind.Progman => $"图标视图在 {Describe(Host)}",
			LayoutKind.SimpleWorker => $"图标视图在 {Describe(Host)}（简单布局）",
			LayoutKind.TwoLayer => $"两层 WorkerW 布局：图标视图在 {Describe(Host)}，下面画壁纸的是 {Describe(Wallpaper)}",
		};
	}

	/// <summary>
	/// 一次采样的结果。Black 为空表示没下结论，原因在 Skip，同类原因的键在 SkipKey（限频用）。颜色是 COLORREF。
	/// </summary>
	sealed record Sample(bool? Black, int Sampled, uint Darkest, uint Brightest, string Covering, string? SkipKey = null, string? Skip = null)
	{
		public override string ToString() => Black switch
		{
			null => $"没下结论：{Skip}",
			true => $"{Sampled} 个露出桌面的点全是纯黑（点上的窗口：{Covering}）",
			false => $"{Sampled} 个露出桌面的点不是纯黑，最暗 {Hex(Darkest)}，最亮 {Hex(Brightest)}（点上的窗口：{Covering}）",
		};
	}

	public static string Describe(IntPtr hwnd)
			=> hwnd == IntPtr.Zero ? "无" : $"{GetClassName(hwnd)}(0x{hwnd.ToInt64():X})";

	static string Hex(uint color) => $"#{color & 0xFF:X2}{(color >> 8) & 0xFF:X2}{(color >> 16) & 0xFF:X2}";

	/// <summary>
	/// 开始定时检查布局。在界面线程上、托盘图标建好后调用。
	/// </summary>
	public static void Start()
	{
		lock (Gate)
		{
			if (_stopped || _layoutTimer != null)
			{
				return;
			}
			_layoutTimer = new Timer(_ => CheckLayout(), null, TimeSpan.Zero, LayoutInterval);
		}
		_dispatcher = Dispatcher.CurrentDispatcher;
		HookExplorer();
	}

	/// <summary>
	/// 在界面线程上给当前资源管理器进程装 WorkerW 事件钩子；资源管理器重启（Progman 换了进程）后由 CheckLayout 安排重装。
	/// </summary>
	static void HookExplorer()
	{
		if (_stopped)
		{
			return;
		}
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		if (progman == IntPtr.Zero || GetWindowThreadProcessId(progman, out uint explorer) == 0 || (explorer == _hookedExplorer && _workerHook != IntPtr.Zero))
		{
			return;
		}
		if (_workerHook != IntPtr.Zero)
		{
			UnhookWinEvent(_workerHook);
		}
		_specialWorkers.Clear();
		_hookedExplorer = explorer;
		_workerHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, WorkerEventCallback, explorer, 0, WINEVENT_OUTOFCONTEXT);
		if (_workerHook == IntPtr.Zero)
		{
			Log.Warn("监视资源管理器的 WorkerW 失败，记不到画壁纸的 WorkerW 出现和消失");
		}
	}

	/// <summary>
	/// 资源管理器窗口的显示、隐藏、销毁事件（界面线程）。只认顶层、鼠标穿透的 WorkerW，其余事件只做一次类名或字典判断就返回。
	/// </summary>
	static void OnWorkerEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
	{
		// 由系统直接回调，异常必须就地捕获，否则进程会被终止
		try
		{
			if (objectId != 0 || childId != 0 || hwnd == IntPtr.Zero)
			{
				return;
			}
			if (eventType != EVENT_OBJECT_SHOW)
			{
				if (_specialWorkers.Remove(hwnd, out var since))
				{
					string how = eventType == EVENT_OBJECT_DESTROY ? "销毁" : "隐藏";
					_ = Task.Run(() => OnSpecialWorkerGone(hwnd, how, since));
				}
				return;
			}
			if (GetClassName(hwnd) != "WorkerW" || GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow()
					|| (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) == 0)
			{
				return;
			}
			if (_specialWorkers.TryAdd(hwnd, DateTime.Now))
			{
				var foreground = GetForegroundWindow();
				_ = Task.Run(() => OnSpecialWorkerShown(hwnd, foreground));
			}
		}
		catch (Exception ex)
		{
			Log.Warn("处理资源管理器 WorkerW 事件失败", ex);
		}
	}

	/// <summary>
	/// 特殊 WorkerW 出现：记它的样式、范围，当时的前台，最近的各类触发，挂在它下面的其他进程的窗口和快照（30 秒内多次出现时只记精简快照），
	/// 再盯 5 秒桌面窗口的变化。
	/// </summary>
	static async Task OnSpecialWorkerShown(IntPtr hwnd, IntPtr foreground)
	{
		try
		{
			bool full;
			lock (Gate)
			{
				full = DateTime.Now - _lastSpecialSnapshot > TimeSpan.FromSeconds(30);
				if (full)
				{
					_lastSpecialSnapshot = DateTime.Now;
				}
			}
			var rect = GetWindowRect(hwnd);
			GetWindowThreadProcessId(foreground, out uint foregroundProcess);
			Log.Info($"出现鼠标穿透的 WorkerW 0x{hwnd.ToInt64():X}：style=0x{GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64():X} exstyle=0x{GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64():X}"
					+ $" rect=({rect.Left},{rect.Top},{rect.Width},{rect.Height})；当时前台 {Describe(foreground)}（pid={foregroundProcess}）；最近的触发：{RecentEvents()}；"
					+ $"挂在它下面的其他进程窗口：{ForeignChildren(hwnd)}；{Snapshot(!full)}");
			await WatchAsync(hwnd);
		}
		catch (Exception ex)
		{
			Log.Warn("记录 WorkerW 出现失败", ex);
		}
	}

	static void OnSpecialWorkerGone(IntPtr hwnd, string how, DateTime since)
	{
		try
		{
			Log.Info($"鼠标穿透的 WorkerW 0x{hwnd.ToInt64():X} 已{how}，存在 {(DateTime.Now - since).TotalMilliseconds:F0} 毫秒；{Snapshot(true)}");
		}
		catch (Exception ex)
		{
			Log.Warn("记录 WorkerW 消失失败", ex);
		}
		// 用户日志里黑屏是在它消失之后，这时检测一轮（只采样、记日志）
		Schedule("鼠标穿透的 WorkerW 消失", false);
	}

	/// <summary>
	/// 特殊 WorkerW 出现后 5 秒内每 100 毫秒看一次桌面窗口（Progman、WorkerW 的上下次序、可见性、样式，图标列表是否可见），变了才记，最多 30 条。
	/// </summary>
	static async Task WatchAsync(IntPtr special)
	{
		var start = DateTime.Now;
		string? previous = null;
		int lines = 0;
		while (DateTime.Now - start < TimeSpan.FromSeconds(5) && lines < 30 && !_stopped)
		{
			string state = WatchState(special);
			if (state != previous)
			{
				Log.Info($"WorkerW 0x{special.ToInt64():X} 出现后 {(DateTime.Now - start).TotalMilliseconds:F0} 毫秒：{state}");
				previous = state;
				lines++;
			}
			await Task.Delay(100);
		}
	}

	static string WatchState(IntPtr special)
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		GetWindowThreadProcessId(progman, out uint explorer);
		var parts = new List<string>();
		var listView = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			string className = GetClassName(hwnd);
			if (className is not ("Progman" or "WorkerW"))
			{
				return true;
			}
			var view = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
			bool visible = IsWindowVisible(hwnd);
			if (view != IntPtr.Zero)
			{
				listView = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
			}
			if (!visible && view == IntPtr.Zero && hwnd != special)
			{
				return true;
			}
			GetWindowThreadProcessId(hwnd, out uint process);
			parts.Add($"{className} 0x{hwnd.ToInt64():X}{(visible ? "" : "（隐藏）")}{(view != IntPtr.Zero ? "［图标视图］" : "")}{(process != explorer ? $"（pid={process}）" : "")}"
					+ $" style=0x{GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64():X} exstyle=0x{GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64():X}");
			return true;
		}, IntPtr.Zero);
		if (!IsWindow(special))
		{
			parts.Add($"0x{special.ToInt64():X} 已销毁");
		}
		string list = listView == IntPtr.Zero ? "找不到" : IsWindowVisible(listView) ? "可见" : "隐藏";
		return $"{string.Join(" > ", parts)}；图标列表{list}";
	}

	/// <summary>
	/// 挂在 hwnd 下面、不属于资源管理器的子窗口（动态壁纸软件会把自己的窗口挂在画壁纸的 WorkerW 下面）。
	/// </summary>
	static string ForeignChildren(IntPtr hwnd)
	{
		GetWindowThreadProcessId(hwnd, out uint explorer);
		var children = new List<string>();
		for (var child = FindWindowEx(hwnd, IntPtr.Zero, null, null); child != IntPtr.Zero && children.Count < 5; child = FindWindowEx(hwnd, child, null, null))
		{
			GetWindowThreadProcessId(child, out uint process);
			if (process != explorer)
			{
				children.Add($"{Describe(child)}（pid={process}）");
			}
		}
		return children.Count == 0 ? "无" : string.Join("、", children);
	}

	/// <summary>
	/// 最近 10 分钟内各类触发距现在多久，按时间从近到远。
	/// </summary>
	static string RecentEvents()
	{
		lock (Gate)
		{
			var now = DateTime.Now;
			var recent = _lastEvents.Where(e => now - e.Value < TimeSpan.FromMinutes(10)).OrderByDescending(e => e.Value)
					.Select(e => $"{e.Key} {(now - e.Value).TotalSeconds:F1} 秒前").ToList();
			return recent.Count == 0 ? "10 分钟内没有" : string.Join("、", recent);
		}
	}

	public static void Stop()
	{
		lock (Gate)
		{
			_stopped = true;
			_layoutTimer?.Dispose();
			_layoutTimer = null;
			_debounce?.Dispose();
			_debounce = null;
		}
		Interlocked.Increment(ref _probeGeneration);
		// Stop 在界面线程上调用，和装钩子的是同一个线程
		if (_workerHook != IntPtr.Zero)
		{
			UnhookWinEvent(_workerHook);
			_workerHook = IntPtr.Zero;
		}
	}

	/// <summary>
	/// 解锁、唤醒、显示器或壁纸变化等事件：防抖后检测一轮。
	/// </summary>
	public static void Trigger(string reason) => Schedule(reason, false);

	/// <summary>
	/// 前台切换（界面线程上的 WinEvent 回调）：前台换成桌面或任务栏时可能是点了显示桌面，防抖后确认桌面在最上层再检测。
	/// 这里只看类名，重活都在防抖之后的线程池上做。两层布局下显示桌面不换宿主，只能靠这里发现。
	/// </summary>
	public static void OnForegroundChanged(IntPtr hwnd)
	{
		if (hwnd != IntPtr.Zero && (GetClassName(hwnd) == "Shell_TrayWnd" || DesktopHost.IsDesktopSurface(hwnd)))
		{
			Schedule("显示桌面", true);
		}
	}

	/// <summary>
	/// 图标视图换了宿主。可能在钩子线程上调用，快照和检测都放到线程池上做，不拖住调用方。
	/// </summary>
	public static void OnHostChanged(IntPtr previous, IntPtr current)
	{
		int skipped = TakeSnapshotSlot();
		if (skipped >= 0)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					string note = skipped > 0 ? $"（上一份快照之后另有 {skipped} 次换宿主没记）" : "";
					Log.Info($"桌面宿主变化：{Describe(previous)} → {Describe(current)}{note}；{Snapshot(false)}");
					// 搬家的瞬间旧宿主可能还显示着，1 秒后再看一眼它有没有藏起来
					await Task.Delay(1000);
					Log.Info($"换宿主 1 秒后：{Snapshot(true)}");
				}
				catch (Exception ex)
				{
					Log.Warn("记录桌面窗口诊断信息失败", ex);
				}
			});
		}
		Schedule("换宿主", false);
	}

	/// <summary>
	/// 能否记一份完整快照：能的话返回之前跳过的次数，否则返回 -1 并计入跳过次数。
	/// </summary>
	static int TakeSnapshotSlot()
	{
		lock (Gate)
		{
			var now = DateTime.Now;
			if (now - _lastSnapshot < SnapshotInterval)
			{
				_skippedSnapshots++;
				return -1;
			}
			_lastSnapshot = now;
			int skipped = _skippedSnapshots;
			_skippedSnapshots = 0;
			return skipped;
		}
	}

	static void Schedule(string reason, bool needsDesktopOnTop)
	{
		lock (Gate)
		{
			if (_stopped)
			{
				return;
			}
			_pendingReasons.Add(reason);
			_lastEvents[reason] = DateTime.Now;
			_pendingNeedsDesktopOnTop &= needsDesktopOnTop;
			_debounce ??= new Timer(_ => OnDebounced());
			_debounce.Change(DebounceMilliseconds, Timeout.Infinite);
		}
	}

	static void OnDebounced()
	{
		string reason;
		bool needsDesktopOnTop;
		lock (Gate)
		{
			if (_stopped || _pendingReasons.Count == 0)
			{
				return;
			}
			reason = string.Join("、", _pendingReasons);
			needsDesktopOnTop = _pendingNeedsDesktopOnTop;
			_pendingReasons.Clear();
			_pendingNeedsDesktopOnTop = true;
		}
		try
		{
			// 前台换成任务栏多半只是切换应用，桌面没被提上来就不用检测
			if (needsDesktopOnTop && !DesktopHost.IsDesktopOnTop())
			{
				// 前台已经是桌面、桌面却不算在最上层：记下压在桌面上面的窗口，免得像用户日志那样显示桌面后一条检测结果都没有还不知道为什么
				if (DesktopHost.IsDesktopSurface(GetForegroundWindow()))
				{
					LogSkip(reason, new Sample(null, 0, 0, 0, "", "notop", $"前台是桌面，但桌面上面还有窗口，不检测：{WindowAboveDesktop()}"));
				}
				return;
			}
			int generation = Interlocked.Increment(ref _probeGeneration);
			_ = ProbeAsync(generation, reason);
		}
		catch (Exception ex)
		{
			Log.Warn("检测桌面背景失败", ex);
		}
	}

	/// <summary>
	/// 按 ProbeDelays 依次检测背景；又来了新的一轮（generation 变了）就交给它。
	/// </summary>
	static async Task ProbeAsync(int generation, string reason)
	{
		try
		{
			int elapsed = 0;
			bool reported = false;
			foreach (int delay in ProbeDelays)
			{
				await Task.Delay(delay - elapsed);
				elapsed = delay;
				if (Volatile.Read(ref _probeGeneration) != generation)
				{
					return;
				}
				var sample = SampleDesktop();
				string when = $"{reason}后约 {delay + DebounceMilliseconds} 毫秒";
				if (sample.Black == null)
				{
					LogSkip(when, sample);
					continue;
				}
				// 每轮记一次结果（不黑的至多一分钟一条），之后只在变黑、不再黑时记
				if (!reported && sample.Black == false && ShouldLogResult())
				{
					Log.Info($"桌面背景检测（{when}）：{sample}");
				}
				reported = true;
				Handle(when, sample, delay == ProbeDelays[^1]);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("检测桌面背景失败", ex);
		}
	}

	/// <summary>
	/// 根据一次下了结论的采样更新黑屏状态，只记日志、不做任何处理：刚变黑时记警告（连同布局、快照和最近的触发），
	/// 恢复了记一条，最后一次采样仍黑再记一条。
	/// </summary>
	static void Handle(string when, Sample sample, bool last)
	{
		DateTime? since;
		var layout = sample.Black == true ? ReadLayout() : null;
		// 前后两轮检测可能短暂重叠，黑屏状态在锁里读写
		lock (Gate)
		{
			since = _blackSince;
			_blackSince = sample.Black == true ? since ?? DateTime.Now : null;
		}
		if (sample.Black == true && since == null)
		{
			Log.Warn($"疑似桌面背景变成纯黑（{when}）：{sample}；布局：{layout}；最近的触发：{RecentEvents()}；{Snapshot(false)}");
		}
		else if (sample.Black == false && since is DateTime start)
		{
			Log.Info($"桌面背景已不再是纯黑（距发现变黑 {(DateTime.Now - start).TotalSeconds:F1} 秒，{when}）：{sample}");
		}
		else if (sample.Black == true && last)
		{
			Log.Warn($"桌面背景仍是纯黑（{when}）；布局：{layout}；{Snapshot(true)}");
		}
	}

	/// <summary>
	/// 定时检查布局：变了就记一行和快照；进入两层布局时检测一轮；两层布局下每次顺便采样一次，只在变黑、恢复时记日志。
	/// </summary>
	static void CheckLayout()
	{
		try
		{
			var layout = ReadLayout();
			// 资源管理器重启后 Progman 换了进程，WorkerW 事件钩子要在界面线程上重装
			var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
			if (GetWindowThreadProcessId(progman, out uint explorer) != 0 && explorer != _hookedExplorer)
			{
				_dispatcher?.BeginInvoke(HookExplorer);
			}
			Layout? previous;
			lock (Gate)
			{
				previous = _lastLayout;
				_lastLayout = layout;
			}
			if (previous == null || previous != layout)
			{
				string from = previous == null ? "启动后第一次检查" : $"原来是 {previous}";
				Log.Info($"桌面窗口布局：{layout}（{from}）；{Snapshot(false)}");
				if (layout.Kind == LayoutKind.TwoLayer)
				{
					Schedule("进入两层布局", false);
				}
				return;
			}
			if (layout.Kind == LayoutKind.TwoLayer)
			{
				var sample = SampleDesktop();
				if (sample.Black != null)
				{
					Handle("两层布局下定时检测", sample, false);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("检查桌面窗口布局失败", ex);
		}
	}

	/// <summary>
	/// 按 Z 序从上到下找装着图标视图的顶层窗口，再看它下面有没有画壁纸的 WorkerW（资源管理器的、可见、不装图标视图、铺满虚拟屏幕）。
	/// </summary>
	static Layout ReadLayout()
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		GetWindowThreadProcessId(progman, out uint explorer);
		int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
		int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
		var host = IntPtr.Zero;
		var wallpaper = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			bool hasView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
			if (host == IntPtr.Zero)
			{
				if (hasView)
				{
					host = hwnd;
				}
				return true;
			}
			if (!hasView && IsWindowVisible(hwnd) && GetClassName(hwnd) == "WorkerW" && GetWindowThreadProcessId(hwnd, out uint process) != 0 && process == explorer)
			{
				var rect = GetWindowRect(hwnd);
				if (rect.Width >= width && rect.Height >= height)
				{
					wallpaper = hwnd;
					return false;
				}
			}
			return true;
		}, IntPtr.Zero);
		if (host == IntPtr.Zero)
		{
			return new Layout(LayoutKind.Unknown, IntPtr.Zero, IntPtr.Zero);
		}
		if (GetClassName(host) != "WorkerW")
		{
			return new Layout(LayoutKind.Progman, host, IntPtr.Zero);
		}
		return wallpaper == IntPtr.Zero ? new Layout(LayoutKind.SimpleWorker, host, IntPtr.Zero) : new Layout(LayoutKind.TwoLayer, host, wallpaper);
	}

	static bool ShouldLogResult()
	{
		lock (Gate)
		{
			var now = DateTime.Now;
			if (now - _lastResultLog < ResultLogInterval)
			{
				return false;
			}
			_lastResultLog = now;
			return true;
		}
	}

	/// <summary>
	/// 记一条没下结论的检测：同类原因 SkipLogInterval 内只记一条，期间被压下的次数带在下一条里，不整条吞掉。
	/// </summary>
	static void LogSkip(string when, Sample sample)
	{
		var key = sample.SkipKey ?? "";
		int suppressed;
		lock (Gate)
		{
			var now = DateTime.Now;
			if (_skipLogged.TryGetValue(key, out var last) && now - last < SkipLogInterval)
			{
				_skipSuppressed[key] = _skipSuppressed.GetValueOrDefault(key) + 1;
				return;
			}
			_skipLogged[key] = now;
			suppressed = _skipSuppressed.GetValueOrDefault(key);
			_skipSuppressed.Remove(key);
		}
		string note = suppressed > 0 ? $"（上一条之后另有 {suppressed} 次同类没下结论）" : "";
		Log.Info($"桌面背景检测（{when}）{sample}{note}（同类原因 {SkipLogInterval.TotalMinutes:0} 分钟内只记一条）");
	}

	/// <summary>
	/// 按 DesktopHost.IsDesktopOnTop 的标准，桌面窗口上面第一个让它不算在最上层的窗口。
	/// </summary>
	static string WindowAboveDesktop()
	{
		var desktop = DesktopHost.FindDesktopWindow();
		uint own = (uint)Environment.ProcessId;
		for (var above = GetWindow(desktop, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
		{
			long exStyle = GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64();
			if ((exStyle & WS_EX_TOPMOST) != 0)
			{
				return "无（再往上都是置顶窗口）";
			}
			GetWindowThreadProcessId(above, out uint process);
			if (process == own || !IsWindowVisible(above) || IsIconic(above))
			{
				continue;
			}
			if (DwmGetWindowAttribute(above, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
			{
				continue;
			}
			return DescribeWindow(new WindowInfo(above, GetWindowRect(above), exStyle, process, false), null);
		}
		return "无";
	}

	/// <summary>
	/// 采样时用的顶层窗口信息，按 Z 序从上到下。
	/// </summary>
	sealed record WindowInfo(IntPtr Hwnd, RECT Rect, long ExStyle, uint Process, bool Own);

	/// <summary>
	/// 可见、没最小化、没被 DWM 藏起来（cloak）的顶层窗口，按 Z 序从上到下。看不见的窗口不会挡住桌面，这里先去掉。
	/// </summary>
	static List<WindowInfo> TopLevelWindows()
	{
		uint own = (uint)Environment.ProcessId;
		var list = new List<WindowInfo>();
		EnumWindows((hwnd, _) =>
		{
			if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
			{
				return true;
			}
			if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
			{
				return true;
			}
			GetWindowThreadProcessId(hwnd, out uint process);
			list.Add(new WindowInfo(hwnd, GetWindowRect(hwnd), GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64(), process, process == own));
			return true;
		}, IntPtr.Zero);
		return list;
	}

	/// <summary>
	/// 从上往下找该点上第一个看得见的窗口：是桌面表面就返回它，否则返回 0 并把挡住的窗口记进 blocking。
	/// 以前直接用 WindowFromPoint，它按鼠标命中测试找窗口，会返回看不见却接收鼠标的窗口（用户日志：被 Qt51511QWindowIcon 挡住、
	/// 一个露出桌面的点都没有，用户却看得见黑色的桌面），所以改成自己按 Z 序判断，越过的看不见的窗口记进 passed：
	/// 鼠标穿透（WS_EX_TRANSPARENT）的、整体透明度为 0 的分层窗口；逐像素透明的分层窗口读不到透明度，按系统命中测试（WindowFromPoint）
	/// 该点不落在它身上就算透明。本程序的图标层只有图标处算挡住。
	/// </summary>
	static IntPtr DesktopAt(POINT point, List<WindowInfo> windows, Dictionary<IntPtr, string> blocking, Dictionary<IntPtr, string> passed)
	{
		foreach (var window in windows)
		{
			if (!window.Rect.Contains(point))
			{
				continue;
			}
			if (DesktopHost.IsDesktopSurface(window.Hwnd))
			{
				return window.Hwnd;
			}
			if (window.Own && DesktopHost.IsLayer(window.Hwnd))
			{
				if (DesktopHost.HitsLayerIcon(point))
				{
					blocking.TryAdd(window.Hwnd, DescribeWindow(window, "本程序图标层的图标"));
					return IntPtr.Zero;
				}
				continue;
			}
			if ((window.ExStyle & WS_EX_TRANSPARENT) != 0)
			{
				passed.TryAdd(window.Hwnd, DescribeWindow(window, "鼠标穿透"));
				continue;
			}
			if ((window.ExStyle & WS_EX_LAYERED) != 0)
			{
				if (GetLayeredWindowAttributes(window.Hwnd, out _, out byte alpha, out uint flags))
				{
					if ((flags & LWA_ALPHA) != 0 && alpha == 0)
					{
						passed.TryAdd(window.Hwnd, DescribeWindow(window, "分层窗口整体透明度 0"));
						continue;
					}
				}
				else if (GetAncestor(WindowFromPoint(point), GA_ROOT) != window.Hwnd)
				{
					passed.TryAdd(window.Hwnd, DescribeWindow(window, "逐像素透明的分层窗口，该点不接收鼠标"));
					continue;
				}
			}
			blocking.TryAdd(window.Hwnd, DescribeWindow(window, null));
			return IntPtr.Zero;
		}
		return IntPtr.Zero;
	}

	/// <summary>
	/// 日志里描述一个窗口：类名、进程名和 pid、范围、样式、分层窗口的透明度。
	/// </summary>
	static string DescribeWindow(WindowInfo window, string? why)
	{
		string process;
		try
		{
			using var info = System.Diagnostics.Process.GetProcessById((int)window.Process);
			process = info.ProcessName;
		}
		catch (Exception)
		{
			// 进程已退出或无权访问时只记 pid
			process = "?";
		}
		string layered = "";
		if ((window.ExStyle & WS_EX_LAYERED) != 0)
		{
			layered = GetLayeredWindowAttributes(window.Hwnd, out _, out byte alpha, out uint flags)
					? $" 分层 alpha={((flags & LWA_ALPHA) != 0 ? alpha.ToString() : "未设")} flags=0x{flags:X}"
					: " 分层（逐像素透明）";
		}
		var r = window.Rect;
		long style = GetWindowLongPtr(window.Hwnd, GWL_STYLE).ToInt64();
		return $"{(why == null ? "" : why + " ")}{GetClassName(window.Hwnd)} 0x{window.Hwnd.ToInt64():X} {process}(pid={window.Process}) rect=({r.Left},{r.Top},{r.Width},{r.Height})"
				+ $" style=0x{style:X} exstyle=0x{window.ExStyle:X}{layered}";
	}

	/// <summary>
	/// 从屏幕取每个显示器工作区里 4×4 个点，只留露出桌面的（按 Z 序最上面的看得见的窗口是桌面表面，见 DesktopAt）。
	/// 动态壁纸（GDI 截屏是黑的，见 constraints）、纯黑的纯色背景、能读的露出桌面的点不到 3 个时不下结论，原因写在结果里。
	/// </summary>
	static Sample SampleDesktop()
	{
		if (WallpaperBlur.HasLiveWallpaper())
		{
			return new Sample(null, 0, 0, 0, "", "live", "动态壁纸，GDI 截屏不准，跳过");
		}
		if (IsBlackSolidBackground())
		{
			return new Sample(null, 0, 0, 0, "", "black", "背景就是纯黑的纯色，跳过");
		}
		var windows = TopLevelWindows();
		var points = new List<POINT>();
		var covering = new HashSet<string>();
		// 挡住采样点的窗口、判断时越过的看不见的窗口：各按窗口去重，日志里只列前几个
		var blocking = new Dictionary<IntPtr, string>();
		var passed = new Dictionary<IntPtr, string>();
		int total = 0;
		EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
		{
			var (work, _) = GetMonitorWorkArea(monitor);
			for (int row = 0; row < 4; row++)
			{
				for (int column = 0; column < 4; column++)
				{
					total++;
					var point = new POINT(work.Left + work.Width * (2 * column + 1) / 8, work.Top + work.Height * (2 * row + 1) / 8);
					var surface = DesktopAt(point, windows, blocking, passed);
					if (surface != IntPtr.Zero)
					{
						points.Add(point);
						covering.Add(GetClassName(surface));
					}
				}
			}
			return true;
		}, IntPtr.Zero);
		string coveringText = string.Join("、", covering);
		if (passed.Count > 0)
		{
			coveringText += $"；越过的看不见的窗口：{string.Join("；", passed.Values.Take(3))}{(passed.Count > 3 ? $" 等 {passed.Count} 个" : "")}";
		}
		if (points.Count < 3)
		{
			string blockers = string.Join("；", blocking.Values.Take(3)) + (blocking.Count > 3 ? $" 等 {blocking.Count} 个" : "");
			string extra = coveringText.Length > 0 ? $"；点上的桌面窗口：{coveringText}" : "";
			return new Sample(null, 0, 0, 0, coveringText, "covered", $"露出桌面的点只有 {points.Count} 个（共 {total} 个点，挡住的窗口：{blockers}{extra}）");
		}
		var screen = GetDC(IntPtr.Zero);
		if (screen == IntPtr.Zero)
		{
			return new Sample(null, 0, 0, 0, coveringText, "dc", "取不到屏幕 DC");
		}
		try
		{
			int sampled = 0;
			bool black = true;
			uint darkest = 0;
			uint brightest = 0;
			int darkestSum = int.MaxValue;
			int brightestSum = -1;
			foreach (var point in points)
			{
				uint color = GetPixel(screen, point.X, point.Y);
				if (color == CLR_INVALID)
				{
					continue;
				}
				sampled++;
				color &= 0xFFFFFF;
				black &= color == 0;
				int sum = (int)((color & 0xFF) + ((color >> 8) & 0xFF) + ((color >> 16) & 0xFF));
				if (sum < darkestSum)
				{
					darkestSum = sum;
					darkest = color;
				}
				if (sum > brightestSum)
				{
					brightestSum = sum;
					brightest = color;
				}
			}
			if (sampled < 3)
			{
				return new Sample(null, sampled, 0, 0, coveringText, "pixel", $"{points.Count} 个露出桌面的点里只读出 {sampled} 个像素（CLR_INVALID，可能在锁屏或安全桌面上）");
			}
			return new Sample(black, sampled, darkest, brightest, coveringText);
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
	/// 本程序的图标层和最上面的其他窗口的位置，图标视图的宿主、可见性和范围，图标列表是否可见，前台窗口。只读窗口属性、不发消息。
	/// compact 时只列 Progman 和 WorkerW 的可见性和位置。
	/// </summary>
	static string Snapshot(bool compact)
	{
		var progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		GetWindowThreadProcessId(progman, out uint explorer);
		uint own = (uint)Environment.ProcessId;
		var lines = new List<string>();
		int index = 0;
		int hiddenWorkers = 0;
		int layerIndex = -1;
		string ownTop = "无";
		var defViewParent = IntPtr.Zero;
		var defView = IntPtr.Zero;
		var listView = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			int position = index++;
			GetWindowThreadProcessId(hwnd, out uint process);
			bool visible = IsWindowVisible(hwnd);
			if (process == own && visible)
			{
				if (DesktopHost.IsLayer(hwnd))
				{
					layerIndex = layerIndex < 0 ? position : layerIndex;
				}
				else if (ownTop == "无")
				{
					ownTop = $"[{position}] {GetClassName(hwnd)}";
				}
				return true;
			}
			string className = GetClassName(hwnd);
			if (className is not ("Progman" or "WorkerW"))
			{
				return true;
			}
			var view = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (view != IntPtr.Zero)
			{
				defViewParent = hwnd;
				defView = view;
				listView = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
			}
			if (className == "WorkerW" && !visible && view == IntPtr.Zero)
			{
				hiddenWorkers++;
				return true;
			}
			if (compact)
			{
				lines.Add($"[{position}] {className} 0x{hwnd.ToInt64():X} {(visible ? "可见" : "隐藏")}{(view != IntPtr.Zero ? " 装着图标视图" : "")}");
				return true;
			}
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
			var rect = GetWindowRect(hwnd);
			long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
			long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
			lines.Add($"[{position}] {className} 0x{hwnd.ToInt64():X} pid={process}{(process != explorer ? "（不是资源管理器）" : "")}"
					+ $" {(visible ? "可见" : "隐藏")}{(cloaked != 0 ? $" DWM 隐藏 {cloaked}" : "")} rect=({rect.Left},{rect.Top},{rect.Width},{rect.Height})"
					+ $" style=0x{style:X} exstyle=0x{exStyle:X}{(view != IntPtr.Zero ? " 装着图标视图" : "")}{DescribeChildWorkers(hwnd)}");
			return true;
		}, IntPtr.Zero);
		if (compact)
		{
			return $"{string.Join(" > ", lines)}；另有 {hiddenWorkers} 个隐藏的 WorkerW";
		}
		var foreground = GetForegroundWindow();
		GetWindowThreadProcessId(foreground, out uint foregroundProcess);
		string list = listView == IntPtr.Zero ? "找不到" : IsWindowVisible(listView) ? "可见" : "隐藏";
		var viewRect = GetWindowRect(defView);
		// 图标视图和图标列表的样式也记下：黑屏时退出本程序（重新显示列表）壁纸立刻回来，要看列表藏着时它们是怎么画的
		string viewState = defView == IntPtr.Zero ? "" : $"（{(IsWindowVisible(defView) ? "可见" : "隐藏")} rect=({viewRect.Left},{viewRect.Top},{viewRect.Width},{viewRect.Height})"
				+ $" style=0x{GetWindowLongPtr(defView, GWL_STYLE).ToInt64():X} exstyle=0x{GetWindowLongPtr(defView, GWL_EXSTYLE).ToInt64():X}"
				+ (listView == IntPtr.Zero ? "" : $"；图标列表 style=0x{GetWindowLongPtr(listView, GWL_STYLE).ToInt64():X} exstyle=0x{GetWindowLongPtr(listView, GWL_EXSTYLE).ToInt64():X}") + "）";
		return $"桌面窗口（共 {index} 个顶层窗口）：{string.Join(" > ", lines)}；另有 {hiddenWorkers} 个隐藏的 WorkerW；"
				+ $"图标视图在 {Describe(defViewParent)}{viewState}，图标列表{list}；本程序图标层 {(layerIndex < 0 ? "无" : $"[{layerIndex}]")}，本程序最上面的其他窗口 {ownTop}；"
				+ $"前台 {Describe(foreground)}（pid={foregroundProcess}）";
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
