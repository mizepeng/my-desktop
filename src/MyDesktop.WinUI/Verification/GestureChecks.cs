using System.Drawing;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Native;
using MyDesktop.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class GestureChecks
{
	public static async Task RunAsync(DesktopSession session)
	{
		Require(!DesktopInput.IsInputDesktop(), "框选与拖放仅在独立测试桌面验证");
		var surface = session.Surfaces.First(item => item.DesktopIcons.Keys.Count > 0);
		var fence = surface.Fences.First(item => item.Frame.Active.IsPortal);
		var portal = fence.Frame.Active;
		var desktop = fence.Frame.Members.First(item => !item.IsPortal);
		var takeover = session.Takeover!;
		var foreground = GetForegroundWindow();
		string fixture = Path.Combine(AppPaths.DataDir, "desktop-snapshot.json");
		string original = File.ReadAllText(fixture);
		var originalSnapshot = JsonSerializer.Deserialize<DesktopSnapshot>(original)!;
		var loose = surface.DesktopIcons.Cells.First();
		string portalItem = fence.DisplayedPaths.First(File.Exists);
		var start = new Point(loose.Value.Right + 12, loose.Value.Top + 10);
		var end = new Point(loose.Value.Left + 3, loose.Value.Bottom - 3);
		try
		{
			fence.UpdateLayout();
			Require(UiChecks.Descendants(surface.DesktopIcons).OfType<Button>().Where(item => item.Tag is string).All(item => item.CanDrag)
				&& fence.ItemList.CanDragItems, "散放按钮与分区 GridView 均已接入 WinUI 拖动源");
			session.SelectFence(fence, [portalItem], false);
			session.SelectLoose(loose.Key, true, surface);
			var before = session.Selection.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
			session.Gestures.Begin(start, false, surface);
			Require(session.Selection.Keys.Count == 0 && !surface.IsInputCaptured, "空白按下先清选择，未越过阈值前不扩展宿主输入区域");
			session.Gestures.Move(end);
			Require(session.Selection.Keys.SequenceEqual(new[] { loose.Key }) && surface.IsInputCaptured && surface.IsBandVisible,
				"反向框选同步散放选择，并在已有宿主内显示选框");
			Require(GetAncestor(WindowFromPoint(new MyDesktop.Native.POINT(start.X, start.Y)), 2) == surface.Handle, "框选期间空白可接收松开事件");
			session.Gestures.Cancel();
			Require(before.SetEquals(session.Selection.Keys) && !surface.IsInputCaptured && !surface.IsBandVisible, "取消框选恢复原选择并释放完整输入区域");
			Require(GetAncestor(WindowFromPoint(new MyDesktop.Native.POINT(start.X, start.Y)), 2) == takeover.Target!.Root, "取消后桌面空白重新穿透给测试桌面");
			session.Gestures.Begin(start, true, surface);
			session.Gestures.Move(end);
			Require(before.SetEquals(session.Selection.Keys), "追加框选保留其他分区的原选择");
			session.Gestures.End(end);
			await fence.ContentReady;
			fence.UpdateLayout();
			var hit = surface.SelectionTargets(fence).First(item => item.Key == portalItem);
			session.Gestures.Begin(new(hit.Bounds.Left + 2, hit.Bounds.Top + 2), false, surface, fence);
			session.Gestures.Move(new(hit.Bounds.Right - 2, hit.Bounds.Bottom - 2));
			Require(session.Selection.Keys.Contains(portalItem) && !surface.IsInputCaptured && surface.IsBandVisible, "分区框选只命中内容区，保持分区之外穿透");
			session.HandleKey(DesktopInputCommand.Delete, 0x2E, false);
			Require(File.Exists(portalItem), "框选期间不会执行落在隐藏 Explorer 上的删除命令");
			session.Gestures.Cancel();

			session.Gestures.Begin(start, false, surface);
			session.Gestures.Move(end);
			takeover.Release("隔离验证主动撤销接管。");
			Require(!session.Gestures.IsActive && !surface.IsInputCaptured && !surface.IsBandVisible, "接管撤销时取消框选并释放输入区域");
			takeover.Refresh();
			await WaitAsync(() => takeover.IsActive, "框选取消后可重新接管测试桌面");

			// 数据包检查不进入系统拖放循环，也不读写共享剪贴板。
			var data = new DataPackage();
			var payload = session.DragDrop.Prepare(data, surface, null, loose.Key, start);
			var view = data.GetView();
			var storage = await Task.Run(async () => await view.GetStorageItemsAsync());
			Require(surface.IsInputCaptured && data.GetView().Contains(StandardDataFormats.StorageItems)
				&& storage.Single().Path == loose.Key, "后台请求可取得按需生成的真实 StorageItem，拖动期间扩展桌面放置区域");
			Require(ReferenceEquals(await session.DragDrop.ResolveAsync(data.GetView()), payload), "内部拖动按会话标识识别原始选择");
			session.DragDrop.End();
			Require(!session.DragDrop.IsActive && !surface.IsInputCaptured, "取消拖动释放原有宿主的放置区域");
			await RejectAsync(() => session.DragDrop.ResolveAsync(data.GetView()), "结束后的拖动数据包不能重放");
			session.DragDrop.End();
			var canceledData = new DataPackage();
			session.DragDrop.Prepare(canceledData, surface, null, loose.Key, start);
			session.DragDrop.End();
			session.DragDrop.Prepare(new DataPackage(), surface, null, loose.Key, start);
			try { await canceledData.GetView().GetStorageItemsAsync(); }
			// 已取消的数据包允许系统报告无可用数据，不能影响当前的新会话。
			catch (System.Runtime.InteropServices.COMException) { }
			catch (InvalidOperationException) { }
			await Task.Delay(30);
			Require(session.DragDrop.IsActive && surface.IsInputCaptured, "旧数据请求晚到不会取消随后开始的新拖动");
			session.DragDrop.End();
			await fence.ContentReady;
			string folder = fence.DisplayedPaths.First(Directory.Exists);
			session.SelectFence(fence, [portalItem, folder], false);
			session.SelectLoose(loose.Key, true, surface);
			data = new DataPackage();
			payload = session.DragDrop.Prepare(data, surface, null, loose.Key, start);
			storage = await data.GetView().GetStorageItemsAsync();
			Require(payload.Paths.Length == 3 && storage.Count == 3 && storage.Any(item => item is StorageFolder),
				"跨区域多选拖动携带全部文件和文件夹，不丢失分区选择");
			session.DragDrop.End();

			fence.ActivateTab(desktop.Id);
			await fence.ContentReady;
			session.SelectLoose(loose.Key, false, surface);
			session.Commands.Clipboard.Write(surface.Handle, [loose.Key], false);
			uint sequence = session.Commands.Clipboard.Read(surface.Handle)!.Sequence;
			data = new DataPackage();
			payload = session.DragDrop.Prepare(data, surface, null, loose.Key, start);
			await session.DragDrop.DropAsync(payload, surface, desktop, start, FileDropEffect.Copy | FileDropEffect.Move, control: true);
			Require(desktop.Members.Contains(loose.Key) && File.Exists(loose.Key), "桌面拖入分区仅改归属，Ctrl 不复制另一份桌面文件");
			session.DragDrop.End();
			await WaitAsync(() => !surface.DesktopIcons.Keys.Contains(loose.Key) && fence.DisplayedPaths.Contains(loose.Key), "拖入后分区与散放视图完成换手");
			session.SelectFence(fence, [loose.Key], false);
			data = new DataPackage();
			payload = session.DragDrop.Prepare(data, surface, fence, loose.Key, start);
			var dropPoint = new Point(loose.Value.Left + loose.Value.Width / 2, loose.Value.Top + loose.Value.Height / 4);
			await session.DragDrop.DropAsync(payload, surface, null, dropPoint, FileDropEffect.Move);
			Require(!desktop.Members.Contains(loose.Key) && File.Exists(loose.Key), "拖回桌面只移除归属，目标已完成移动时源端不重复删除");
			session.DragDrop.End();
			await WaitAsync(() => surface.DesktopIcons.Keys.Contains(loose.Key), "拖回后恢复散放图标并读取新位置");
			var saved = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(fixture))!;
			Require(saved.ByKey[loose.Key].Position == new Point(loose.Value.Left - saved.ListBounds.Left, loose.Value.Top - saved.ListBounds.Top),
				"松手位置已写入隔离 Explorer 快照，保留自动排列坐标语义");

			// 散放多选拖动按绝对起点平移；测试完恢复夹具，后续检查继续使用原位置。
			data = new DataPackage();
			payload = session.DragDrop.Prepare(data, surface, null, loose.Key, start);
			var previous = payload.Cells![loose.Key];
			await session.DragDrop.DropAsync(payload, surface, null, new(start.X + 25, start.Y + 35), FileDropEffect.Move);
			session.DragDrop.End();
			saved = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(fixture))!;
			Require(saved.ByKey[loose.Key].Position == new Point(previous.Left + 25 - saved.ListBounds.Left, previous.Top + 35 - saved.ListBounds.Top),
				"散放拖动按鼠标相对起点的位移写回，不累计上次结果");

			fence.ActivateTab(portal.Id);
			await fence.ContentReady;
			string external = Path.Combine(portal.FolderPath, "拖放验证.txt");
			File.WriteAllText(external, "拖放文件内容");
			var externalData = new DataPackage();
			externalData.SetStorageItems([await StorageFile.GetFileFromPathAsync(external)]);
			payload = await session.DragDrop.ResolveAsync(externalData.GetView());
			var result = await session.DragDrop.DropAsync(payload, surface, desktop, start, FileDropEffect.Copy | FileDropEffect.Move, control: true);
			var copied = session.Commands.LastResult!.Completed.Single().Destination!;
			Require(result == DataPackageOperation.Copy && File.ReadAllText(copied) == "拖放文件内容" && File.Exists(external)
				&& desktop.Members.Contains(copied), "外部 StorageItems 拖入桌面分区按实际复制结果归属，保留源文件");
			Require(session.Commands.Clipboard.Read(surface.Handle)?.Sequence == sequence, "拖放不改动原有复制或剪切列表");
			Require(session.DragDrop.Effect(payload, portal, FileDropEffect.Copy | FileDropEffect.Move, false, false, false, "其他图标") == FileDropEffect.None,
				"尚未迁移的图标放置目标不会被误当成分区空白");
			session.DragDrop.End();
			desktop.Members.Remove(copied);
			File.Delete(copied);
			File.Delete(external);
			Require(GetForegroundWindow() == foreground, "框选、拖放和位置验证没有改变实际输入桌面的前台窗口");
		}
		finally
		{
			session.CancelInteractions();
			File.WriteAllText(fixture + ".tmp", original);
			File.Move(fixture + ".tmp", fixture, true);
			fence.ActivateTab(portal.Id);
			session.FilesCompleted(true);
		}
		await WaitAsync(() => takeover.IsActive && takeover.Snapshot?.Signature == originalSnapshot.Signature && surface.DesktopIcons.Keys.Count == 2,
			"恢复测试快照后接管和全部输入区域保持正常");
	}

	static async Task RejectAsync(Func<Task<FileDragPayload>> action, string message)
	{
		try { await action(); }
		catch (InvalidOperationException) { UiChecks.Write("通过：" + message); return; }
		throw new InvalidOperationException(message);
	}

	static async Task WaitAsync(Func<bool> condition, string message)
	{
		for (int attempt = 0; !condition(); attempt++)
		{
			if (attempt >= 200) { throw new TimeoutException(message); }
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
