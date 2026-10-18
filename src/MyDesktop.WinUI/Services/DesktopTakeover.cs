using Microsoft.UI.Dispatching;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Services;

internal sealed class DesktopTakeover : IDisposable
{
	readonly DesktopSession _session;
	readonly DesktopSnapshotSource _source;
	readonly DesktopInputHooks _hooks;
	readonly DispatcherQueueTimer _watchdog;
	readonly bool _fixture;
	readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
	readonly TaskCompletionSource _firstActive = new(TaskCreationOptions.RunContinuationsAsynchronously);
	Task _hooksReady = Task.CompletedTask;
	DesktopSnapshot? _snapshot;
	DesktopIconTarget? _target;
	IconRecovery? _recovery;
	DateTime _observedAt;
	DateTime? _staleFilesSince;
	string? _signature;
	bool _busy, _pending, _dirty = true, _restoring, _disposed, _nativeEditing;
	int _generation;

	public DesktopTakeover(DesktopSession session, bool fixture)
	{
		_session = session;
		_fixture = fixture;
		var queue = DispatcherQueue.GetForCurrentThread();
		_source = new DesktopSnapshotSource(queue, fixture, AppPaths.DataDir);
		_source.Observed += Observed;
		_hooks = new DesktopInputHooks(queue);
		_hooks.Command += HandleKey;
		_hooks.BlankPressed += () => Select(null, false);
		_hooks.Failed += error => Release($"桌面输入保护不可用，已恢复系统图标：{error}");
		_hooks.NativeEditDetected += () =>
		{
			_nativeEditing = true;
			Release("系统正在编辑桌面名称，已暂时恢复系统图标。");
		};
		_watchdog = queue.CreateTimer();
		_watchdog.Interval = TimeSpan.FromMilliseconds(250);
		_watchdog.Tick += (_, _) => CheckConnection();
		foreach (var surface in session.Surfaces)
		{
			surface.DesktopIcons.SelectionRequested += Select;
			surface.DesktopIcons.OpenRequested += Open;
		}
	}

	public bool IsActive { get; private set; }
	public Task FirstActive => _firstActive.Task;
	internal int? GuardProcessId => _recovery?.ProcessId;
	internal DesktopSnapshot? Snapshot => _snapshot;
	internal DesktopIconTarget? Target => _target;
	internal IReadOnlyList<string> SelectedKeys => _selected.ToArray();

	public void Start()
	{
		_hooksReady = _hooks.StartAsync();
		_watchdog.Start();
		_source.Start();
	}

	public void Refresh()
	{
		_dirty = true;
		_source.Refresh();
	}

	internal void ItemRenamed(string oldPath, string newPath, bool directory)
	{
		if (!IsActive && _target != null)
		{
			Release(null);
		}
		else
		{
			_generation++;
		}
		var selected = _selected.Select(key => ItemRename.RewritePath(key, oldPath, newPath, directory)).ToArray();
		_selected.Clear();
		_selected.UnionWith(selected);
		Refresh();
	}

	void Observed(DesktopSnapshot? snapshot, string? error)
	{
		_snapshot = snapshot;
		if (snapshot == null)
		{
			Release(error == null ? "等待资源管理器桌面就绪。" : $"桌面读取失败，已保留系统图标：{error}");
			return;
		}
		_observedAt = DateTime.UtcNow;
		if (_busy)
		{
			_pending = true;
		}
		else
		{
			_ = ApplyAsync();
		}
	}

	async Task ApplyAsync()
	{
		if (_disposed || _restoring || _nativeEditing || _snapshot == null)
		{
			return;
		}
		_busy = true;
		int generation = _generation;
		try
		{
			if (_session.FileWatchError is { } watchError)
			{
				throw new InvalidOperationException(watchError + "，请重启后重试。");
			}
			if (!_session.DesktopWatchReady)
			{
				throw new InvalidOperationException("桌面文件夹的监视尚未就绪，已保留系统图标。");
			}
			var target = DesktopIconTarget.Find(_fixture);
			if (target == null || target.HiddenBySystem)
			{
				Release(target == null ? "等待资源管理器桌面就绪。" : null);
				return;
			}
			if (_target != null && target != _target)
			{
				Release(null);
				_pending = true;
				return;
			}
			if (IsActive && !_dirty && _signature == _snapshot.Signature)
			{
				return;
			}
			if (!IsActive && !target.Visible)
			{
				throw new InvalidOperationException("桌面图标已被其他程序隐藏，请先退出另一个桌面接管实例。");
			}
			var snapshot = _snapshot;
			// 文件变化通知可能先于 Explorer 快照，不能按旧路径把已改名成员重新画成散放图标。
			if (snapshot.Items.Any(entry => entry.IsFileSystem && !File.Exists(entry.Key) && !Directory.Exists(entry.Key)))
			{
				_staleFilesSince ??= DateTime.UtcNow;
				if (!IsActive || DateTime.UtcNow - _staleFilesSince >= TimeSpan.FromSeconds(3))
				{
					throw new InvalidOperationException("Explorer 尚未完成文件变化同步，已暂时恢复系统图标。");
				}
				return;
			}
			_staleFilesSince = null;
			var monitors = _session.Surfaces.Select(surface => new DesktopMonitor(surface.WorkArea, surface.Scale)).ToArray();
			var blocked = _session.Surfaces.SelectMany(surface => surface.Fences.Select(fence =>
			{
				var bounds = surface.BoundsOf(fence);
				return new System.Drawing.Rectangle(bounds.Left + surface.WorkArea.Left, bounds.Top + surface.WorkArea.Top, bounds.Width, bounds.Height);
			})).ToArray();
			var placements = DesktopLayout.Build(snapshot, _session.Settings.Fences, blocked, monitors);
			foreach (var surface in _session.Surfaces)
			{
				var items = placements.Where(item =>
				{
					var rectangle = (RECT)item.Bounds;
					return MonitorFromRect(ref rectangle, MONITOR_DEFAULTTONEAREST) == MonitorOf(surface.WorkArea);
				}).ToArray();
				await surface.DesktopIcons.SetItemsAsync(items);
			}
			// 当前桌面分区也要画好，不能只因散放图标已就绪就把分区成员在 Explorer 中一并藏掉。
			var desktopFences = _session.Surfaces.SelectMany(surface => surface.Fences).Where(fence => !fence.Frame.Active.IsPortal).ToArray();
			await Task.WhenAll(desktopFences.Select(fence => fence.ContentReady)).WaitAsync(TimeSpan.FromSeconds(3));
			foreach (var fence in desktopFences)
			{
				var expected = snapshot.Items.Where(entry => fence.Frame.Active.Members.Contains(entry.Key, StringComparer.OrdinalIgnoreCase));
				if (expected.Any(entry => !fence.DisplayedPaths.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)))
				{
					throw new InvalidOperationException($"分区「{fence.Frame.Active.Title}」的桌面图标尚未完整加载。");
				}
			}
			if (_disposed || generation != _generation || !target.IsCurrent || target.HiddenBySystem)
			{
				return;
			}
			await _hooksReady;
			if (!IsActive)
			{
				var recovery = await IconRecovery.ArmAsync(target);
				if (_disposed || generation != _generation || !target.IsCurrent || target.HiddenBySystem)
				{
					recovery.Dispose();
					return;
				}
				_target = target;
				_recovery = recovery;
				_hooks.Desktop = target.Root;
				_hooks.Enabled = true;
				await _source.ClearSelectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
				if (_disposed || generation != _generation)
				{
					return;
				}
				foreach (var surface in _session.Surfaces)
				{
					surface.DesktopIcons.ShowIcons(true);
					surface.DesktopIcons.UpdateLayout();
				}
				await _session.Surfaces[0].Compositor.RequestCommitAsync();
				if (_disposed || generation != _generation || !target.IsCurrent || target.HiddenBySystem)
				{
					Release(null);
					return;
				}
				if (_recovery?.IsAlive != true || !target.Show(false))
				{
					throw new InvalidOperationException("桌面图标恢复保护失效或无法隐藏系统图标。");
				}
				for (int attempt = 0; attempt < 50 && target.Visible; attempt++)
				{
					await Task.Delay(20);
				}
				if (target.Visible || !target.IsCurrent)
				{
					throw new InvalidOperationException("系统桌面没有完成图标切换，已取消接管。");
				}
				if (_disposed || generation != _generation)
				{
					return;
				}
				IsActive = true;
				_firstActive.TrySetResult();
			}
			_signature = snapshot.Signature;
			_dirty = false;
			_selected.IntersectWith(placements.Select(item => item.Entry.Key));
			UpdateSelection();
			Notice(null);
		}
		catch (Exception ex)
		{
			Log.Error("WinUI 桌面接管失败", ex);
			Release($"桌面接管未完成，已恢复系统图标：{ex.Message}");
		}
		finally
		{
			_busy = false;
			if (_pending && !_disposed)
			{
				_pending = false;
				_ = ApplyAsync();
			}
		}
	}

	static nint MonitorOf(RECT area) => MonitorFromRect(ref area, MONITOR_DEFAULTTONEAREST);

	void CheckConnection()
	{
		if (_restoring)
		{
			if (_target is { IsCurrent: true, HiddenBySystem: false, Visible: false } target)
			{
				target.Show(true);
				return;
			}
			FinishRelease();
			_source.Refresh();
		}
		if (IsActive && (_target is not { IsCurrent: true, HiddenBySystem: false } || _recovery?.IsAlive != true
			|| DateTime.UtcNow - _observedAt > TimeSpan.FromSeconds(12)))
		{
			Release("桌面连接或恢复保护发生变化，已恢复系统图标。");
			_source.Refresh();
		}
		else if (IsActive && _target is { Visible: true } visible)
		{
			visible.Show(false);
		}
		if (_nativeEditing && !_restoring)
		{
			var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
			uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
			if (GetGUIThreadInfo(thread, ref info) && GetClassName(info.hwndFocus) != "Edit")
			{
				_nativeEditing = false;
				_source.Refresh();
			}
		}
	}

	internal void Release(string? message)
	{
		_generation++;
		IsActive = false;
		_signature = null;
		_dirty = true;
		_recovery?.Dispose();
		_recovery = null;
		Notice(message);
		if (_target is { IsCurrent: true, HiddenBySystem: false } target)
		{
			// 隐藏可能尚未执行，仍需排入显示请求覆盖它。
			target.Show(true);
			if (!target.Visible)
			{
				_restoring = true;
				return;
			}
		}
		FinishRelease();
	}

	void FinishRelease()
	{
		_restoring = false;
		_hooks.Enabled = false;
		_target = null;
		_selected.Clear();
		foreach (var surface in _session.Surfaces.Where(surface => !surface.IsClosed))
		{
			surface.DesktopIcons.ShowIcons(false);
		}
	}

	internal void Select(string? key, bool extend)
	{
		if (!extend)
		{
			_selected.Clear();
		}
		if (key != null && (!_selected.Contains(key) || !extend))
		{
			_selected.Add(key);
		}
		else if (key != null)
		{
			_selected.Remove(key);
		}
		UpdateSelection();
	}

	void UpdateSelection()
	{
		foreach (var surface in _session.Surfaces)
		{
			surface.DesktopIcons.Select(_selected);
		}
	}

	void Open(string key)
	{
		try
		{
			ExplorerShell.Open(key, Path.GetDirectoryName(key) ?? AppPaths.Desktop);
			UsageStats.Add(key);
		}
		catch (Exception ex)
		{
			Notice($"无法打开项目：{ex.Message}");
		}
	}

	internal void HandleKey(DesktopInputCommand command, uint key, bool extend)
	{
		var keys = _session.Surfaces.SelectMany(surface => surface.DesktopIcons.Keys).ToArray();
		if (!IsActive)
		{
			return;
		}
		switch (command)
		{
			case DesktopInputCommand.Open:
				foreach (string selected in _selected.ToArray())
				{
					Open(selected);
				}
				break;
			case DesktopInputCommand.Rename:
				if (_selected.Count == 1)
				{
					string selected = _selected.Single();
					try
					{
						_session.Surfaces.Single(surface => surface.DesktopIcons.Keys.Contains(selected, StringComparer.OrdinalIgnoreCase)).DesktopIcons.BeginRename(selected);
					}
					catch (Exception ex)
					{
						Notice(ex.Message);
					}
				}
				break;
			case DesktopInputCommand.SelectAll:
				_selected.UnionWith(keys);
				UpdateSelection();
				break;
			case DesktopInputCommand.Previous:
			case DesktopInputCommand.Next:
			case DesktopInputCommand.First:
			case DesktopInputCommand.Last:
				if (keys.Length > 0)
				{
					int index = Array.FindIndex(keys, _selected.Contains);
					int destination = command switch
					{
						DesktopInputCommand.Previous => Math.Max(0, index - 1),
						DesktopInputCommand.Next => Math.Min(keys.Length - 1, index + 1),
						DesktopInputCommand.First => 0,
						DesktopInputCommand.Last => keys.Length - 1,
						DesktopInputCommand.Open or DesktopInputCommand.Rename or DesktopInputCommand.SelectAll or DesktopInputCommand.TypeAhead or DesktopInputCommand.Unsupported
							=> throw new InvalidOperationException("此命令不属于位置导航。"),
					};
					Select(keys[destination], extend);
				}
				break;
			case DesktopInputCommand.TypeAhead:
				var match = _snapshot?.Items.FirstOrDefault(item => keys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)
					&& PinyinMatcher.Rank(item.DisplayName, char.ToLowerInvariant((char)key).ToString()) > 0);
				if (match != null)
				{
					Select(match.Key, false);
				}
				break;
			case DesktopInputCommand.Unsupported:
				Notice("当前版本暂不支持这项桌面操作。");
				break;
		}
	}

	void Notice(string? message)
	{
		foreach (var surface in _session.Surfaces.Where(surface => !surface.IsClosed))
		{
			surface.SetNotice(message);
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_watchdog.Stop();
		_source.Dispose();
		Release(null);
		_hooks.Dispose();
		foreach (var surface in _session.Surfaces)
		{
			surface.DesktopIcons.Invalidate();
		}
	}
}
