using Microsoft.UI.Xaml.Input;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using MyDesktop.WinUI.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class RenameChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "编辑验证只能在非输入桌面运行");
		var surface = session.Surfaces.First(surface => surface.Fences.Count > 0);
		var frame = surface.Fences[0];
		var other = surface.Fences[1];
		var hwnd = surface.Handle;
		var foreground = GetForegroundWindow();
		string title = frame.Frame.Active.Title;

		frame.BeginTitleEdit();
		frame.CancelEdit();
		await Task.Delay(50);
		Require(!surface.IsEditing && Passive(hwnd), "快速取消不会被排队的焦点请求重新激活");
		frame.BeginTitleEdit();
		await Task.Delay(50);
		Require(surface.IsEditing && !Passive(hwnd), "仅在编辑期间临时允许宿主激活");
		Require(ReferenceEquals(FocusManager.GetFocusedElement(frame.XamlRoot), frame.NameInput), "输入框获得 XAML 焦点");
		frame.NameInput.Text = "未确认的标题";
		Require(frame.Frame.Active.Title == title, "标题草稿未提前写入配置模型");
		frame.CancelEdit();
		Require(frame.Frame.Active.Title == title && Passive(hwnd), "取消标题编辑不保存草稿并恢复非激活状态");
		UiChecks.Write("检查：首次编辑后的层级及隐藏输入窗口。");
		EnumWindows((window, _) =>
		{
			if (GetWindow(window, GW_OWNER) == hwnd)
			{
				UiChecks.Write($"宿主名下窗口：0x{window:X} {GetClassName(window)}，可见 {IsWindowVisible(window)}。");
			}
			return true;
		}, 0);
		await DesktopPlacementChecks.RunAsync(hwnd);

		frame.SetRolledUp(true);
		frame.BeginTitleEdit();
		await Task.Delay(50);
		Require(frame.Frame.Active.RolledUp && Math.Abs(frame.VisibleHeight - frame.Height) < 0.001,
			$"编辑收起的分区只临时展开视图（收起={frame.Frame.Active.RolledUp}，可见={frame.VisibleHeight}，展开={frame.Height}）");
		frame.SetPointerInside(true);
		Require(!frame.IsTemporarilyExpanded && frame.InputOwner == surface.ExpandedHandle && !Topmost(surface.ExpandedHandle),
			"普通编辑期间悬停不会临时展开，收起的分区仍由桌面层的浮层接收输入");
		frame.SetPointerInside(false);
		frame.CancelEdit();
		Require(frame.Frame.Active.RolledUp && frame.VisibleHeight == 36, "取消后恢复收起状态");
		frame.SetRolledUp(false);
		frame.BeginTitleEdit();
		await Task.Delay(50);
		frame.NameInput.Text = " ";
		Require(!frame.CommitEdit() && frame.IsEditing && frame.EditErrorText.Length > 0 && frame.Frame.Active.Title == title, "空标题明确报错并保留编辑框");
		frame.NameInput.Text = title + "（新标题）";
		Require(frame.CommitEdit() && Passive(hwnd), "确认标题后更新标签文字并恢复非激活状态");
		await Task.Delay(400);
		var saved = SettingsStore.ReadFrom(AppPaths.SettingsFile);
		Require(saved.Fences.Single(fence => fence.Id == frame.Frame.Active.Id).Title == title + "（新标题）", "标题改名已持久保存");
		Require(saved.Fences[0].Title == "工作", "标题改名没有覆盖同组其他标签");

		frame.BeginTitleEdit();
		await Task.Delay(50);
		other.BeginTitleEdit();
		await Task.Delay(50);
		Require(!frame.IsEditing && other.IsEditing && surface.IsEditing, "同一宿主同时只有一个编辑框持有输入");
		other.CancelEdit();
		frame.BeginTitleEdit();
		await Task.Delay(50);
		frame.NameInput.Text = "切换时丢弃";
		var portal = frame.Frame.Active;
		frame.ActivateTab(frame.Frame.Members[0].Id);
		Require(!frame.IsEditing && Passive(hwnd) && portal.Title == title + "（新标题）", "切换标签取消未确认的改名");
		frame.ActivateTab(portal.Id);
		await frame.ContentReady.WaitAsync(TimeSpan.FromSeconds(10));

		var files = frame.ItemList.Items.Cast<FenceItem>().OrderBy(item => item.Path).ToArray();
		var source = files[0];
		string content = File.ReadAllText(source.Path);
		string existingContent = File.ReadAllText(files[1].Path);
		frame.BeginItemRename(source.Path);
		await Task.Delay(50);
		frame.NameInput.Text = "取消后不应出现";
		frame.CancelEdit();
		Require(File.Exists(source.Path) && Directory.GetFiles(portal.FolderPath).Length == 2 && Passive(hwnd), "取消文件改名没有改动文件系统");
		frame.BeginItemRename(source.Path);
		await Task.Delay(50);
		frame.NameInput.Text = "NUL";
		Require(!frame.CommitEdit() && frame.IsEditing && frame.EditErrorText.Length > 0 && File.Exists(source.Path), "保留名称被拒绝且源文件仍在");
		frame.NameInput.Text = files[1].Name;
		Require(!frame.CommitEdit() && frame.EditErrorText.Contains("同名") && File.ReadAllText(files[1].Path) == existingContent, "名称冲突明确提示且不会覆盖文件");
		var desktop = session.Settings.Fences[0];
		desktop.Members.Add(source.Path);
		desktop.CustomOrder.Add(source.Path);
		UsageStats.Add(source.Path);
		frame.NameInput.Text = source.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "WinUI重命名.txt" : "WinUI重命名";
		Require(frame.CommitEdit(), "系统 Shell 文件改名成功");
		await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady)).WaitAsync(TimeSpan.FromSeconds(10));
		string renamed = Path.Combine(portal.FolderPath, "WinUI重命名.txt");
		Require(!File.Exists(source.Path) && File.ReadAllText(renamed) == content, "真实文件已改名并保留内容和扩展名");
		Require(desktop.Members.Contains(renamed) && desktop.CustomOrder.Contains(renamed), "所有分区中的成员和顺序引用一起更新");
		Require(UsageStats.Score(source.Path) == 0 && UsageStats.Score(renamed) > 0, "使用记录跟随文件改名");
		Require(frame.DisplayedPaths.Contains(renamed) && !frame.DisplayedPaths.Contains(source.Path), "改名后重新读取 Shell 名称和图像");
		await Task.Delay(400);
		saved = SettingsStore.ReadFrom(AppPaths.SettingsFile);
		Require(saved.Fences[0].Members.Contains(renamed) && saved.Fences[0].CustomOrder.Contains(renamed), "文件路径改动已保存到配置");
		var renamedItem = frame.ItemList.Items.Cast<FenceItem>().Single(item => item.Path == renamed);
		frame.BeginItemRename(renamed);
		await Task.Delay(50);
		frame.NameInput.Text = renamedItem.Name.Replace("WinUI", "WINUI", StringComparison.Ordinal);
		Require(frame.CommitEdit(), "仅修改文件名大小写的 Shell 改名成功");
		await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady)).WaitAsync(TimeSpan.FromSeconds(10));
		string upper = Path.Combine(portal.FolderPath, "WINUI重命名.txt");
		Require(Directory.GetFiles(portal.FolderPath).Contains(upper, StringComparer.Ordinal) && desktop.Members.Contains(upper, StringComparer.Ordinal),
			"文件系统和分区引用都保留新的大小写");

		string folder = Path.Combine(portal.FolderPath, "待改目录");
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "子项.txt"), "目录改名验证");
		other.Frame.Active.IsPortal = true;
		other.Frame.Active.FolderPath = folder;
		frame.RefreshContent();
		await frame.ContentReady.WaitAsync(TimeSpan.FromSeconds(10));
		frame.BeginItemRename(folder);
		await Task.Delay(50);
		frame.NameInput.Text = "新目录";
		Require(frame.CommitEdit(), "映射目录中的普通文件夹可以改名");
		await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady)).WaitAsync(TimeSpan.FromSeconds(10));
		string newFolder = Path.Combine(portal.FolderPath, "新目录");
		Require(!Directory.Exists(folder) && other.Frame.Active.FolderPath == newFolder && other.DisplayedPaths.Single() == Path.Combine(newFolder, "子项.txt"),
			"目录改名同步另一个分区的映射并刷新内容");
		Require(!surface.IsEditing && Passive(hwnd), "全部编辑结束后恢复桌面宿主状态");
		Require(GetForegroundWindow() == foreground, "编辑验证没有改变实际输入桌面的前台窗口");
	}

	static bool Passive(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0;
	static bool Topmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

	static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
		UiChecks.Write("通过：" + message);
	}
}
