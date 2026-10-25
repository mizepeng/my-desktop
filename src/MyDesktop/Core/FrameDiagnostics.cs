using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace MyDesktop.Core;

/// <summary>
/// 【临时诊断，排查飞行动画掉帧用，查清后删除】
/// 帧时钟每次运行期间统计每帧的节奏和耗时、界面线程上耗时长的调度器操作，结束时汇总写进日志（[诊断] 开头）。
/// 只有期间开过飞行图标才写。
/// </summary>
internal static class FrameDiagnostics
{
	/// <summary>
	/// 界面线程上的调度器操作超过这么久才记下来（毫秒）。
	/// </summary>
	const double SlowOperationMs = 3;

	/// <summary>
	/// 两帧间隔超过这么久算一次晚到的帧，记下明细（毫秒）。
	/// </summary>
	const double LateFrameMs = 12;

	// 帧时钟线程和界面线程（调度器钩子、计时）都会写，汇总和清空时用它互斥
	static readonly object Gate = new();
	static bool _active;
	static bool _hooked;
	static bool _hadFlights;
	static long _sessionStart;
	static long _lastFrameStart;
	static int _gen0;
	static int _gen1;
	static int _gen2;
	static TimeSpan _gcPause;
	static readonly List<double> Intervals = [];
	static readonly List<double> Latencies = [];
	static readonly List<double> Works = [];
	static readonly List<int> Flushes = [];
	static readonly List<int> Placed = [];
	static readonly List<string> LateFrames = [];
	static readonly List<(double At, double Ms, string Name)> SlowOperations = [];
	static readonly Dictionary<string, (int Count, double Total, double Max)> Timed = [];
	static readonly Stack<long> OperationStarts = new();
	static int _placedThisFrame;
	static long _frameFlushedAt;
	static long _firstPlace;
	static long _lastPlace;
	static readonly List<double> Spreads = [];
	static readonly List<double> PlaceTimes = [];
	static readonly List<double> UlwTimes = [];
	static readonly List<double> ComposeTimes = [];
	static readonly List<double> PlaceTimesComposed = [];
	static readonly List<double> Lags = [];
	static readonly List<int> Straddles = [];
	static readonly List<int> Missed = [];
	// 后台线程每次 DwmFlush 返回的时刻（环形），用来看一帧里的摆放有没有跨过一次合成
	static readonly long[] FlushRing = new long[8192];
	static int _flushCount;
	static int _composedThisFrame;
	static int _composed;

	static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);
	static readonly FieldInfo? ArgsField = typeof(DispatcherOperation).GetField("_args", BindingFlags.NonPublic | BindingFlags.Instance);

	/// <summary>
	/// 帧时钟启动时在界面线程上调用。
	/// </summary>
	public static void Begin(Dispatcher dispatcher)
	{
		lock (Gate)
		{
			if (!_hooked)
			{
				_hooked = true;
				dispatcher.Hooks.OperationStarted += (_, _) => OperationStarts.Push(Stopwatch.GetTimestamp());
				dispatcher.Hooks.OperationCompleted += OnOperationCompleted;
				dispatcher.Hooks.OperationAborted += (_, _) =>
				{
					if (OperationStarts.Count > 0)
					{
						OperationStarts.Pop();
					}
				};
			}
			_active = true;
			_hadFlights = false;
			_sessionStart = Stopwatch.GetTimestamp();
			_lastFrameStart = 0;
			_gen0 = GC.CollectionCount(0);
			_gen1 = GC.CollectionCount(1);
			_gen2 = GC.CollectionCount(2);
			_gcPause = GC.GetTotalPauseDuration();
			_composed = 0;
			Intervals.Clear();
			Latencies.Clear();
			Works.Clear();
			Flushes.Clear();
			Placed.Clear();
			Spreads.Clear();
			PlaceTimes.Clear();
			UlwTimes.Clear();
			ComposeTimes.Clear();
			PlaceTimesComposed.Clear();
			Lags.Clear();
			Straddles.Clear();
			Missed.Clear();
			_firstPlace = 0;
			_placedThisFrame = 0;
			LateFrames.Clear();
			SlowOperations.Clear();
			Timed.Clear();
		}
	}

	/// <summary>
	/// 飞行图标摆放了一次；composed 表示重新合成了画面。
	/// </summary>
	public static void Place(bool composed)
	{
		_hadFlights = true;
		_placedThisFrame++;
		long now = Stopwatch.GetTimestamp();
		if (_firstPlace == 0)
		{
			_firstPlace = now;
		}
		_lastPlace = now;
		if (composed)
		{
			_composedThisFrame++;
			_composed++;
		}
	}

	/// <summary>
	/// 一次摆放结束，start 是开始摆放的时刻。
	/// </summary>
	public static void PlaceEnd(long start, bool composed)
	{
		if (_active)
		{
			(composed ? PlaceTimesComposed : PlaceTimes).Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		}
	}

	/// <summary>
	/// UpdateLayeredWindow 或合成画面一次的耗时。
	/// </summary>
	public static void Part(long start, bool ulw)
	{
		if (_active)
		{
			(ulw ? UlwTimes : ComposeTimes).Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		}
	}

	/// <summary>
	/// 后台线程上 DwmFlush 返回时调用。
	/// </summary>
	public static void Flushed()
	{
		int i = Interlocked.Increment(ref _flushCount) - 1;
		Volatile.Write(ref FlushRing[i & (FlushRing.Length - 1)], Stopwatch.GetTimestamp());
	}

	/// <summary>
	/// 结算上一帧的摆放：第一次到最后一次摆放隔了多久、中间跨过几次合成、第一次摆放前是否已经错过了合成。
	/// </summary>
	static void FinishFrame()
	{
		if (_placedThisFrame == 0 || _firstPlace == 0)
		{
			_firstPlace = 0;
			return;
		}
		Placed.Add(_placedThisFrame);
		Spreads.Add(Stopwatch.GetElapsedTime(_firstPlace, _lastPlace).TotalMilliseconds);
		Lags.Add(Stopwatch.GetElapsedTime(_frameFlushedAt, _firstPlace).TotalMilliseconds);
		int count = Volatile.Read(ref _flushCount);
		int straddle = 0;
		int missed = 0;
		for (int i = Math.Max(0, count - 64); i < count; i++)
		{
			long t = Volatile.Read(ref FlushRing[i & (FlushRing.Length - 1)]);
			if (t > _firstPlace && t <= _lastPlace)
			{
				straddle++;
			}
			// 比合成返回晚 0.5 毫秒以上才算这次合成之后
			else if (t > _frameFlushedAt + Stopwatch.Frequency / 2000 && t <= _firstPlace)
			{
				missed++;
			}
		}
		Straddles.Add(straddle);
		Missed.Add(missed);
		_firstPlace = 0;
	}

	/// <summary>
	/// 计时一段代码，using 结束时记下；只在界面线程上用。
	/// </summary>
	public static Scope Time(string name) => new(name, Stopwatch.GetTimestamp());

	internal readonly struct Scope(string name, long start) : IDisposable
	{
		public void Dispose()
		{
			lock (Gate)
			{
				if (!_active)
				{
					return;
				}
				double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
				var (count, total, max) = Timed.GetValueOrDefault(name);
				Timed[name] = (count + 1, total + ms, Math.Max(max, ms));
			}
		}
	}

	/// <summary>
	/// 一帧开始在界面线程上完成任务之前调用：flushedAt 是后台线程等到合成的时刻，flushes 是这一帧等了几次 DwmFlush。
	/// </summary>
	public static void FrameStart(long flushedAt, int flushes, long start)
	{
		if (!_active)
		{
			return;
		}
		FinishFrame();
		_frameFlushedAt = flushedAt;
		_placedThisFrame = 0;
		_composedThisFrame = 0;
		Latencies.Add(Stopwatch.GetElapsedTime(flushedAt, start).TotalMilliseconds);
		Flushes.Add(flushes);
		if (_lastFrameStart != 0)
		{
			double interval = Stopwatch.GetElapsedTime(_lastFrameStart, start).TotalMilliseconds;
			Intervals.Add(interval);
			if (interval > LateFrameMs && LateFrames.Count < 40)
			{
				LateFrames.Add($"{Offset(start):F0}ms 处间隔 {interval:F1}（DwmFlush {flushes} 次，派发延迟 {Latencies[^1]:F1}）");
			}
		}
		_lastFrameStart = start;
	}

	/// <summary>
	/// 一帧的任务都完成（所有飞行图标都摆好）之后调用。
	/// </summary>
	public static void FrameEnd(long start)
	{
		if (!_active)
		{
			return;
		}
		Works.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
	}

	/// <summary>
	/// 帧时钟退出时调用，汇总写日志。
	/// </summary>
	public static void End()
	{
		lock (Gate)
		{
			if (!_active)
			{
				return;
			}
			FinishFrame();
			_active = false;
			if (!_hadFlights || Works.Count < 10)
			{
				return;
			}
			var lines = new List<string>
			{
				$"[诊断] 帧时钟：{Works.Count} 帧，历时 {Offset(Stopwatch.GetTimestamp()):F0} ms，重新合成 {_composed} 次",
				$"[诊断]   间隔 {Stats(Intervals)}",
				$"[诊断]   DwmFlush 次数分布 {string.Join("，", Flushes.GroupBy(f => f).OrderBy(g => g.Key).Select(g => $"{g.Key} 次×{g.Count()}"))}",
				$"[诊断]   合成后到界面线程开始的派发延迟 {Stats(Latencies)}",
				$"[诊断]   界面线程上完成帧任务的耗时 {Stats(Works)}",
				$"[诊断]   有摆放的帧 {Placed.Count} 个，每帧摆放图标 平均 {(Placed.Count > 0 ? Placed.Average() : 0):F1}、最多 {(Placed.Count > 0 ? Placed.Max() : 0)}",
				$"[诊断]   一次摆放耗时：只挪 {Stats(PlaceTimes)}；重新合成 {Stats(PlaceTimesComposed)}",
				$"[诊断]   其中 UpdateLayeredWindow {Stats(UlwTimes)}；合成画面 {Stats(ComposeTimes)}",
				$"[诊断]   合成返回到这一帧第一次摆放 {Stats(Lags)}",
				$"[诊断]   一帧里第一次到最后一次摆放 {Stats(Spreads)}",
				$"[诊断]   一帧的摆放中间跨过合成：{Straddles.Count(c => c > 0)} 帧（{Straddles.Sum()} 次）；第一次摆放前已错过合成：{Missed.Count(c => c > 0)} 帧",
				$"[诊断]   GC 次数 gen0 {GC.CollectionCount(0) - _gen0}、gen1 {GC.CollectionCount(1) - _gen1}、gen2 {GC.CollectionCount(2) - _gen2}，"
						+ $"暂停共 {(GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds:F1} ms",
			};
			foreach (var (name, (count, total, max)) in Timed.OrderByDescending(t => t.Value.Total))
			{
				lines.Add($"[诊断]   计时 {name}：{count} 次，共 {total:F1} ms，最长 {max:F1} ms");
			}
			lines.Add($"[诊断]   晚到的帧（间隔超过 {LateFrameMs} ms）{Intervals.Count(i => i > LateFrameMs)} 次：");
			lines.AddRange(LateFrames.Select(f => "[诊断]     " + f));
			lines.Add($"[诊断]   界面线程上超过 {SlowOperationMs} ms 的调度器操作 {SlowOperations.Count} 个（按时间）：");
			lines.AddRange(SlowOperations.Take(60).Select(o => $"[诊断]     {o.At:F0}ms 处 {o.Ms:F1} ms {o.Name}"));
			Log.Info(string.Join(Environment.NewLine, lines));
		}
	}

	static void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
	{
		lock (Gate)
		{
			if (OperationStarts.Count == 0)
			{
				return;
			}
			long start = OperationStarts.Pop();
			if (!_active)
			{
				return;
			}
			double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			if (ms >= SlowOperationMs && SlowOperations.Count < 400)
			{
				SlowOperations.Add((Offset(start), ms, $"[{e.Operation.Priority}] {Describe(e.Operation)}"));
			}
		}
	}

	static string Describe(DispatcherOperation operation)
	{
		var method = MethodField?.GetValue(operation) as Delegate;
		var args = ArgsField?.GetValue(operation);
		// 异步方法的续体：经 SynchronizationContext 排进来，参数里才是真正要执行的委托
		if (args is Delegate inner)
		{
			return Describe(inner);
		}
		return method == null ? "（取不到委托）" : Describe(method);
	}

	static string Describe(Delegate method)
	{
		var target = method.Target?.GetType();
		// 异步状态机的盒子：最后一个泛型参数是状态机，名字里带方法名
		if (target is { IsGenericType: true } && target.GenericTypeArguments.Length > 0)
		{
			var machine = target.GenericTypeArguments[^1];
			return $"{machine.DeclaringType?.Name}.{machine.Name}";
		}
		return $"{method.Method.DeclaringType?.Name}.{method.Method.Name}";
	}

	static double Offset(long timestamp) => Stopwatch.GetElapsedTime(_sessionStart, timestamp).TotalMilliseconds;

	static string Stats(List<double> values)
	{
		if (values.Count == 0)
		{
			return "（无）";
		}
		var sorted = values.Order().ToList();
		double At(double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
		return $"p50 {At(0.5):F2}、p90 {At(0.9):F2}、p99 {At(0.99):F2}、最大 {sorted[^1]:F2} ms";
	}
}
