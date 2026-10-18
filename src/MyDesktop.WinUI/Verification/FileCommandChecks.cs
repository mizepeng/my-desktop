using Microsoft.UI.Xaml.Controls;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class FileCommandChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "文件命令只在独立桌面验证");
		var surface = session.Surfaces.First(item => item.Fences.Any(fence => fence.Frame.Active.IsPortal));
		var fence = surface.Fences.First(item => item.Frame.Active.IsPortal);
		var portal = fence.Frame.Active;
		var desktop = fence.Frame.Members.First(item => !item.IsPortal);
		var commands = session.Commands;
		var foreground = GetForegroundWindow();
		string loose = session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).First();
		string selected = fence.DisplayedPaths[0];
		session.SelectFence(fence, [selected], false);
		session.SelectLoose(loose, true, surface);
		Require(session.Selection.Keys.Count == 2 && fence.ItemList.SelectedItems.Count == 1, "追加散放选择保留分区选择并同步视图");
		session.SelectLoose(loose, true, surface);
		session.BeginSelectedRename();
		Require(fence.IsEditing, "取消追加选择后 F2 仍按剩余项目所属区域改名");
		fence.CancelEdit();
		session.SelectLoose(loose, true, surface);
		fence.ActivateTab(desktop.Id);
		await fence.ContentReady;
		Require(session.Selection.Keys.SequenceEqual(new[] { loose }), "切换标签清理隐藏项目选择并保留其他区域");
		fence.ActivateTab(portal.Id);
		await fence.ContentReady;
		session.SelectFence(fence, [selected], false);
		Require(session.Selection.Keys.SequenceEqual(new[] { selected }), "分区单选清除其他区域的选择");
		await commands.ExecuteAsync(DesktopInputCommand.Cut);
		fence.UpdateLayout();
		Require(commands.Clipboard.Read(surface.Handle) is { Cut: true } cut && cut.Paths.SequenceEqual(new[] { selected }),
			"剪切写入所选文件列表：" + surface.Notice);
		var container = fence.ItemList.ContainerFromIndex(0) as GridViewItem;
		Require(container != null && Math.Abs(container.Opacity - 0.4) < 0.001, "剪切淡化实际图标容器");
		fence.RefreshContent();
		await fence.ContentReady;
		Require(session.Selection.Keys.Contains(selected) && commands.CutPaths.Contains(selected), "内容刷新保留共享选择和剪切状态");
		await commands.ExecuteAsync(DesktopInputCommand.Copy);
		Require(commands.Clipboard.Read(surface.Handle) is { Cut: false } && commands.CutPaths.Count == 0, "重新复制清理剪切状态");

		string folder = Path.Combine(AppPaths.DataDir, "文件命令来源");
		Directory.CreateDirectory(folder);
		string source = Path.Combine(folder, "重名.txt");
		string existing = Path.Combine(portal.FolderPath, "重名.txt");
		File.WriteAllText(source, "复制来源");
		File.WriteAllText(existing, "目标原文件");
		commands.Clipboard.Write(surface.Handle, [source], false);
		session.SetContext(surface, fence);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		var copied = Completed(commands, 1).Single();
		Require(copied.Destination != existing && File.ReadAllText(copied.Destination!) == "复制来源"
			&& File.ReadAllText(existing) == "目标原文件" && File.Exists(source), "Shell 重名复制使用真实返回路径且不覆盖原文件");
		await fence.ContentReady;
		Require(fence.DisplayedPaths.Contains(copied.Destination!), "复制结果立即刷新映射内容");

		string moving = Path.Combine(folder, "移动.txt");
		File.WriteAllText(moving, "移动来源");
		commands.Clipboard.Write(surface.Handle, [moving], true);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		var moved = Completed(commands, 1).Single();
		Require(!File.Exists(moving) && File.ReadAllText(moved.Destination!) == "移动来源" && commands.Clipboard.Read(surface.Handle) == null,
			"Shell 移动成功后才清空对应剪切列表");

		string tree = Path.Combine(folder, "目录");
		Directory.CreateDirectory(tree);
		File.WriteAllText(Path.Combine(tree, "子项.txt"), "后代内容");
		commands.Clipboard.Write(surface.Handle, [tree], false);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		var directory = Completed(commands, 1).Single();
		Require(directory.Directory && File.ReadAllText(Path.Combine(directory.Destination!, "子项.txt")) == "后代内容", "目录复制仅记录所选根项并保留后代内容");

		string locked = Path.Combine(folder, "占用.txt");
		File.WriteAllText(locked, "不能误报成功");
		using (var file = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			commands.Clipboard.Write(surface.Handle, [locked], true);
			await commands.ExecuteAsync(DesktopInputCommand.Paste);
			Require(commands.LastResult is { Completed.Count: 0 } failed && (failed.Aborted || failed.Error != null)
				&& commands.Clipboard.Read(surface.Handle)?.Cut == true && !File.Exists(Path.Combine(portal.FolderPath, "占用.txt")),
				"占用导致失败时不补成功路径、不清剪切列表");
		}

		fence.ActivateTab(desktop.Id);
		await fence.ContentReady;
		session.SetContext(surface, fence);
		string desktopExisting = Path.Combine(session.DesktopFolders()[0], "重名.txt");
		File.WriteAllText(desktopExisting, "桌面原文件");
		commands.Clipboard.Write(surface.Handle, [source], false);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		var assigned = Completed(commands, 1).Single();
		Require(assigned.Destination != desktopExisting && desktop.Members.Contains(assigned.Destination!)
			&& !desktop.Members.Contains(desktopExisting) && File.ReadAllText(desktopExisting) == "桌面原文件", "桌面分区按 Shell 重名结果归入新文件，保留原文件归属");
		await fence.ContentReady;
		Require(fence.DisplayedPaths.Contains(assigned.Destination!), "实际复制结果已显示在目标桌面分区");
		string first = Path.Combine(folder, "批次一.txt");
		File.WriteAllText(first, "批次中可移动的文件");
		using (var file = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			commands.Clipboard.Write(surface.Handle, [first, locked], true);
			await commands.ExecuteAsync(DesktopInputCommand.Paste);
			var partial = commands.LastResult ?? throw new InvalidOperationException("批次没有返回操作结果。");
			Require(partial.Aborted && partial.Completed.Count == 1 && partial.Completed[0].Source == first
				&& desktop.Members.Contains(partial.Completed[0].Destination!) && File.Exists(locked)
				&& !desktop.Members.Contains(Path.Combine(session.DesktopFolders()[0], "占用.txt"))
				&& commands.Clipboard.Read(surface.Handle)?.Cut == true && surface.Notice.Length > 0,
				"批次中途失败只同步实际完成项，不归入失败文件或清空剪切列表");
			await fence.ContentReady;
			await Task.Delay(100);
			Require(surface.Notice.Length > 0, "内容刷新不会抹掉文件命令的部分失败提示");
		}

		// 桌面内剪切只变更成员；还原归属后继续复用原有监视与崩溃恢复检查。
		commands.Clipboard.Write(surface.Handle, [loose], true);
		session.SetContext(surface, fence);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		Completed(commands, 1);
		Require(desktop.Members.Contains(loose) && File.Exists(loose), "桌面剪切到分区只转移归属，保留源文件");
		await WaitAsync(() => !session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Contains(loose), "归入分区后散放视图不重复绘制");
		commands.Clipboard.Write(surface.Handle, [loose], true);
		session.SetContext(surface, null);
		await commands.ExecuteAsync(DesktopInputCommand.Paste);
		Completed(commands, 1);
		Require(!desktop.Members.Contains(loose) && File.Exists(loose), "剪切回桌面空白只移除分区归属");
		await WaitAsync(() => session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Contains(loose), "移出分区后恢复散放视图");
		fence.ActivateTab(portal.Id);
		await fence.ContentReady;
		session.SelectFence(fence, [moved.Destination!], false);
		await commands.ExecuteAsync(DesktopInputCommand.Delete);
		Completed(commands, 1);
		Require(!File.Exists(moved.Destination!), "删除命令只删除所选测试文件，隔离验证不写回收站或撤销记录");
		await fence.ContentReady;
		Require(!fence.DisplayedPaths.Contains(moved.Destination!) && session.Selection.Keys.Count == 0, "删除后同步清理视图和选择");
		Require(GetForegroundWindow() == foreground && session.Takeover!.IsActive, "文件操作没有抢占用户前台，测试接管保持有效");
	}

	static IReadOnlyList<CompletedFile> Completed(FileCommands commands, int expected)
	{
		var result = commands.LastResult ?? throw new InvalidOperationException("文件命令没有返回 Shell 结果。");
		Require(!result.Aborted && result.Error == null && result.Completed.Count == expected,
			$"Shell 明确完成 {expected} 项：实际 {result.Completed.Count} 项，取消 {result.Aborted}，错误 {result.Error}");
		return result.Completed;
	}

	static async Task WaitAsync(Func<bool> condition, string message)
	{
		for (int attempt = 0; !condition(); attempt++)
		{
			if (attempt >= 150) { throw new TimeoutException(message); }
			await Task.Delay(20);
		}
		UiChecks.Write("通过：" + message);
	}

	static void Require(bool condition, string message)
	{
		if (!condition) { throw new InvalidOperationException(message); }
		UiChecks.Write("通过：" + message);
	}
}
