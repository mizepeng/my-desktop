using Microsoft.Win32;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 查 Win10 上桌面背景变成纯黑（用户反馈：多在点「显示桌面」时看到，分区和任务栏正常，再点两下显示桌面恢复；不开本程序不黑）。
/// 推测：解锁、登录后资源管理器建出两层 WorkerW 布局（图标视图所在的 WorkerW 下面另有一个画壁纸的 WorkerW），
/// 这时图标视图所在的 WorkerW 露出来的画面靠图标列表画，本程序藏起列表后就没人画、是纯黑；显示桌面只是把它亮出来。
/// 本类负责：
/// - 换宿主、布局变化时记桌面窗口结构的快照（限频）；
/// - 在换宿主、显示桌面、解锁、唤醒、显示器和壁纸变化后，以及两层布局下定时，从屏幕取露出桌面的点检测是否纯黑，没下结论也记原因（限频）；
/// - 确认纯黑且是两层布局时尝试恢复：先重画，不行再重设同一张壁纸，每一步前后都采样记结果；一次黑屏只试一遍，10 分钟内最多 3 遍。
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

	/// <summary>
	/// 恢复的频率上限：RecoveryWindow 内最多 RecoveryLimit 遍。
	/// </summary>
	static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);
	const int RecoveryLimit = 3;

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
	// 这次黑屏是否已经尝试过恢复；看到不黑时清掉
	static bool _recoveryTried;
	static readonly Queue<DateTime> _recoveries = new();
	static DateTime _lastResultLog;
	static readonly Dictionary<string, DateTime> _skipLogged = [];

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
				// 每轮记一次结果（不黑的至多一分钟一条），之后只在变黑、恢复时记
				if (!reported && sample.Black == false && ShouldLogResult())
				{
					Log.Info($"桌面背景检测（{when}）：{sample}");
				}
				reported = true;
				await HandleAsync(when, sample, delay == ProbeDelays[^1]);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("检测桌面背景失败", ex);
		}
	}

	/// <summary>
	/// 根据一次下了结论的采样更新黑屏状态：刚变黑时记警告，是两层布局就尝试恢复；恢复了记一条；最后一次采样仍黑再记一条。
	/// </summary>
	static async Task HandleAsync(string when, Sample sample, bool last)
	{
		DateTime? since;
		bool recover = false;
		var layout = sample.Black == true ? ReadLayout() : null;
		// 前后两轮检测可能短暂重叠，黑屏状态在锁里读写
		lock (Gate)
		{
			since = _blackSince;
			if (sample.Black == true)
			{
				_blackSince = since ?? DateTime.Now;
				if (!_recoveryTried && layout?.Kind == LayoutKind.TwoLayer)
				{
					_recoveryTried = true;
					recover = true;
				}
			}
			else
			{
				_blackSince = null;
				_recoveryTried = false;
			}
		}
		if (sample.Black == true && since == null)
		{
			Log.Warn($"疑似桌面背景变成纯黑（{when}）：{sample}；布局：{layout}；{Snapshot(false)}");
		}
		else if (sample.Black == false && since is DateTime start)
		{
			Log.Info($"桌面背景已不再是纯黑（距发现变黑 {(DateTime.Now - start).TotalSeconds:F1} 秒，{when}）：{sample}");
		}
		else if (sample.Black == true && last)
		{
			Log.Warn($"桌面背景仍是纯黑（{when}）；布局：{layout}；{Snapshot(true)}");
		}
		if (recover && layout != null)
		{
			await RecoverAsync(layout);
		}
	}

	/// <summary>
	/// 两层布局下确认纯黑后尝试恢复，在线程池上执行。台阶 1：重画图标视图所在的 WorkerW（连同子窗口）和画壁纸的 WorkerW，
	/// 只作废、不带 *NOW，不同步等资源管理器；台阶 2：重设同一张壁纸，让资源管理器重画。每一步之后都采样记结果，恢复了就停。
	/// </summary>
	static async Task RecoverAsync(Layout layout)
	{
		lock (Gate)
		{
			var now = DateTime.Now;
			while (_recoveries.Count > 0 && now - _recoveries.Peek() > RecoveryWindow)
			{
				_recoveries.Dequeue();
			}
			if (_recoveries.Count >= RecoveryLimit)
			{
				Log.Warn($"{RecoveryWindow.TotalMinutes:0} 分钟内已尝试恢复 {RecoveryLimit} 次，这次只记日志、不再尝试");
				return;
			}
			_recoveries.Enqueue(now);
		}
		Log.Info($"尝试恢复纯黑的桌面背景，台阶 1：重画 {Describe(layout.Host)} 和 {Describe(layout.Wallpaper)}");
		bool hostRedrawn = RedrawWindow(layout.Host, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
		bool wallpaperRedrawn = RedrawWindow(layout.Wallpaper, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE);
		await Task.Delay(500);
		var sample = SampleDesktop();
		Log.Info($"台阶 1 之后（RedrawWindow 结果：{hostRedrawn}、{wallpaperRedrawn}）：{sample}");
		if (sample.Black == false)
		{
			return;
		}
		var path = WallpaperForReset(out string? skip);
		if (path == null)
		{
			Log.Info($"跳过台阶 2（重设壁纸）：{skip}");
			return;
		}
		Log.Info($"尝试恢复纯黑的桌面背景，台阶 2：重设同一张壁纸 {path}");
		// 不带 SPIF_UPDATEINIFILE：不写用户设置，只让资源管理器按这张图重画一次。会广播 WM_SETTINGCHANGE，本程序自己也会收到、再检测一轮
		if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_SENDCHANGE))
		{
			Log.Warn($"重设壁纸失败（错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）");
			return;
		}
		await Task.Delay(1000);
		Log.Info($"台阶 2 之后 1 秒：{SampleDesktop()}");
		await Task.Delay(2000);
		Log.Info($"台阶 2 之后 3 秒：{SampleDesktop()}");
	}

	/// <summary>
	/// 台阶 2 要重设的壁纸文件：用户设的那张图（Control Panel\Desktop 的 WallPaper）。没设图片、文件不在、
	/// 是资源管理器转码后的缓存、或背景不是单张图片（幻灯片放映等）时返回 null，原因放在 skip。
	/// </summary>
	static string? WallpaperForReset(out string? skip)
	{
		using (var wallpapers = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers"))
		{
			// BackgroundType：0 图片、1 纯色、2 幻灯片放映（推测，没有公开文档）；只在明确是图片或没有这一项时重设
			if (wallpapers?.GetValue("BackgroundType") is int type && type != 0)
			{
				skip = $"背景不是单张图片（BackgroundType={type}，可能是幻灯片放映或纯色）";
				return null;
			}
		}
		using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
		var path = desktop?.GetValue("WallPaper") as string;
		if (string.IsNullOrEmpty(path))
		{
			skip = "读不到壁纸路径";
			return null;
		}
		if (path.Contains("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase))
		{
			skip = $"壁纸路径是资源管理器转码后的缓存：{path}";
			return null;
		}
		if (!File.Exists(path))
		{
			skip = $"壁纸文件不存在：{path}";
			return null;
		}
		skip = null;
		return path;
	}

	/// <summary>
	/// 定时检查布局：变了就记一行和快照；进入两层布局时检测一轮；两层布局下每次顺便采样一次，只在变黑、恢复时记日志。
	/// </summary>
	static void CheckLayout()
	{
		try
		{
			var layout = ReadLayout();
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
					_ = HandleAsync("两层布局下定时检测", sample, false);
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

	static void LogSkip(string when, Sample sample)
	{
		var key = sample.SkipKey ?? "";
		lock (Gate)
		{
			var now = DateTime.Now;
			if (_skipLogged.TryGetValue(key, out var last) && now - last < SkipLogInterval)
			{
				return;
			}
			_skipLogged[key] = now;
		}
		Log.Info($"桌面背景检测（{when}）{sample}（同类原因 {SkipLogInterval.TotalMinutes:0} 分钟内不再记）");
	}

	/// <summary>
	/// 从屏幕取每个显示器工作区里 4×4 个点，只留露出桌面的（WindowFromPoint 落在桌面表面上，分区、图标层上的图标、其他窗口挡住的不算）。
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
		var points = new List<POINT>();
		var covering = new HashSet<string>();
		var blocking = new HashSet<string>();
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
					var hwnd = WindowFromPoint(point);
					var root = GetAncestor(hwnd, GA_ROOT);
					if (DesktopHost.IsDesktopSurface(hwnd) || DesktopHost.IsDesktopSurface(root))
					{
						points.Add(point);
						covering.Add(GetClassName(root));
					}
					else if (blocking.Count < 5)
					{
						blocking.Add(root == IntPtr.Zero ? "无" : GetClassName(root));
					}
				}
			}
			return true;
		}, IntPtr.Zero);
		string coveringText = string.Join("、", covering);
		if (points.Count < 3)
		{
			return new Sample(null, 0, 0, 0, coveringText, "covered", $"露出桌面的点只有 {points.Count} 个（共 {total} 个点，挡住的窗口有 {string.Join("、", blocking)}）");
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
		string viewState = defView == IntPtr.Zero ? "" : $"（{(IsWindowVisible(defView) ? "可见" : "隐藏")} rect=({viewRect.Left},{viewRect.Top},{viewRect.Width},{viewRect.Height})）";
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
