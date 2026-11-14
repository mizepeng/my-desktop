using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// Win10 黑屏测试版：通过 Shell 的 FWF_NOICONS 控制图标显示，不再主动隐藏整个 SysListView32。
/// 所有 COM 操作都在独立 STA 线程上；先持久化原始状态，正常退出、守护进程和下次启动都能恢复。
/// </summary>
internal static class ShellDesktopIcons
{
	const uint NoIcons = 0x1000;
	static readonly Lazy<Dispatcher> Worker = new(CreateWorker);
	static int _refreshQueued;
	static volatile bool _desiredVisible = true;
	static volatile bool _visible = true;
	static volatile bool _ownsSetting;
	static volatile bool _initiallyHidden;
	static volatile bool _failed;

	public static event Action<Exception>? Failed;

	public static bool Visible => _visible;
	public static bool OwnsSetting => _ownsSetting;
	public static bool InitiallyHidden => _initiallyHidden;
	static string RestoreFile => Path.Combine(AppPaths.DataDir, "desktop-icons-restore.json");

	/// <summary>
	/// HideIcons 为 null 表示接管前注册表值不存在，恢复时删除本程序可能产生的值，而不是填一个默认值。
	/// </summary>
	sealed record RestoreState([property: JsonRequired] bool NoIcons, [property: JsonRequired] int? HideIcons);

	public static void SetVisible(bool visible)
	{
		// 已经报告失败并准备退出时，不再接受看门狗重复发来的隐藏请求。
		_desiredVisible = visible || _failed;
		Refresh();
	}

	/// <summary>
	/// 看门狗请求重新核对 Shell 标志；Explorer 重启或宿主切换后也走同一条路径，不使用旧的隐藏方式兜底。
	/// </summary>
	public static void Refresh()
	{
		if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
		{
			return;
		}
		Worker.Value.BeginInvoke(() =>
		{
			bool visible = _desiredVisible;
			try
			{
				Apply(visible);
			}
			catch (Exception ex)
			{
				Log.Error("Win10 测试版应用 Shell 图标显示状态失败，未退回隐藏图标控件", ex);
				if (!visible)
				{
					_failed = true;
					_desiredVisible = true;
					Failed?.Invoke(ex);
				}
			}
			finally
			{
				Interlocked.Exchange(ref _refreshQueued, 0);
				if (visible != _desiredVisible)
				{
					Refresh();
				}
			}
		});
	}

	/// <summary>
	/// 退出时最多等两秒，Explorer 无响应时保留恢复文件和守护进程，由主进程退出后继续恢复。
	/// </summary>
	public static bool Restore(TimeSpan timeout)
	{
		_desiredVisible = true;
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Worker.Value.BeginInvoke(() =>
		{
			try
			{
				Apply(true);
				completion.SetResult(true);
			}
			catch (Exception ex)
			{
				Log.Error("恢复 Shell 桌面图标失败，保留原始状态供守护进程或下次启动恢复", ex);
				completion.SetResult(false);
			}
		});
		if (!completion.Task.Wait(timeout))
		{
			Log.Warn("等待 Shell 恢复桌面图标超时，保留守护进程继续恢复");
			return false;
		}
		return completion.Task.Result;
	}

	static Dispatcher CreateWorker()
	{
		Dispatcher? dispatcher = null;
		using var ready = new ManualResetEventSlim();
		var thread = new Thread(() =>
		{
			dispatcher = Dispatcher.CurrentDispatcher;
			ready.Set();
			Dispatcher.Run();
		})
		{
			IsBackground = true,
			Name = "ShellDesktopIcons",
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		ready.Wait();
		return dispatcher!;
	}

	static void Apply(bool visible)
	{
		var view = DesktopFolderView.Open() ?? throw new InvalidOperationException("Explorer 桌面视图尚未就绪。");
		try
		{
			Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out uint flags));
			var saved = File.Exists(RestoreFile)
					? JsonSerializer.Deserialize<RestoreState>(File.ReadAllText(RestoreFile))
							?? throw new InvalidDataException("桌面图标恢复文件为空。")
					: null;
			if (!visible && saved == null)
			{
				saved = new RestoreState((flags & NoIcons) != 0, DesktopHost.ReadSystemHideIcons());
				Directory.CreateDirectory(AppPaths.DataDir);
				var temp = RestoreFile + ".tmp";
				using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					JsonSerializer.Serialize(stream, saved);
					stream.Flush(true);
				}
				File.Move(temp, RestoreFile, true);
			}
			if (saved != null)
			{
				_initiallyHidden = saved.NoIcons || saved.HideIcons is int value && value != 0;
				_ownsSetting = true;
			}
			bool hidden = visible ? saved?.NoIcons ?? (flags & NoIcons) != 0 : true;
			if (((flags & NoIcons) != 0) != hidden)
			{
				Marshal.ThrowExceptionForHR(view.SetCurrentFolderFlags(NoIcons, hidden ? NoIcons : 0));
				Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out uint actual));
				if (((actual & NoIcons) != 0) != hidden)
				{
					throw new InvalidOperationException($"Shell 没有应用图标显示标志：请求隐藏={hidden}，实际 flags=0x{actual:X}。");
				}
				Log.Info($"Win10 测试版：Shell 图标显示标志已改为 FWF_NOICONS={hidden}，flags=0x{actual:X}；未调用 SW_HIDE");
			}
			if (visible && saved != null)
			{
				// 部分 Shell 实现会同步系统选项；退出时精确恢复接管前的值，不能把自己的隐藏误当成用户的设置。
				if (DesktopHost.ReadSystemHideIcons() != saved.HideIcons)
				{
					DesktopHost.RestoreSystemHideIcons(saved.HideIcons);
					if (DesktopHost.ReadSystemHideIcons() != saved.HideIcons)
					{
						throw new InvalidOperationException("HideIcons 未恢复为接管前的值。");
					}
				}
				File.Delete(RestoreFile);
				_ownsSetting = false;
				_initiallyHidden = false;
				Log.Info($"Win10 测试版：已恢复接管前的 Shell 图标状态，FWF_NOICONS={hidden}");
			}
			if (visible && !hidden)
			{
				// 从旧版升级时只恢复可能遗留的控件隐藏，不再主动隐藏控件。
				var list = DesktopHost.FindFolderView();
				if (list != IntPtr.Zero && (GetWindowLongPtr(list, GWL_STYLE).ToInt64() & 0x10000000) == 0)
				{
					if (!ShowWindowAsync(list, SW_SHOWNA))
					{
						throw new InvalidOperationException("恢复旧版遗留的图标控件隐藏失败。");
					}
				}
			}
			_visible = !hidden;
		}
		finally
		{
			DesktopFolderView.Release(view);
		}
	}
}
