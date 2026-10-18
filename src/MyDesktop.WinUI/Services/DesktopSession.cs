using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;
using FenceView = MyDesktop.WinUI.Views.FenceView;
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
	FileCommands? _commands;
	FenceView? _activeFence;
	DesktopSurface? _activeSurface;
	bool _dirty, _disposed;

	public DesktopSession(AppSettings settings, StartupOptions options, Action exit)
	{
		_settings = settings;
		_options = options;
		_exit = exit;
		Selection.Changed += ProjectSelection;
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
	internal ItemSelection Selection { get; } = new();
	internal FileCommands Commands => _commands ?? throw new InvalidOperationException("文件命令尚未连接到桌面宿主。");
	internal FenceView? ActiveFence => _activeFence;
	internal DesktopSurface ActiveSurface => _activeSurface ?? throw new InvalidOperationException("桌面输入宿主尚未就绪。");
	internal bool IsPreview => _options.PreviewFile != null;

	internal IReadOnlyList<string> DesktopFolders() => _options.IsProbe ? [Path.Combine(AppPaths.DataDir, "桌面")] : [AppPaths.Desktop, AppPaths.CommonDesktop];

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
		_activeSurface = _surfaces[0];
		_commands = new FileCommands(this, _options.IsProbe);
		foreach (var surface in _surfaces)
		{
			surface.DesktopIcons.SelectionRequested += (key, extend) => SelectLoose(key, extend, surface);
			surface.DesktopIcons.ItemContextRequested += (_, anchor, point) =>
			{
				SetContext(surface, null);
				Commands.CreateMenu().ShowAt(anchor, point);
			};
			surface.ClipboardChanged += () => Commands.RefreshClipboard();
			foreach (var fence in surface.Fences)
			{
				fence.TargetRequested += () => { SetContext(surface, fence); surface.ActivateForSelection(); };
				fence.SelectionRequested += (keys, extend) => SelectFence(fence, keys, extend);
				fence.ScopeHidden += scope => Selection.Remove(scope);
				fence.ContentChanged += () =>
				{
					Selection.Retain(fence.Frame.Active.Id, fence.DisplayedPaths);
					ProjectSelection();
				};
				fence.ItemContextRequested += (anchor, point) =>
				{
					SetContext(surface, fence);
					Commands.CreateMenu().ShowAt(anchor, point);
				};
			}
		}
		if (!_options.IsProbe && !IsPreview)
		{
			_surfaces[0].WatchClipboard();
			Commands.RefreshClipboard();
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

	internal void SetContext(DesktopSurface surface, FenceView? fence)
	{
		_activeSurface = surface;
		_activeFence = fence;
	}

	internal void SelectLoose(string? key, bool extend, DesktopSurface? surface = null)
	{
		SetContext(surface ?? ActiveSurface, null);
		Selection.Click(Guid.Empty, key, extend);
	}

	internal void SelectFence(FenceView fence, IReadOnlyList<string> keys, bool extend)
	{
		var surface = _surfaces.Single(surface => surface.Fences.Contains(fence));
		SetContext(surface, fence);
		surface.ActivateForSelection();
		Selection.Set(fence.Frame.Active.Id, keys, extend);
	}

	internal void ProjectSelection()
	{
		foreach (var surface in _surfaces.Where(surface => !surface.IsClosed))
		{
			surface.DesktopIcons.Select(Selection.InScope(Guid.Empty));
			foreach (var fence in surface.Fences)
			{
				fence.SelectPaths(Selection.InScope(fence.Frame.Active.Id));
			}
		}
		_commands?.ApplyCutState();
	}

	internal IReadOnlyList<SelectedFile> SelectedFiles()
	{
		var result = new List<SelectedFile>();
		void Desktop(string path)
		{
			var entry = _takeover?.Snapshot?.ByKey.GetValueOrDefault(path);
			bool file = entry?.IsFileSystem == true && FileOperationRules.IsDesktopFile(path, DesktopFolders());
			result.Add(new SelectedFile(path, file, file && entry!.CanDelete));
		}
		foreach (string path in Selection.InScope(Guid.Empty)) { Desktop(path); }
		foreach (var fence in _surfaces.SelectMany(surface => surface.Fences))
		{
			foreach (string path in Selection.InScope(fence.Frame.Active.Id))
			{
				if (fence.Frame.Active.IsPortal)
				{
					bool file = Path.IsPathFullyQualified(path) && PathUtil.AreEqual(Path.GetDirectoryName(path), fence.Frame.Active.FolderPath);
					result.Add(new SelectedFile(path, file, file));
				}
				else { Desktop(path); }
			}
		}
		return result;
	}

	internal void BeginSelectedRename()
	{
		string key = Selection.Keys.Count == 1 ? Selection.Keys[0] : throw new InvalidOperationException("请选择一个项目进行改名。");
		var fence = _surfaces.SelectMany(surface => surface.Fences)
			.FirstOrDefault(item => Selection.InScope(item.Frame.Active.Id).Contains(key, StringComparer.OrdinalIgnoreCase));
		if (fence != null)
		{
			fence.BeginItemRename(key);
		}
		else
		{
			_surfaces.Single(surface => surface.DesktopIcons.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)).DesktopIcons.BeginRename(key);
		}
	}

	internal void HandleKey(DesktopInputCommand command, uint key, bool extend)
	{
		if (command is DesktopInputCommand.Copy or DesktopInputCommand.Cut or DesktopInputCommand.Paste or DesktopInputCommand.Delete)
		{
			_ = Commands.ExecuteAsync(command);
			return;
		}
		try
		{
			var keys = _activeFence?.DisplayedPaths.ToArray() ?? _surfaces.SelectMany(surface => surface.DesktopIcons.Keys).ToArray();
			Guid scope = _activeFence?.Frame.Active.Id ?? Guid.Empty;
			switch (command)
			{
				case DesktopInputCommand.Open:
					foreach (string selected in Selection.Keys)
					{
						ExplorerShell.Open(selected, Path.GetDirectoryName(selected) ?? AppPaths.Desktop);
						UsageStats.Add(selected);
					}
					break;
				case DesktopInputCommand.Rename: BeginSelectedRename(); break;
				case DesktopInputCommand.SelectAll: Selection.Set(scope, keys, false); break;
				case DesktopInputCommand.Previous:
				case DesktopInputCommand.Next:
				case DesktopInputCommand.First:
				case DesktopInputCommand.Last:
					if (keys.Length > 0)
					{
						int index = Array.FindIndex(keys, candidate => Selection.InScope(scope).Contains(candidate, StringComparer.OrdinalIgnoreCase));
						int next = command == DesktopInputCommand.First ? 0 : command == DesktopInputCommand.Last ? keys.Length - 1
							: command == DesktopInputCommand.Previous ? Math.Max(0, index - 1) : Math.Min(keys.Length - 1, index + 1);
						var selected = extend ? Selection.InScope(scope).Append(keys[next]) : new[] { keys[next] };
						Selection.Set(scope, selected, extend);
					}
					break;
				case DesktopInputCommand.TypeAhead:
					var match = keys.FirstOrDefault(path => PinyinMatcher.Rank(_takeover?.Snapshot?.ByKey.GetValueOrDefault(path)?.DisplayName
						?? Path.GetFileName(path), char.ToLowerInvariant((char)key).ToString()) > 0);
					if (match != null) { Selection.Set(scope, [match], false); }
					break;
				case DesktopInputCommand.Unsupported:
					Notice(key == 0x2E && extend ? "当前版本暂不支持永久删除。" : "当前版本暂不支持这项桌面操作。");
					break;
			}
		}
		catch (Exception ex) { Notice(ex.Message); }
	}

	internal void Notice(string? message)
	{
		foreach (var surface in _surfaces.Where(surface => !surface.IsClosed)) { surface.SetNotice(message); }
	}

	internal void FilesCompleted(bool save)
	{
		if (save) { SaveSoon(); }
		_files?.CheckFolders();
		RefreshFiles();
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
