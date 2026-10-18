using Microsoft.UI.Xaml.Controls;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Verification;

namespace MyDesktop.WinUI.Services;

internal sealed class FileCommands
{
	readonly DesktopSession _session;
	readonly bool _probe;
	readonly IFileClipboard _clipboard;
	HashSet<string> _cut = new(StringComparer.OrdinalIgnoreCase);
	bool _busy;

	public FileCommands(DesktopSession session, bool probe)
	{
		_session = session;
		_probe = probe;
		if (probe && (DesktopInput.IsInputDesktop() || DesktopIconTarget.Find(true) == null))
		{
			throw new InvalidOperationException("测试文件命令只能在独立测试桌面运行。");
		}
		_clipboard = probe ? new MemoryFileClipboard() : new FileClipboard();
	}

	internal FileOperationResult? LastResult { get; private set; }
	internal IReadOnlySet<string> CutPaths => _cut;
	internal IFileClipboard Clipboard => _clipboard;
	internal bool IsBusy => _busy;

	public MenuFlyout CreateMenu()
	{
		var menu = new MenuFlyout();
		_session.ActiveFence?.HoldExpansion(menu);
		var selected = _session.SelectedFiles();
		bool files = selected.Count > 0 && selected.All(item => item.IsFileSystem);
		Add("复制", DesktopInputCommand.Copy, files);
		Add("剪切", DesktopInputCommand.Cut, files && selected.All(item => item.CanDelete));
		Add("粘贴", DesktopInputCommand.Paste, true);
		var rename = new MenuFlyoutItem { Text = "重命名", IsEnabled = _session.Selection.Keys.Count == 1 && !_session.IsPreview };
		rename.Click += (_, _) =>
		{
			try { _session.BeginSelectedRename(); }
			catch (Exception ex) { _session.Notice(ex.Message); }
		};
		menu.Items.Add(rename);
		Add("删除", DesktopInputCommand.Delete, files && selected.All(item => item.CanDelete));
		return menu;

		void Add(string text, DesktopInputCommand command, bool enabled)
		{
			var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled && !_session.IsPreview };
			item.Click += async (_, _) => await ExecuteAsync(command);
			menu.Items.Add(item);
		}
	}

	public async Task ExecuteAsync(DesktopInputCommand command)
	{
		if (_busy)
		{
			_session.Notice("正在处理上一项文件操作。");
			return;
		}
		_busy = true;
		LastResult = null;
		try
		{
			if (_session.IsPreview) { throw new InvalidOperationException("预览模式不能执行文件操作。"); }
			nint owner = _session.ActiveFence?.InputOwner ?? _session.ActiveSurface.Handle;
			if (command is DesktopInputCommand.Copy or DesktopInputCommand.Cut)
			{
				bool cut = command == DesktopInputCommand.Cut;
				var paths = FileOperationRules.ValidateSelection(_session.SelectedFiles(), cut);
				_clipboard.Write(owner, paths, cut);
				_session.Notice(null);
			}
			else if (command == DesktopInputCommand.Delete)
			{
				var paths = FileOperationRules.ValidateSelection(_session.SelectedFiles(), true);
				LastResult = await TransferAsync(owner, FileAction.Delete, paths, null);
				_session.FilesCompleted(false);
				Report(LastResult);
			}
			else if (command == DesktopInputCommand.Paste)
			{
				await PasteAsync(owner, _session.ActiveFence?.Frame.Active);
			}
			else { throw new ArgumentException("这不是文件操作命令。"); }
		}
		catch (Exception ex)
		{
			Log.Error("文件操作未完成", ex);
			_session.Notice(ex.Message);
		}
		finally
		{
			_busy = false;
			RefreshClipboard();
		}
	}

	async Task PasteAsync(nint owner, FenceSettings? target)
	{
		var data = _clipboard.Read(owner) ?? throw new InvalidOperationException("剪贴板中没有可粘贴的文件。");
		var result = await TransferFilesAsync(owner, target, data.Paths, data.Cut);
		if (data.Cut && !result.Aborted && result.Error == null && result.Completed.Count == data.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
		{
			_clipboard.Clear(owner, data.Sequence);
		}
		Report(result);
	}

	internal async Task<FileOperationResult?> DropAsync(nint owner, FenceSettings? target, IReadOnlyList<string> paths, bool move)
	{
		if (_busy) { _session.Notice("正在处理上一项文件操作。"); return null; }
		_busy = true;
		LastResult = null;
		try
		{
			if (_session.IsPreview) { throw new InvalidOperationException("预览模式不能执行文件操作。"); }
			var result = await TransferFilesAsync(owner, target, paths, move);
			Report(result);
			return result;
		}
		catch (Exception ex)
		{
			Log.Error("拖放文件未完成", ex);
			_session.Notice(ex.Message);
			return null;
		}
		finally { _busy = false; }
	}

	async Task<FileOperationResult> TransferFilesAsync(nint owner, FenceSettings? target, IReadOnlyList<string> source, bool move)
	{
		bool desktop = target?.IsPortal != true;
		string folder = desktop ? _session.DesktopFolders()[0] : target!.FolderPath;
		FileOperationRules.ValidatePaste(source, folder);
		if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException("粘贴目标目录不存在。"); }
		var paths = source.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		foreach (string path in paths) { _ = File.GetAttributes(path); }
		var logical = paths.Where(path => move && (desktop && FileOperationRules.IsDesktopFile(path, _session.DesktopFolders())
			|| PathUtil.AreEqual(Path.GetDirectoryName(path), folder))).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var physical = paths.Where(path => !logical.Contains(path)).ToArray();
		var result = physical.Length == 0 ? new FileOperationResult([], false, null)
			: await TransferAsync(owner, move ? FileAction.Move : FileAction.Copy, physical, folder);
		var logicalDone = result.Aborted || result.Error != null ? [] : logical.ToArray();
		var completed = result.Completed.Concat(logicalDone.Select(path => new CompletedFile(path, path, Directory.Exists(path)))).ToArray();
		bool save = false;
		foreach (var item in result.Completed.Where(item => move && item.Destination != null))
		{
			foreach (var fence in _session.Settings.Fences.Where(fence => !fence.IsPortal))
			{
				fence.Members.RemoveAll(path => PathUtil.AreEqual(path, item.Source) || item.Directory && PathUtil.IsUnder(path, item.Source));
				fence.CustomOrder.RemoveAll(path => PathUtil.AreEqual(path, item.Source) || item.Directory && PathUtil.IsUnder(path, item.Source));
			}
			ItemRename.UpdateReferences(_session.Settings, item.Source, item.Destination!, item.Directory);
			UsageStats.Rename(item.Source, item.Destination!, item.Directory);
			_session.Selection.Rename(item.Source, item.Destination!, item.Directory);
			save = true;
		}
		if (desktop && completed.Length > 0)
		{
			var assigned = completed.Select(item => item.Destination ?? throw new InvalidOperationException("已完成的粘贴项目缺少目标路径。")).ToArray();
			if (assigned.Any(path => !FileOperationRules.IsDesktopFile(path, _session.DesktopFolders())))
			{
				throw new InvalidOperationException("Shell 返回的粘贴结果不属于桌面目录，未修改分区归属。");
			}
			FileOperationRules.Assign(_session.Settings, assigned, target);
			save = true;
		}
		LastResult = result with { Completed = completed };
		_session.FilesCompleted(save);
		return LastResult;
	}

	Task<FileOperationResult> TransferAsync(nint owner, FileAction action, IReadOnlyList<string> paths, string? folder)
	{
		if (_probe && (paths.Any(path => !PathUtil.IsUnder(path, AppPaths.DataDir)) || folder != null && !PathUtil.IsUnder(folder, AppPaths.DataDir)))
		{
			throw new InvalidOperationException("隔离验证拒绝操作测试数据目录之外的文件。");
		}
		// 验证只删除本次创建的测试文件，不写用户回收站或撤销历史，也不弹系统对话框。
		return ShellTransfer.RunAsync(owner, action, paths, folder, quiet: _probe, recycle: !_probe);
	}

	void Report(FileOperationResult result) => _session.Notice(result.Error != null ? $"已完成 {result.Completed.Count} 项，其余未完成：{result.Error}"
		: result.Aborted ? $"操作已停止，已完成 {result.Completed.Count} 项。未完成的项目保持原状，请重新选择后重试。" : null);

	internal void RefreshClipboard()
	{
		try
		{
			var data = _clipboard.Read(_session.ActiveSurface.Handle);
			_cut = data?.Cut == true ? data.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			ApplyCutState();
		}
		catch (Exception ex)
		{
			Log.Warn("读取文件剪贴板失败", ex);
			_session.Notice($"无法读取剪贴板：{ex.Message}");
		}
	}

	internal void ApplyCutState()
	{
		foreach (var surface in _session.Surfaces.Where(surface => !surface.IsClosed))
		{
			surface.DesktopIcons.ApplyCutState(_cut);
			foreach (var fence in surface.Fences) { fence.ApplyCutState(_cut); }
		}
	}
}
