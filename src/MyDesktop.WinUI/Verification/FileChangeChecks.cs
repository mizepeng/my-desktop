using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml.Input;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using MyDesktop.WinUI.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class FileChangeChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "文件监视与散放改名验证只能在独立桌面运行");
		var takeover = session.Takeover!;
		var monitor = session.Files!;
		var surface = session.Surfaces.First(item => item.DesktopIcons.Keys.Count > 0);
		var icons = surface.DesktopIcons;
		var editor = icons.EditorControl;
		var fence = session.Surfaces.SelectMany(item => item.Fences).First(item => item.Frame.Active.IsPortal);
		string source = icons.Keys[0];
		string other = icons.Keys[1];
		string original = File.ReadAllText(source);
		var foreground = GetForegroundWindow();

		icons.BeginRename(source);
		editor.Cancel();
		await Task.Delay(50);
		Require(!surface.IsEditing && Passive(surface.Handle), "快速取消散放改名不会被排队的焦点请求重新激活");
		takeover.Select(source, false);
		takeover.HandleKey(DesktopInputCommand.Rename, 0x71, false);
		await Task.Delay(50);
		Require(surface.IsEditing && !Passive(surface.Handle) && ReferenceEquals(FocusManager.GetFocusedElement(icons.XamlRoot), editor.NameInput),
			"F2 命令连接到散放改名输入框并获得 XAML 焦点");
		editor.NameInput.Text = "不应写入";
		editor.Cancel();
		Require(File.ReadAllText(source) == original && Passive(surface.Handle), "取消散放改名不修改文件并恢复非激活状态");

		fence.BeginTitleEdit();
		await Task.Delay(30);
		icons.BeginRename(source);
		await Task.Delay(30);
		Require(!fence.IsEditing && editor.IsEditing, "散放改名与分区改名共用同一宿主输入控制");
		fence.BeginTitleEdit();
		await Task.Delay(30);
		Require(!editor.IsEditing && fence.IsEditing, "切换回分区编辑时取消散放草稿");
		fence.CancelEdit();

		icons.BeginRename(source);
		await Task.Delay(30);
		editor.NameInput.Text = "NUL";
		Require(!editor.Commit() && editor.IsEditing && editor.ErrorText.Length > 0 && File.Exists(source), "散放改名拒绝保留名称并保留编辑框");
		editor.NameInput.Text = Path.GetFileNameWithoutExtension(other);
		Require(!editor.Commit() && editor.ErrorText.Contains("同名") && File.ReadAllText(source) == original, "散放改名拒绝名称冲突且不覆盖文件");
		UsageStats.Add(source);
		editor.NameInput.Text = "散放新名称";
		Require(editor.Commit(), "散放图标通过共用 Shell 操作完成改名");
		string renamed = Path.Combine(Path.GetDirectoryName(source)!, "散放新名称.txt");
		RewriteSnapshot(source, renamed);
		takeover.Refresh();
		await WaitAsync(() => icons.Keys.Contains(renamed), "Explorer 快照更新后散放图标使用新路径");
		Require(!File.Exists(source) && File.ReadAllText(renamed) == original && takeover.SelectedKeys.Contains(renamed), "散放改名保留文件内容、扩展名与选择");
		Require(UsageStats.Score(source) == 0 && UsageStats.Score(renamed) > 0 && !surface.IsEditing && Passive(surface.Handle), "散放改名同步使用记录并恢复宿主状态");

		var desktop = session.Settings.Fences[0];
		string member = desktop.Members.Single(path => Path.GetFileName(path) == "工作.txt");
		desktop.CustomOrder.Add(member);
		UsageStats.Add(member);
		string external = Path.Combine(Path.GetDirectoryName(member)!, "外部工作.txt");
		File.Move(member, external);
		await WaitAsync(() => desktop.Members.Contains(external), "真实文件监视把外部改名同步到非活动标签成员");
		Require(desktop.CustomOrder.Contains(external) && UsageStats.Score(external) > 0, "外部改名保留自定义排序和使用记录");
		Require(!session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Contains(member), "文件通知先到时不会把旧分区成员重画为散放图标");
		RewriteSnapshot(member, external);
		takeover.Refresh();
		await WaitAsync(() => takeover.IsActive && takeover.Snapshot!.ByKey.ContainsKey(external), "外部改名的快照与分区引用完成同步");
		Require(!session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Contains(external), "改名后的分区成员不会在散放视图重复出现");
		await Task.Delay(400);
		Require(SettingsStore.ReadFrom(AppPaths.SettingsFile).Fences[0].Members.Contains(external), "外部改名引用已保存到配置");

		string intermediate = Path.Combine(Path.GetDirectoryName(external)!, "中间名称.txt");
		string final = Path.Combine(Path.GetDirectoryName(external)!, "最终名称.txt");
		File.Move(external, intermediate);
		File.Move(intermediate, final);
		await WaitAsync(() => desktop.Members.Contains(final), "连续外部改名按通知顺序同步，不依赖已消失的中间路径");
		RewriteSnapshot(external, final);
		takeover.Refresh();
		Require(UsageStats.Score(final) > 0 && !desktop.Members.Contains(intermediate), "连续改名不丢失成员和使用记录");

		string portal = fence.Frame.Active.FolderPath;
		string descendant = Directory.GetFiles(portal).First();
		UsageStats.Add(descendant);
		string newPortal = portal + "（外部改名）";
		Directory.Move(portal, newPortal);
		await WaitAsync(() => fence.Frame.Active.FolderPath == newPortal, "外部目录改名同步映射根目录");
		string newDescendant = Path.Combine(newPortal, Path.GetRelativePath(portal, descendant));
		await WaitAsync(() => fence.DisplayedPaths.Contains(newDescendant) && monitor.WatchedFolders.Contains(newPortal), "映射视图和文件监视转到新目录");
		Require(UsageStats.Score(descendant) == 0 && UsageStats.Score(newDescendant) > 0, "外部目录改名同步子项使用记录");

		fence.BeginTitleEdit();
		await Task.Delay(50);
		fence.NameInput.Text = "文件刷新期间保留草稿";
		string created = Path.Combine(newPortal, "监视新增.txt");
		File.WriteAllText(created, "文件变化验证");
		await Task.Delay(500);
		Require(fence.IsEditing && fence.NameInput.Text == "文件刷新期间保留草稿", "外部文件变化不会取消正在输入的改名草稿");
		fence.CancelEdit();
		await WaitAsync(() => fence.DisplayedPaths.Contains(created), "改名结束后补上暂缓的目录刷新");
		File.Delete(created);
		await WaitAsync(() => !fence.DisplayedPaths.Contains(created), "映射目录删除文件后自动刷新图标");

		string shortcut = Path.Combine(Path.GetDirectoryName(final)!, "更新程序.lnk");
		string backup = shortcut + ".bak";
		File.WriteAllText(shortcut, "测试快捷方式替换，不执行");
		desktop.Members.Add(shortcut);
		desktop.CustomOrder.Add(shortcut);
		bool replacementObserved = false;
		void Renamed(string oldPath, string newPath) => replacementObserved |= oldPath == shortcut && newPath == backup;
		monitor.Renamed += Renamed;
		try
		{
			File.Move(shortcut, backup);
			File.WriteAllText(shortcut, "替换后的测试内容");
			await WaitAsync(() => replacementObserved, "真实文件监视收到快捷方式备份改名通知");
			Require(desktop.Members.Contains(shortcut) && !desktop.Members.Contains(backup) && desktop.CustomOrder.Contains(shortcut),
				"快捷方式替换没有把分区成员或顺序带到备份文件");
		}
		finally
		{
			monitor.Renamed -= Renamed;
		}
		Require(GetForegroundWindow() == foreground, "散放改名与外部文件变化验证没有改变实际桌面的前台窗口");
		monitor.Dispose();
		takeover.Refresh();
		await WaitAsync(() => !takeover.IsActive && DesktopIconTarget.Find(true)?.Visible == true, "桌面目录失去监视保护时恢复系统图标并拒绝继续接管");
	}

	static void RewriteSnapshot(string oldPath, string newPath)
	{
		string file = Path.Combine(AppPaths.DataDir, "desktop-snapshot.json");
		var snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(file))!;
		var entries = snapshot.Items.Select(entry => PathUtil.AreEqual(entry.Key, oldPath)
			? entry with { Key = newPath, DisplayName = Path.GetFileNameWithoutExtension(newPath) } : entry).ToArray();
		var changed = new DesktopSnapshot(snapshot.AutoArrange, snapshot.Spacing, snapshot.IconSize, snapshot.ListBounds, entries);
		File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(changed));
		File.Move(file + ".tmp", file, true);
	}

	static bool Passive(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0;

	static async Task WaitAsync(Func<bool> condition, string message)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(8))
			{
				throw new TimeoutException(message);
			}
			await Task.Delay(20);
		}
		UiChecks.Write("通过：" + message);
	}

	static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
		UiChecks.Write("通过：" + message);
	}
}
