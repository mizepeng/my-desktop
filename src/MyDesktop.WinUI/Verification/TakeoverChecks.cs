using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MyDesktop.Core;
using MyDesktop.Verification;
using MyDesktop.WinUI.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class TakeoverChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		var takeover = session.Takeover ?? throw new InvalidOperationException("没有创建桌面接管服务。");
		var control = FindWindowEx(0, 0, DesktopFixtureProtocol.ControlClass, null);
		await WaitAsync(() => takeover.IsActive && takeover.Target?.IsCurrent == true, "桌面重建后重新读取并接管测试图标列表");
		var list = Command(control, DesktopProbeCommand.GetIconList);
		Require(!IsWindowVisible(list) && takeover.GuardProcessId is > 0, "全部图像与恢复进程就绪后才隐藏测试列表");
		Require(takeover.Target!.Fixture && takeover.Target.List == list, "接管目标只指向隔离桌面的图标列表");
		var icons = session.Surfaces.SelectMany(surface => surface.DesktopIcons.Keys).ToArray();
		Require(icons.Length == 2 && icons.All(path => Path.GetFileName(path).StartsWith("散放", StringComparison.Ordinal)), "分区成员没有在散放图标中重复显示");
		Require(session.Surfaces.Sum(surface => UiChecks.Descendants(surface.DesktopIcons).OfType<Image>().Count(image => image.Source is WriteableBitmap)) == 2,
			"散放图标使用真实 Shell 图像，位于现有 WinUI 宿主内部");
		var surface = session.Surfaces.First(surface => surface.DesktopIcons.Keys.Count > 0);
		var bounds = surface.DesktopIcons.HitRectangles.First();
		Require(GetAncestor(WindowFromPoint(new MyDesktop.Native.POINT(bounds.Left + bounds.Width / 2, bounds.Top + 10)), 2) == surface.Handle,
			"散放图标所在格子可接收输入");
		Require(GetAncestor(WindowFromPoint(new MyDesktop.Native.POINT(bounds.Right + 20, bounds.Top + 10)), 2) == takeover.Target.Root,
			"格子之外的桌面空白仍然穿透");
		takeover.Select(icons[0], false);
		Require(takeover.SelectedKeys.SequenceEqual(new[] { icons[0] }) && surface.DesktopIcons.SelectedKeys.Contains(icons[0]), "散放选择状态已同步到 XAML 视图");
		takeover.Select(icons[1], true);
		Require(takeover.SelectedKeys.Count == 2, "追加选择保留已选项目");
		takeover.Select(null, false);
		Require(takeover.SelectedKeys.Count == 0, "桌面空白选择可以清空散放选择");

		var hiddenTab = session.Settings.Fences[0];
		hiddenTab.Members.Add(icons[0]);
		takeover.Refresh();
		await WaitAsync(() => !session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Contains(icons[0]), "非活动标签中的成员也从散放视图移除");
		hiddenTab.Members.Remove(icons[0]);
		takeover.Refresh();
		await WaitAsync(() => session.Surfaces.SelectMany(item => item.DesktopIcons.Keys).Count() == 2, "移出分区后按 Explorer 的排列重新显示");

		string file = Path.Combine(AppPaths.DataDir, "desktop-snapshot.json");
		string original = File.ReadAllText(file);
		File.WriteAllText(file, "{损坏快照");
		takeover.Refresh();
		await WaitAsync(() => !takeover.IsActive && IsWindowVisible(list)
			&& session.Surfaces.All(item => item.DesktopIcons.Visibility == Visibility.Collapsed), "读取失败时恢复测试列表并释放散放图标输入区域");
		Require(session.Surfaces.Any(item => item.Notice.Length > 0), "接管失败在界面中明确提示");
		File.WriteAllText(file, original);
		takeover.Refresh();
		await WaitAsync(() => takeover.IsActive && !IsWindowVisible(list), "快照恢复后重新接管");

		Command(control, DesktopProbeCommand.HideIconsBySystem);
		await WaitAsync(() => !takeover.IsActive && session.Surfaces.All(item => item.DesktopIcons.Visibility == Visibility.Collapsed), "用户隐藏图标时同步隐藏散放视图");
		Require(!IsWindowVisible(list), "恢复保护没有覆盖用户主动隐藏图标的选择");
		Command(control, DesktopProbeCommand.ShowIconsBySystem);
		takeover.Refresh();
		await WaitAsync(() => takeover.IsActive && !IsWindowVisible(list), "重新显示图标后恢复接管");

		int oldGuard = takeover.GuardProcessId!.Value;
		using (var guard = Process.GetProcessById(oldGuard))
		{
			guard.Kill();
			await guard.WaitForExitAsync();
		}
		await WaitAsync(() => takeover.GuardProcessId != oldGuard, "恢复进程退出后旧接管已撤销");
		await WaitAsync(() => takeover.IsActive && takeover.GuardProcessId is > 0 && takeover.GuardProcessId != oldGuard, "新的恢复保护就绪后才能再次接管");
	}

	static nint Command(nint controller, DesktopProbeCommand command)
	{
		if (SendMessageTimeout(controller, DesktopFixtureProtocol.DesktopProbeMessage, (nint)command, 0, 2, 1000, out var result) == 0 || result == 0)
		{
			throw new InvalidOperationException("隔离桌面控制失败：" + command);
		}
		return result;
	}

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
