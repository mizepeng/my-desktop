using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Services;

internal sealed class DesktopSession : IDisposable
{
	readonly AppSettings _settings;
	readonly StartupOptions _options;
	readonly Action _exit;
	readonly ShellItemLoader _images = new();
	readonly FileRename _rename;
	readonly List<DesktopSurface> _surfaces = [];
	readonly DispatcherQueueTimer _saveTimer;
	Window? _welcome;
	DesktopTakeover? _takeover;
	FileChangeMonitor? _files;
	bool _dirty, _disposed;

	public DesktopSession(AppSettings settings, StartupOptions options, Action exit)
	{
		_settings = settings;
		_options = options;
		_exit = exit;
		_rename = new FileRename(settings, SaveSoon, DesktopFolders);
		_rename.Changed += (oldPath, newPath, directory) =>
		{
			_takeover?.ItemRenamed(oldPath, newPath, directory);
			_files?.CheckFolders();
			RefreshFiles();
		};
		_saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_saveTimer.Interval = TimeSpan.FromMilliseconds(300);
		_saveTimer.IsRepeating = false;
		_saveTimer.Tick += (_, _) => Save();
	}

	internal IReadOnlyList<DesktopSurface> Surfaces => _surfaces;
	internal AppSettings Settings => _settings;
	internal DesktopTakeover? Takeover => _takeover;
	internal FileChangeMonitor? Files => _files;
	internal string? FileWatchError { get; private set; }
	internal bool DesktopWatchReady => DesktopFolders().All(folder => _files?.IsWatching(folder) == true);

	IReadOnlyList<string> DesktopFolders() => _options.IsProbe ? [Path.Combine(AppPaths.DataDir, "桌面")] : [AppPaths.Desktop, AppPaths.CommonDesktop];

	public void Start()
	{
		var workspace = new FenceWorkspace(_settings);
		if (workspace.Frames.Count == 0)
		{
			ShowWelcome();
			return;
		}
		var monitors = new List<nint>();
		if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref RECT bounds, nint data) => { monitors.Add(monitor); return true; }, 0))
		{
			throw new System.ComponentModel.Win32Exception();
		}
		foreach (var monitor in monitors)
		{
			var frames = workspace.Frames.Where(frame =>
			{
				var model = frame.Active;
				var rectangle = new RECT(model.X, model.Y, model.X + model.Width, model.Y + model.Height);
				return MonitorFromRect(ref rectangle, MONITOR_DEFAULTTONEAREST) == monitor;
			}).ToArray();
			var (workArea, scale) = GetMonitorWorkArea(monitor);
			var surface = new DesktopSurface(workArea, scale, frames, _settings, _images, _rename, _options.PreviewFile != null);
			surface.SaveRequested += SaveSoon;
			surface.ExitRequested += _exit;
			surface.Closed += (_, _) => { if (!_disposed) { _exit(); } };
			_surfaces.Add(surface);
		}
		if (_options.PreviewFile == null)
		{
			_takeover = new DesktopTakeover(this, _options.IsProbe);
			_files = new FileChangeMonitor(DispatcherQueue.GetForCurrentThread(), WatchFolders);
			_files.Relevant += IsRelevant;
			_files.Renamed += (oldPath, newPath) =>
			{
				if (ItemRename.ShouldFollowExternalRename(oldPath, newPath))
				{
					// 改名通知可能到达时新目录已再次改名，不依赖此刻的文件属性判断是否要同步后代引用。
					_rename.Observe(oldPath, newPath, true);
				}
			};
			_files.Settled += RefreshFiles;
			_files.Failed += message =>
			{
				FileWatchError = message;
				Log.Error(message);
				_takeover.Release(message + "，已恢复系统图标，请重启后重试。");
			};
			_files.Start();
		}
		if (_options.IsProbe)
		{
			_surfaces.First(surface => surface.Fences.Count > 0).Ready += () => DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
			{
				if (_options.IsCrashProbe)
				{
					try
					{
						await _takeover!.FirstActive.WaitAsync(TimeSpan.FromSeconds(10));
						var state = new MyDesktop.Verification.RecoveryProbeState(Environment.ProcessId, _takeover.GuardProcessId!.Value, _takeover.Target!.List.ToInt64());
						File.WriteAllText(_options.ReportFile! + ".tmp", System.Text.Json.JsonSerializer.Serialize(state));
						File.Move(_options.ReportFile! + ".tmp", _options.ReportFile!, true);
					}
					catch (Exception ex)
					{
						File.AppendAllText(_options.ReportFile!, ex + Environment.NewLine);
						Environment.ExitCode = 1;
						_exit();
					}
					return;
				}
				try
				{
					await Verification.UiChecks.RunAsync(this, _options.ReportFile!);
				}
				catch (Exception ex)
				{
					File.AppendAllText(_options.ReportFile!, ex + Environment.NewLine);
					Environment.ExitCode = 1;
				}
				finally
				{
					_exit();
				}
			});
		}
		foreach (var surface in _surfaces)
		{
			surface.ShowOnDesktop();
		}
		_takeover?.Start();
		Log.Info($"WinUI 已创建 {_surfaces.Count} 个桌面宿主、{workspace.Frames.Count} 个分区视图。");
	}

	void ShowWelcome()
	{
		var create = new Button { Content = "创建桌面分区", IsEnabled = _options.PreviewFile == null };
		var content = new StackPanel { Spacing = 20, Padding = new Thickness(32) };
		content.Children.Add(new TextBlock { Text = "MyDesktop", FontSize = 28 });
		content.Children.Add(new TextBlock { Text = "桌面上还没有分区。创建一个分区开始整理。", TextWrapping = TextWrapping.Wrap });
		content.Children.Add(create);
		_welcome = new Window { Title = "MyDesktop", Content = content };
		_welcome.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 280));
		create.Click += (_, _) =>
		{
			var (work, scale) = GetMonitorWorkArea(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY));
			_settings.Fences.Add(new FenceSettings
			{
				X = work.Left + (int)(80 * scale), Y = work.Top + (int)(80 * scale), Width = (int)(400 * scale), Height = (int)(300 * scale),
				LayoutDpi = (int)(96 * scale),
			});
			SaveSoon();
			Start();
			_welcome.Close();
			_welcome = null;
		};
		_welcome.Closed += (_, _) => { if (_surfaces.Count == 0 && !_disposed) { _exit(); } };
		_welcome.Activate();
	}

	IReadOnlyList<FolderWatch> WatchFolders() => FolderWatch.Build(DesktopFolders(), _settings.Fences.Where(fence => fence.IsPortal).Select(fence => fence.FolderPath));

	bool IsRelevant(string path) => DesktopFolders().Any(folder => PathUtil.AreEqual(Path.GetDirectoryName(path), folder))
		|| _settings.Fences.Any(fence => fence.IsPortal && (PathUtil.AreEqual(fence.FolderPath, path) || PathUtil.AreEqual(Path.GetDirectoryName(path), fence.FolderPath)));

	void RefreshFiles()
	{
		foreach (var fence in _surfaces.SelectMany(surface => surface.Fences))
		{
			fence.RefreshContent(true);
		}
		_takeover?.Refresh();
	}

	void SaveSoon()
	{
		if (_options.PreviewFile != null)
		{
			return;
		}
		_dirty = true;
		_takeover?.Refresh();
		_saveTimer.Stop();
		_saveTimer.Start();
	}

	void Save()
	{
		if (_dirty)
		{
			SettingsStore.Save(_settings);
			_dirty = false;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_saveTimer.Stop();
		_files?.Dispose();
		_takeover?.Dispose();
		Save();
		foreach (var surface in _surfaces.Where(surface => !surface.IsClosed))
		{
			surface.Close();
		}
		_welcome?.Close();
		_images.Dispose();
	}
}
