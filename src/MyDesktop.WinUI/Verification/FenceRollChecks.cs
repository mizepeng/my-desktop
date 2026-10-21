using System.Drawing;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.Verification;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using MyDesktop.WinUI.Views;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class FenceRollChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "四向收起与临时展开仅在独立测试桌面验证");
		var surface = session.Surfaces.First(host => host.Fences.Count >= 2);
		var frame = surface.Fences.First(fence => fence.Frame.Members.Count > 1);
		var neighbor = surface.Fences.First(fence => fence != frame);
		var settings = session.Settings;
		var originals = settings.Fences.ToDictionary(model => model.Id, model =>
		{
			var copy = new FenceSettings();
			FenceGroups.CopyFrame(model, copy);
			return copy;
		});
		var groups = settings.Groups.Select(group => new FenceGroup { Members = group.Members.ToList(), Active = group.Active }).ToList();
		var options = (settings.ExpandOnHover, settings.CollapseDelay);
		var foreground = GetForegroundWindow();
		var controller = FindWindowEx(0, 0, DesktopFixtureProtocol.ControlClass, null);
		var app = Command(controller, DesktopProbeCommand.OpenApplication);
		var taskbar = FindWindowEx(0, 0, "Static", "隔离置顶窗口");
		var activeId = frame.Frame.Active.Id;
		try
		{
			settings.ExpandOnHover = true;
			settings.CollapseDelay = 40;
			frame.SetRolledUp(false);
			neighbor.SetRolledUp(false);
			await Task.WhenAll(frame.RollReady, neighbor.RollReady);
			frame.ArrangeAt(100, 100, 360, 260);
			neighbor.ArrangeAt(600, 100, 300, 260);
			frame.CommitPosition();
			neighbor.CommitPosition();
			await WaitAsync(() => Above(app, surface.Handle) && !Topmost(surface.Handle), "普通应用仍覆盖桌面宿主");
			foreach (var edge in Enum.GetValues<RollEdge>())
			{
				frame.SetRollDirection(edge);
				var expanded = surface.ExpandedBoundsOf(frame);
				frame.SetRolledUp(true);
				await frame.RollReady;
				var strip = surface.BoundsOf(frame);
				Require(strip.Equals(surface.LayoutBoundsOf(frame)) && surface.ExpandedBoundsOf(frame).Equals(expanded)
					&& frame.Frame.Members.All(model => model.RollEdge == edge && model.RolledUp), $"{edge} 收起保持展开位置并同步标签，输入仅覆盖标题条");
				Require((edge is RollEdge.Left or RollEdge.Right ? strip.Width : strip.Height) == (int)Math.Round(36 * surface.Scale), "标题条厚度按当前宿主 DPI 计算");
				var overlay = surface.ExpandedHandle;
				var layer = frame.Parent;
				await WaitAsync(() => overlay != 0 && IsWindowVisible(overlay) && frame.InputOwner == overlay && !Topmost(overlay)
					&& Above(app, overlay) && Above(overlay, surface.Handle), "收起的分区常驻浮层，浮层平时与桌面宿主同在普通应用之下");
				frame.SetPointerInside(true);
				await frame.RollReady;
				await frame.ContentReady;
				Require(frame.Parent == layer, "悬停展开只提升浮层层级，不迁移分区视图");
				Require(overlay != 0 && IsWindowVisible(overlay) && Topmost(overlay) && !Topmost(surface.Handle)
					&& Above(taskbar, overlay) && Above(overlay, app) && Above(app, surface.Handle), "仅临时展开浮层处于应用之上和任务栏之下");
				Require(frame.InputOwner == overlay && frame.PanelVisual.Clip != null && surface.LayoutBoundsOf(frame).Equals(strip),
					"临时展开复用原视图、保留圆角裁剪且布局避让仍只计算收起条");
				var header = FenceTabs.HeaderBounds(surface, frame);
				Require(RootAt(Center(header)) == overlay && RootAt(Center(FenceTabs.HeaderBounds(surface, neighbor))) == app,
					"展开内容由浮层接收输入，其他分区没有连带提升");
				if (edge == RollEdge.Top)
				{
					var content = surface.ContentBoundsOf(frame);
					session.Gestures.Begin(new(content.Right - 8, content.Bottom - 8), false, surface, frame);
					session.Gestures.Move(new(content.Left + 8, content.Top + 8));
					Require(surface.IsBandVisible && surface.IsBandInExpandedHost && !surface.IsInputCaptured,
						"临时展开中的框选框绘制在浮层，分区之外保持穿透");
					session.Gestures.Cancel();
					Require(!surface.IsBandVisible && frame.IsTemporarilyExpanded, "取消浮层框选清理预览并保留悬停展开");
					Require(session.Tabs.BeginDrag(surface, frame, activeId, Center(header)), "浮层标签可以开始拆分拖动");
					session.Tabs.UpdateDrag(new(header.Left + header.Width / 2, header.Bottom + header.Height * 3));
					Require(session.Tabs.IsDetaching && surface.IsBandInExpandedHost, "浮层标签拆分预览与来源视图在同一宿主绘制");
					session.Tabs.EndDrag(true);
					Require(!surface.IsBandVisible && !surface.IsInputCaptured && frame.IsTemporarilyExpanded, "取消浮层标签拆分释放预览和输入区域");
					Require(SendMessageTimeout(overlay, 0x0010, 0, 0, 2, 1000, out _) != 0 && IsWindowVisible(overlay), "系统关闭命令不会单独关闭仍在使用的浮层");
					UiChecks.Write($"临时展开层级：主宿主=0x{surface.Handle:X}，浮层=0x{overlay:X}，应用=0x{app:X}，任务栏=0x{taskbar:X}。");
					for (int cycle = 0; cycle < 10; cycle++)
					{
						Command(controller, DesktopProbeCommand.RaiseDesktop);
						await WaitAsync(() => Topmost(surface.Handle) && Above(surface.Handle, app) && Above(overlay, surface.Handle),
							"显示桌面提升宿主后，悬停浮层仍位于宿主上方");
						frame.StepTab(cycle % 2 == 0 ? -1 : 1);
						Command(controller, DesktopProbeCommand.LowerDesktop);
						await WaitAsync(() => Topmost(overlay) && !Topmost(surface.Handle) && Above(app, surface.Handle) && Above(overlay, app) && Above(taskbar, overlay),
							"退出显示桌面后仅悬停浮层保持置顶");
					}
					frame.ActivateTab(activeId);
					await frame.ContentReady;
				}
				frame.SetPointerInside(false);
				await WaitAsync(() => !frame.IsTemporarilyExpanded, "鼠标离开后按配置延迟收回");
				await frame.RollReady;
				await WaitAsync(() => !Topmost(overlay) && Above(app, overlay) && Above(overlay, surface.Handle), "收回结束后浮层回到桌面层");
				Require(IsWindowVisible(overlay) && frame.Parent == layer && frame.InputOwner == overlay && surface.BoundsOf(frame).Equals(strip),
					"收回不迁移分区视图，收起条仍由浮层接收输入");
				frame.SetPointerInside(true);
				await frame.RollReady;
				frame.SetRolledUp(false);
				Require(!frame.IsTemporarilyExpanded && !IsWindowVisible(overlay) && frame.InputOwner == surface.Handle, "悬停状态改为固定展开时回到桌面宿主并收起空浮层");
				await frame.RollReady;
				Require(surface.ExpandedBoundsOf(frame).Equals(expanded) && surface.BoundsOf(frame).Equals(expanded) && !Topmost(surface.Handle), "固定展开恢复全部内容并保持桌面层");
			}

			frame.SetPointerInside(false);
			foreach (var vertical in new[] { false, true })
			{
				frame.SetRolledUp(false);
				await frame.RollReady;
				frame.ArrangeAt(vertical ? 0 : 100, 100, 360, 260);
				frame.SetRollDirection(null);
				frame.CommitPosition();
				frame.SetRolledUp(true);
				await frame.RollReady;
				var strip = FenceTabs.HeaderBounds(surface, frame);
				var point = Center(strip);
				Require(surface.Placement.Begin(frame, point, FenceEdges.None), "收起条可以沿屏幕移动");
				var target = vertical ? new Point(point.X + surface.WorkArea.Right - strip.Right, point.Y)
					: new Point(point.X, point.Y + surface.WorkArea.Bottom - strip.Bottom);
				surface.Placement.Update(target);
				await surface.Placement.EndAsync(false);
				var moved = FenceTabs.HeaderBounds(surface, frame);
				Require(frame.Frame.Active.RollEdge == (vertical ? RollEdge.Right : RollEdge.Bottom)
					&& Center(moved) == target, "收起条换到屏幕另一边时自动换向且不跳离松手位置");
			}
			frame.SetRolledUp(false);
			await frame.RollReady;
			frame.ArrangeAt(100, 100, 360, 260);
			frame.CommitPosition();

			frame.SetRollDirection(RollEdge.Top);
			frame.SetRolledUp(true);
			await frame.RollReady;
			frame.SetTemporaryExpanded(true);
			frame.SetTemporaryExpanded(false);
			await frame.RollReady;
			Require(!Topmost(surface.ExpandedHandle) && frame.InputOwner == surface.ExpandedHandle, "展开尚未出第一帧就取消时浮层也回到桌面层");
			frame.SetRolledUp(false);
			await Task.Delay(40);
			frame.SetRolledUp(true);
			await frame.RollReady;
			Require(frame.IsCollapsed && surface.BoundsOf(frame).Equals(surface.LayoutBoundsOf(frame)), "中途反转动画后回到正确的收起范围");
			settings.ExpandOnHover = false;
			frame.SetPointerInside(true);
			await Task.Delay(80);
			Require(!frame.IsTemporarilyExpanded, "关闭悬停展开时保留收起状态");
			frame.SetDropInside(true);
			await frame.RollReady;
			frame.SetPointerInside(false);
			await Task.Delay(100);
			Require(frame.IsTemporarilyExpanded && Topmost(surface.ExpandedHandle), "拖入文件仍可临时展开，拖放未结束时不收起");
			session.DragDrop.End();
			await WaitAsync(() => !frame.IsTemporarilyExpanded, "拖放结束后恢复延迟收回");
			await frame.RollReady;

			settings.ExpandOnHover = true;
			frame.SetPointerInside(true);
			await frame.RollReady;
			var menu = frame.CreateTitleMenu();
			bool opened = false;
			menu.Opened += (_, _) => opened = true;
			try
			{
				menu.ShowAt(frame);
				await WaitAsync(() => opened, "临时浮层可以打开标题菜单");
				frame.SetPointerInside(false);
				await Task.Delay(100);
				Require(frame.IsTemporarilyExpanded && Topmost(surface.ExpandedHandle), "菜单打开时保持临时展开");
			}
			finally { menu.Hide(); }
			await WaitAsync(() => !frame.IsTemporarilyExpanded, "菜单关闭后恢复收回计时");
			await frame.RollReady;
			frame.SetPointerInside(true);
			await frame.RollReady;
			frame.BeginTitleEdit();
			await WaitAsync(() => surface.IsEditing && frame.IsEditing, "浮层编辑器仍由原桌面会话管理");
			frame.SetPointerInside(false);
			await Task.Delay(100);
			Require(frame.IsTemporarilyExpanded && frame.InputOwner == surface.ExpandedHandle, "编辑期间保持展开与输入宿主");
			frame.CancelEdit();
			await WaitAsync(() => !frame.IsTemporarilyExpanded, "结束编辑后恢复收回计时");
			await frame.RollReady;

			frame.SetRollDirection(RollEdge.Right);
			frame.SetPointerInside(true);
			await frame.RollReady;
			var detached = session.Tabs.Detach(surface, frame, activeId, null) ?? throw new InvalidOperationException("临时展开标签拆出失败。");
			await Task.WhenAll(frame.RollReady, detached.RollReady, frame.ContentReady, detached.ContentReady);
			Require(detached.IsTemporarilyExpanded && frame.IsTemporarilyExpanded && detached.InputOwner == frame.InputOwner,
				"拆出的标签继承临时展开状态并共用同屏浮层");
			Require(session.Tabs.Merge(surface, detached, frame), "浮层中的标签可以重新合并");
			await frame.ContentReady;
			Require(frame.Frame.Active.Id == activeId && frame.IsTemporarilyExpanded && surface.Fences.Count == 2, "合并保留活动标签及临时展开状态");
			session.Takeover!.Release("隔离检查撤销临时展开。");
			Require(!frame.IsTemporarilyExpanded && !Topmost(surface.ExpandedHandle) && frame.InputOwner == surface.ExpandedHandle, "接管撤销时浮层立即回到桌面层");
			session.Takeover.Refresh();
			await WaitAsync(() => session.Takeover.IsActive, "回收浮层后可以重新接管测试桌面");
			Require(GetForegroundWindow() == foreground, "四向动画、临时层级与输入检查没有改变用户前台窗口");
		}
		finally
		{
			session.CancelInteractions();
			session.ResetTemporaryExpansions();
			session.Selection.Replace(new Dictionary<Guid, IReadOnlyList<string>>());
			session.SetContext(surface, null);
			foreach (var fence in surface.Fences.ToArray()) { surface.RemoveFence(fence); }
			settings.Groups = groups;
			foreach (var model in settings.Fences) { FenceGroups.CopyFrame(originals[model.Id], model); }
			(settings.ExpandOnHover, settings.CollapseDelay) = options;
			foreach (var model in new FenceWorkspace(settings).Frames) { surface.AddFence(model); }
			session.LayoutChanged(surface, surface.Fences[0]);
			await Task.WhenAll(surface.Fences.Select(fence => fence.ContentReady));
			Command(controller, DesktopProbeCommand.CloseApplication);
			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(50);
		}
	}

	static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
	static nint RootAt(Point point) => GetAncestor(WindowFromPoint(new(point.X, point.Y)), 2);
	static bool Topmost(nint window) => (GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
	static bool Above(nint upper, nint lower)
	{
		var order = new List<nint>();
		EnumWindows((window, _) => { order.Add(window); return true; }, 0);
		return order.IndexOf(upper) >= 0 && order.IndexOf(upper) < order.IndexOf(lower);
	}
	static nint Command(nint controller, DesktopProbeCommand command)
	{
		if (SendMessageTimeout(controller, DesktopFixtureProtocol.DesktopProbeMessage, (nint)command, 0, 2, 1000, out var result) == 0 || result == 0)
		{
			throw new InvalidOperationException($"隔离桌面命令失败：{command}。");
		}
		return result;
	}
	static async Task WaitAsync(Func<bool> condition, string message)
	{
		for (int attempt = 0; !condition(); attempt++)
		{
			if (attempt >= 150)
			{
				EnumWindows((window, _) =>
				{
					if (IsWindowVisible(window))
					{
						UiChecks.Write($"层级快照：0x{window:X} {GetClassName(window)}，置顶={Topmost(window)}，owner=0x{GetWindow(window, GW_OWNER):X}。");
					}
					return true;
				}, 0);
				throw new TimeoutException(message);
			}
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
