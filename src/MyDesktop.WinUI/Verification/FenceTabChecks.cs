using System.Drawing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using MyDesktop.WinUI.Views;
using FenceView = MyDesktop.WinUI.Views.FenceView;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class FenceTabChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "标签合并、排序与拆分仅在独立测试桌面验证");
		var surface = session.Surfaces.First(host => host.Fences.Count >= 2);
		var frame = surface.Fences.First(fence => fence.Frame.Members.Count > 1);
		var neighbor = surface.Fences.First(fence => fence != frame);
		var settings = session.Settings;
		var groups = settings.Groups.Select(group => new FenceGroup { Members = group.Members.ToList(), Active = group.Active }).ToList();
		var originals = settings.Fences.ToDictionary(model => model.Id, model =>
		{
			var copy = new FenceSettings();
			FenceGroups.CopyFrame(model, copy);
			return copy;
		});
		var options = (settings.SwitchTabOnHover, settings.TabHoverDelay, settings.SnapToEdges, settings.SnapToGrid);
		var foreground = GetForegroundWindow();
		var handles = session.Surfaces.Select(host => host.Handle).ToArray();
		Guid desktop = frame.Frame.Members.First(model => !model.IsPortal).Id, portal = frame.Frame.Members.First(model => model.IsPortal).Id;
		Guid incoming = neighbor.Frame.Active.Id;
		try
		{
			settings.SnapToEdges = settings.SnapToGrid = false;
			frame.ArrangeAt(80, 80, 360, 260);
			neighbor.ArrangeAt(540, 80, 300, 240);
			frame.CommitPosition();
			neighbor.CommitPosition();
			frame.ActivateTab(desktop);
			await frame.ContentReady;
			session.SelectFence(frame, frame.DisplayedPaths, false);
			frame.ActivateTab(portal);
			Require(frame.DisplayedPaths.Count == 0 && session.Selection.InScope(desktop).Count == 0, "切换时立即移除旧标签图标和选择，不能把旧内容误当作新标签文件");
			await frame.ContentReady;
			settings.SwitchTabOnHover = true;
			settings.TabHoverDelay = 40;
			frame.HoverTab(desktop);
			frame.StopTabHover();
			await Task.Delay(80);
			Require(frame.Frame.Active.Id == portal, "离开标签后取消待执行的悬停切换");
			frame.HoverTab(desktop);
			await WaitAsync(() => frame.Frame.Active.Id == desktop, "悬停计时到达后切换标签");
			settings.SwitchTabOnHover = false;
			frame.HoverTab(portal);
			await Task.Delay(80);
			Require(frame.Frame.Active.Id == desktop, "关闭悬停切换后不自动切页");
			frame.StepTab(-1);
			Require(frame.Frame.Active.Id == desktop, "滚轮切换到头后不循环");
			frame.StepTab(1);
			await frame.ContentReady;
			Require(frame.Frame.Active.Id == portal, "滚轮沿标签顺序切换内容");
			frame.Frame.Active.Locked = true;
			Require(frame.CreateTitleMenu().Items.OfType<MenuFlyoutItem>().Where(item => item.Text.StartsWith("标签") || item.Text == "拆出标签").All(item => !item.IsEnabled)
				&& !session.Tabs.Merge(surface, neighbor, frame) && !session.Tabs.BeginDrag(surface, frame, portal, Point.Empty), "锁定的标签组拒绝合并、排序与拆出");
			frame.Frame.Active.Locked = false;

			var targetVisual = frame.PanelVisual;
			var sourceHeader = FenceTabs.HeaderBounds(surface, neighbor);
			var targetHeader = FenceTabs.HeaderBounds(surface, frame);
			var sourcePoint = Center(sourceHeader);
			frame.BeginTitleEdit();
			surface.Placement.Begin(neighbor, sourcePoint, FenceEdges.None);
			surface.Placement.Update(Center(targetHeader));
			Require(!frame.IsMergeTarget, "正在编辑的目标分区不显示无法完成的合并提示");
			surface.Placement.Cancel();
			frame.CancelEdit();
			await frame.ContentReady;
			Require(surface.Placement.Begin(neighbor, sourcePoint, FenceEdges.None), "普通标题栏拖动可以开始合并");
			surface.Placement.Update(Center(targetHeader));
			Require(frame.IsMergeTarget, "经过目标标题栏时显示合并提示");
			await surface.Placement.EndAsync(false);
			await frame.ContentReady;
			Require(surface.Fences.Count == 1 && frame.Frame.Members.Select(model => model.Id).SequenceEqual([desktop, portal, incoming])
				&& frame.Frame.Active.Id == incoming, "松手合并到已有标签组，目标在前并显示来源标签");
			Require(ReferenceEquals(frame.PanelVisual, targetVisual) && !frame.IsMergeTarget && !session.IsInteracting
				&& frame.Frame.Members.All(model => model.X == frame.Frame.Active.X && model.Width == frame.Frame.Active.Width), "合并复用目标视觉、同步布局并结束交互");
			Require(GetAncestor(WindowFromPoint(new(sourceHeader.Left + sourceHeader.Width / 2, sourceHeader.Top + sourceHeader.Height / 2)), 2) != surface.Handle,
				"合并后移除来源视图的输入区域");
			Require(session.Tabs.Move(surface, frame, incoming, 0), "菜单排序可以移动当前标签");
			Require(TabIds(frame).SequenceEqual([incoming, desktop, portal]) && frame.Frame.Active.Id == incoming, "标签控件顺序随配置更新，活动标签保持不变");
			var header = FenceTabs.HeaderBounds(surface, frame);
			Require(session.Tabs.BeginDrag(surface, frame, incoming, Center(header)), "Shift 标签拖动进入独立交互状态");
			session.Tabs.UpdateDrag(new(header.Left + header.Width / 2, header.Bottom + header.Height * 3));
			Require(session.IsInteracting && session.Tabs.IsDetaching && surface.IsBandVisible && surface.IsInputCaptured, "拖出标题栏后在现有宿主内显示拆分预览");
			frame.ActivateTab(desktop);
			Require(frame.Frame.Active.Id == incoming, "拖动期间不能切换来源标签或替换源控件");
			session.CancelInteractions();
			Require(surface.Fences.Count == 1 && !session.IsInteracting && !surface.IsInputCaptured && !surface.IsBandVisible, "取消拆分释放预览和整屏输入区域且不改变分组");
			session.Tabs.BeginDrag(surface, frame, incoming, Center(header));
			session.Tabs.UpdateDrag(new(header.Right - 2, header.Top + header.Height / 2));
			session.Tabs.EndDrag(false);
			Require(TabIds(frame).SequenceEqual([desktop, portal, incoming]), "Shift 拖动按标签实际布局调整顺序");
			session.Tabs.BeginDrag(surface, frame, portal, Center(header));
			session.Tabs.UpdateDrag(new(header.Left + header.Width / 2, header.Bottom + header.Height * 3));
			session.Tabs.EndDrag(false);
			var detached = surface.Fences.Single(fence => fence.Frame.Active.Id == portal);
			await detached.ContentReady;
			Require(surface.Fences.Count == 2 && settings.Groups.Single().Members.Count == 2 && detached.Frame.Active.IsPortal
				&& detached.DisplayedPaths.Count > 0, "Shift 松手拆出非活动标签，映射目录和图标内容保持独立");
			detached.ItemList.SelectedItems.Add(detached.ItemList.Items[0]);
			Require(session.ActiveFence == detached && session.Selection.InScope(portal).Count == 1 && detached.AllowDrop && detached.ItemList.CanDragItems,
				"新拆出的视图连接共享选择与文件拖放入口");
			detached.BeginTitleEdit();
			await WaitAsync(() => surface.IsEditing && detached.IsEditing, "拆出视图的改名编辑器连接宿主输入状态");
			detached.CancelEdit();
			var detachedBounds = surface.BoundsOf(detached);
			frame.ActivateTab(desktop);
			await frame.ContentReady;
			string selected = frame.DisplayedPaths.First();
			session.SelectFence(frame, [selected], true);
			Require(session.Tabs.Merge(surface, frame, detached), "整个来源标签组可以合并到独立目标分区");
			await detached.ContentReady;
			Require(surface.Fences.Count == 1 && TabIds(detached).SequenceEqual([portal, desktop, incoming])
				&& detached.Frame.Active.Id == desktop && surface.BoundsOf(detached).Equals(detachedBounds) && session.Selection.Keys.SequenceEqual([selected]),
				"整组合并保留目标布局、来源顺序与可见选择，并清理隐藏选择");
			var activeDetached = session.Tabs.Detach(surface, detached, desktop, null);
			Require(activeDetached != null, "菜单可以拆出活动标签");
			await activeDetached!.ContentReady;
			Require(detached.Frame.Active.Id == incoming && session.Selection.InScope(desktop).SequenceEqual([selected]),
				"拆出活动标签后显示相邻标签，拆出的可见图标保留选择");
			Require(session.Tabs.Detach(surface, detached, incoming, null) != null && settings.Groups.Count == 0 && surface.Fences.Count == 3,
				"继续拆出后解散只剩一个标签的组");
			await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady));
			await Task.Delay(400);
			var saved = SettingsStore.ReadFrom(AppPaths.SettingsFile);
			Require(saved.Groups.Count == 0 && saved.Fences.Count == 3 && saved.Fences.Single(model => model.Id == portal).IsPortal,
				"拆分结果和独立分区配置通过共享存储保存");
			Require(session.Surfaces.Select(host => host.Handle).SequenceEqual(handles) && GetForegroundWindow() == foreground && session.Takeover!.IsActive,
				"标签操作全程复用宿主、保持接管且未改变前台窗口");
		}
		finally
		{
			session.CancelInteractions();
			session.Selection.Replace(new Dictionary<Guid, IReadOnlyList<string>>());
			session.SetContext(surface, null);
			foreach (var fence in surface.Fences.ToArray()) { surface.RemoveFence(fence); }
			settings.Groups = groups;
			foreach (var model in settings.Fences) { FenceGroups.CopyFrame(originals[model.Id], model); }
			(settings.SwitchTabOnHover, settings.TabHoverDelay, settings.SnapToEdges, settings.SnapToGrid) = options;
			foreach (var frameModel in new FenceWorkspace(settings).Frames) { surface.AddFence(frameModel); }
			session.LayoutChanged(surface, surface.Fences[0]);
			await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(100);
		}
		UiChecks.Write("通过：移除的分区及材质链接经主动回收后没有原生崩溃。");
	}

	static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
	static Guid[] TabIds(FenceView frame) => UiChecks.Descendants(frame).OfType<ToggleButton>().Where(button => button.Tag is Guid).Select(button => (Guid)button.Tag).ToArray();

	static async Task WaitAsync(Func<bool> condition, string message)
	{
		for (int attempt = 0; !condition(); attempt++)
		{
			if (attempt >= 100) { throw new TimeoutException(message); }
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
