using System.Drawing;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using MyDesktop.WinUI.Views;
using FenceView = MyDesktop.WinUI.Views.FenceView;

namespace MyDesktop.WinUI.Verification;

internal static class FencePlacementChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "分区布局仅在独立测试桌面验证");
		var surface = session.Surfaces.First(item => item.Fences.Count >= 2);
		var frame = surface.Fences.First(item => item.Frame.Members.Count > 1);
		var neighbor = surface.Fences.First(item => item != frame);
		var originals = surface.Fences.ToDictionary(item => item, item => (Bounds: Bounds(surface, item), item.Frame.Active.RolledUp));
		var settings = session.Settings;
		var options = (settings.SnapToEdges, settings.SnapToGrid, settings.SnapGap);
		var foreground = MyDesktop.Native.NativeMethods.GetForegroundWindow();
		double scale = surface.Scale;
		int Px(double value) => (int)Math.Round(value * scale);
		try
		{
			settings.SnapToEdges = settings.SnapToGrid = false;
			settings.SnapGap = 0;
			frame.SetRolledUp(false);
			neighbor.SetRolledUp(false);
			await Task.WhenAll(frame.RollReady, neighbor.RollReady);
			frame.ArrangeAt(40, 80, 240, 240);
			neighbor.ArrangeAt(500, 80, 200, 240);
			frame.CommitPosition();
			neighbor.CommitPosition();
			await frame.ContentReady;
			frame.UpdateLayout();
			Require(frame.ResizeEdgesAt(new(frame.Width - 1, frame.Height - 1)) == (FenceEdges.Right | FenceEdges.Bottom), "右下角命中两条调整边");
			var start = Bounds(surface, frame);
			var point = new Point(start.Right - 1, start.Bottom - 1);
			Require(surface.Placement.Begin(frame, point, FenceEdges.Right | FenceEdges.Bottom) && session.IsInteracting, "调整大小纳入交互状态并暂缓内容替换");
			surface.Placement.Update(new(point.X + Px(60), point.Y + Px(40)));
			var resized = Bounds(surface, frame);
			Require(resized.Left == start.Left && resized.Top == start.Top && resized.Width == start.Width + Px(60) && resized.Height == start.Height + Px(40),
				"拖动右下角保持左上角，宽高按物理像素变化");
			await surface.Placement.EndAsync(false);
			Require(!session.IsInteracting && frame.Frame.Members.All(model => model.Width == resized.Width && model.Height == resized.Height),
				"结束调整大小后同步全部标签并恢复刷新");
			await Task.Delay(400);
			var saved = SettingsStore.ReadFrom(AppPaths.SettingsFile);
			Require(saved.Fences.Where(model => frame.Frame.Members.Any(member => member.Id == model.Id))
				.All(model => model.Width == resized.Width && model.Height == resized.Height), "新尺寸已通过共享存储保存");
			frame.Frame.Active.Locked = true;
			Require(!surface.Placement.Begin(frame, point, FenceEdges.Left) && frame.ResizeEdgesAt(new(1, 10)) == FenceEdges.None,
				"锁定分区没有移动或调整大小入口");
			frame.Frame.Active.Locked = false;
			Require(!new FencePlacement(surface, settings, true).Begin(frame, point, FenceEdges.None), "只读预览不接受分区布局修改");
			Require(frame.GridSizes(scale, surface.WorkArea).Heights.Count > 0, "按实际图标容器测得整行吸附尺寸");

			start = Bounds(surface, frame);
			point = new(start.Left + Px(60), start.Top + Px(12));
			var shown = surface.Fences.ToDictionary(item => item, item => item.ItemList.Items.ToArray());
			Require(shown[frame].Length > 0, "拖动前分区已显示图标");
			surface.Placement.Begin(frame, point, FenceEdges.None);
			surface.Placement.Update(new(point.X + Px(480), point.Y + Px(60)));
			Require(Bounds(surface, frame).IntersectsWith(Bounds(surface, neighbor)), "移动期间允许经过相邻分区");
			await surface.Placement.EndAsync(false);
			Require(!Bounds(surface, frame).IntersectsWith(Bounds(surface, neighbor)), "松手后滑到最近可用位置");
			await Task.Delay(400);
			// 拖动结束只补暂缓的刷新，重建图标列表会让分区闪一下。
			Require(shown.All(pair => pair.Key.ItemList.Items.SequenceEqual(pair.Value)), "拖动结束没有重建任何分区的图标列表");

			// 同一帧内连续移动时 XAML 还没看到新的 Composition 平移，指针换算不能叠加平移导致坐标发散。
			start = Bounds(surface, frame);
			var grab = new Windows.Foundation.Point((start.Left - surface.WorkArea.Left) / scale + 60, (start.Top - surface.WorkArea.Top) / scale + 12);
			surface.Placement.Begin(frame, frame.ScreenPoint(grab), FenceEdges.None);
			for (int step = 1; step <= 60; step++)
			{
				surface.Placement.Update(frame.ScreenPoint(new Windows.Foundation.Point(grab.X + step * 4, grab.Y + step * 2)));
			}
			var dragged = Bounds(surface, frame);
			surface.Placement.Cancel();
			Require(Math.Abs(dragged.Left - start.Left - Px(240)) <= 2 && Math.Abs(dragged.Top - start.Top - Px(120)) <= 2,
				$"同一帧内连续拖动跟随指针位移，实际 {dragged.Left - start.Left},{dragged.Top - start.Top}");

			neighbor.ArrangeAt(0, 0, surface.WorkArea.Width / scale, surface.WorkArea.Height / scale);
			start = Bounds(surface, frame);
			point = new(start.Left + Px(60), start.Top + Px(12));
			surface.Placement.Begin(frame, point, FenceEdges.None);
			await surface.Placement.EndAsync(false);
			Require(Bounds(surface, frame) == start, "只点击标题栏不重新摆放原有重叠布局");
			surface.Placement.Begin(frame, point, FenceEdges.None);
			surface.Placement.Update(new(point.X - Px(30), point.Y + Px(20)));
			await surface.Placement.EndAsync(false);
			Require(Bounds(surface, frame) == start, "所在工作区没有空位时回到拖动起点");
			neighbor.ArrangeAt(500, 80, 200, 240);

			surface.Placement.Begin(frame, point, FenceEdges.Left | FenceEdges.Top);
			surface.Placement.Update(new(point.X + Px(20), point.Y + Px(20)));
			session.Gestures.Begin(point, false, surface);
			Require(!session.Gestures.IsActive, "调整分区时不能同时开始图标框选");
			session.CancelInteractions();
			Require(Bounds(surface, frame) == start && !session.IsInteracting, "取消操作恢复原始位置与尺寸");
			surface.Placement.Begin(frame, point, FenceEdges.Right);
			surface.Placement.Update(new(point.X + Px(20), point.Y));
			session.Takeover!.Release("分区布局隔离验证主动撤销接管。");
			Require(Bounds(surface, frame) == start && !session.IsInteracting, "接管撤销时取消分区布局修改");
			session.Takeover.Refresh();
			for (int attempt = 0; !session.Takeover.IsActive && attempt < 100; attempt++) { await Task.Delay(100); }
			Require(session.Takeover.IsActive, "布局取消后可以重新接管测试桌面");

			frame.SetRolledUp(true);
			await frame.RollReady;
			int height = frame.Frame.Active.Height;
			Require(frame.ResizeEdgesAt(new(frame.Width / 2, frame.VisibleHeight - 1)) == FenceEdges.None, "收起后只允许沿标题条的长边调整大小");
			surface.Placement.Begin(frame, point, FenceEdges.Right);
			surface.Placement.Update(new(point.X + Px(20), point.Y));
			await surface.Placement.EndAsync(false);
			Require(frame.Frame.Active.Height == height && frame.Frame.Members.All(model => model.Height == height), "调整收起条宽度不覆盖展开高度");
			Require(MyDesktop.Native.NativeMethods.GetForegroundWindow() == foreground && !surface.IsInputCaptured, "分区布局检查不改变前台窗口或扩展整屏输入区域");
		}
		finally
		{
			surface.Placement.Cancel();
			settings.SnapToEdges = options.SnapToEdges;
			settings.SnapToGrid = options.SnapToGrid;
			settings.SnapGap = options.SnapGap;
			frame.Frame.Active.Locked = false;
			foreach (var (item, original) in originals)
			{
				item.ArrangeAt((original.Bounds.Left - surface.WorkArea.Left) / scale, (original.Bounds.Top - surface.WorkArea.Top) / scale,
					original.Bounds.Width / scale, original.Bounds.Height / scale);
				item.SetRolledUp(original.RolledUp);
				item.CommitPosition();
			}
			await frame.ContentReady;
		}
	}

	static Rectangle Bounds(DesktopSurface surface, FenceView frame)
	{
		var bounds = surface.ExpandedBoundsOf(frame);
		return new Rectangle(bounds.Left + surface.WorkArea.Left, bounds.Top + surface.WorkArea.Top,
			(int)Math.Round(frame.Width * surface.Scale), (int)Math.Round(frame.Height * surface.Scale));
	}

	static void Require(bool condition, string message)
	{
		if (!condition) { throw new InvalidOperationException(message); }
		UiChecks.Write("通过：" + message);
	}
}
