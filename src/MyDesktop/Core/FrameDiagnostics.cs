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
	static int _composedThisFrame;
	static int _composed;

	static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);
	static readonly FieldInfo? ArgsField = typeof(DispatcherOperation).GetField("_args", BindingFlags.NonPublic | BindingFlags.Instance);

	/// <summary>
	/// 帧时钟启动时在界面线程上调用。
	/// </summary>
	public static void Begin(Dispatcher dispatcher)
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
		LateFrames.Clear();
		SlowOperations.Clear();
		Timed.Clear();
	}

	/// <summary>
	/// 飞行图标摆放了一次；composed 表示重新合成了画面。
	/// </summary>
	public static void Place(bool composed)
	{
		_hadFlights = true;
		_placedThisFrame++;
		if (composed)
		{
			_composedThisFrame++;
			_composed++;
		}
	}

	/// <summary>
	/// 计时一段代码，using 结束时记下；只在界面线程上用。
	/// </summary>
	public static Scope Time(string name) => new(name, Stopwatch.GetTimestamp());

	internal readonly struct Scope(string name, long start) : IDisposable
	{
		public void Dispose()
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

	/// <summary>
	/// 一帧开始在界面线程上完成任务之前调用：flushedAt 是后台线程等到合成的时刻，flushes 是这一帧等了几次 DwmFlush。
	/// </summary>
	public static void FrameStart(long flushedAt, int flushes, long start)
	{
		if (!_active)
		{
			return;
		}
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
		Placed.Add(_placedThisFrame);
	}

	/// <summary>
	/// 帧时钟退出时调用，汇总写日志。
	/// </summary>
	public static void End()
	{
		if (!_active)
		{
			return;
		}
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
			$"[诊断]   界面线程上一帧的耗时 {Stats(Works)}；每帧摆放图标 平均 {Placed.Average():F1}、最多 {Placed.Max()}",
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

	static void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
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
