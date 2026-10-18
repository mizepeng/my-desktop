using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.WinUI.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using DrawPoint = System.Drawing.Point;
using DrawRectangle = System.Drawing.Rectangle;
using FenceView = MyDesktop.WinUI.Views.FenceView;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Services;

internal sealed record FileDragPayload(string[] Paths, FileDropEffect Allowed, bool Desktop, string? Token = null,
	DrawPoint? Origin = null, IReadOnlyDictionary<string, DrawRectangle>? Cells = null);

internal sealed class FileDragDrop(DesktopSession session)
{
	const string Format = "MyDesktop.WinUI.FileDrag";
	FileDragPayload? _current;
	DataPackageView? _offered;
	Task<FileDragPayload>? _reading;
	public bool IsActive => _current != null;

	internal void ConnectSource(FrameworkElement source, Func<string> key, DesktopSurface surface, FenceView? fence)
	{
		source.CanDrag = !session.IsPreview;
		DrawPoint? press = null;
		source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
		{
			if (args.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
			{
				var point = GetCursorPos();
				press = new(point.X, point.Y);
			}
		}), true);
		source.DragStarting += (_, args) =>
		{
			try
			{
				Prepare(args.Data, surface, fence, key(), press ?? throw new InvalidOperationException("拖动缺少鼠标起点。"));
			}
			catch (Exception ex) { args.Cancel = true; Failed(ex); End(); }
		};
		source.DropCompleted += (_, _) => { press = null; End(); };
	}

	internal void ConnectList(FenceView fence, DesktopSurface surface)
	{
		fence.ItemList.CanDragItems = !session.IsPreview;
		fence.ItemList.DragItemsStarting += (_, args) =>
		{
			try
			{
				if (args.Items.FirstOrDefault() is not FenceItem item) { throw new InvalidOperationException("拖动项目已不在当前分区中。"); }
				var cursor = GetCursorPos();
				Prepare(args.Data, surface, fence, item.Path, new(cursor.X, cursor.Y));
			}
			catch (Exception ex) { args.Cancel = true; Failed(ex); End(); }
		};
		fence.ItemList.DragItemsCompleted += (_, _) => End();
	}

	internal FileDragPayload Prepare(DataPackage data, DesktopSurface surface, FenceView? fence, string anchor, DrawPoint origin)
	{
		if (session.IsPreview || session.Takeover?.IsActive != true || IsActive) { throw new InvalidOperationException("当前不能开始桌面拖放。"); }
		session.Gestures.Cancel();
		if (!session.Selection.Keys.Contains(anchor, StringComparer.OrdinalIgnoreCase))
		{
			if (fence == null) { session.SelectLoose(anchor, false, surface); }
			else { session.SelectFence(fence, [anchor], false); }
		}
		session.SetContext(surface, fence);
		var selected = session.SelectedFiles();
		var paths = FileOperationRules.ValidateSelection(selected, false);
		var allowed = FileDropEffect.Copy | (selected.All(item => item.CanDelete) ? FileDropEffect.Move : FileDropEffect.None);
		var cells = session.Surfaces.SelectMany(item => item.DesktopIcons.Cells).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
		var payload = new FileDragPayload(paths, allowed, paths.All(path => FileOperationRules.IsDesktopFile(path, session.DesktopFolders())),
			Guid.NewGuid().ToString("N"), fence == null && paths.All(cells.ContainsKey) ? origin : null, cells);
		_current = payload;
		session.UpdateInteraction();
		foreach (var host in session.Surfaces) { host.SetInputCapture(true); }
		data.SetData(Format, payload.Token);
		data.RequestedOperation = (DataPackageOperation)allowed;
		// GridView 的开始事件不提供异步延迟；文件数据由系统请求时按需生成，不阻塞输入线程。
		data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
		{
			var deferral = request.GetDeferral();
			try
			{
				var items = new List<IStorageItem>();
				foreach (string path in paths)
				{
					items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
				}
				// 取消后的异步请求可以晚到；不再提供旧数据，也不能借旧回调结束新的拖动。
				if (_current != payload) { return; }
				request.SetData(items);
			}
			catch (Exception ex)
			{
				surface.DispatcherQueue.TryEnqueue(() => { if (_current == payload) { Failed(ex); End(); } });
			}
			finally { deferral.Complete(); }
		});
		return payload;
	}

	internal void ConnectTarget(UIElement element, DesktopSurface surface, FenceView? fence)
	{
		element.AllowDrop = !session.IsPreview;
		element.DragEnter += Over;
		element.DragOver += Over;
		element.Drop += async (_, args) =>
		{
			args.Handled = true;
			args.AcceptedOperation = DataPackageOperation.None;
			var deferral = args.GetDeferral();
			var target = fence?.Frame.Active;
			try
			{
				var payload = await ResolveAsync(args.DataView);
				string? hover = fence == null ? surface.DesktopIcons.KeyAt(args.OriginalSource as DependencyObject) : FenceView.ItemKeyAt(args.OriginalSource as DependencyObject);
				var point = args.GetPosition(surface.RootElement);
				var destination = new DrawPoint(surface.WorkArea.Left + (int)Math.Round(point.X * surface.Scale), surface.WorkArea.Top + (int)Math.Round(point.Y * surface.Scale));
				args.AcceptedOperation = await DropAsync(payload, surface, target, destination, (FileDropEffect)args.AllowedOperations,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) != 0,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Alt) != 0, hover);
			}
			catch (Exception ex) { Failed(ex); }
			finally { End(); deferral.Complete(); }
		};

		async void Over(object sender, DragEventArgs args)
		{
			args.Handled = true;
			args.AcceptedOperation = DataPackageOperation.None;
			if (!args.DataView.Contains(StandardDataFormats.StorageItems) && !args.DataView.Contains(Format)) { return; }
			var deferral = args.GetDeferral();
			try
			{
				var payload = await ResolveAsync(args.DataView);
				string? hover = fence == null ? surface.DesktopIcons.KeyAt(args.OriginalSource as DependencyObject) : FenceView.ItemKeyAt(args.OriginalSource as DependencyObject);
				var effect = Effect(payload, fence?.Frame.Active, (FileDropEffect)args.AllowedOperations,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) != 0,
					(args.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Alt) != 0, hover);
				args.AcceptedOperation = (DataPackageOperation)effect;
				args.DragUIOverride.Caption = effect == FileDropEffect.None ? "请放到分区或桌面空白处"
					: (effect == FileDropEffect.Move ? "移动到 " : "复制到 ") + (fence?.Frame.Active.Title ?? "桌面");
				args.DragUIOverride.IsCaptionVisible = true;
			}
			catch (Exception ex) { Failed(ex); }
			finally { deferral.Complete(); }
		}
	}

	internal Task<FileDragPayload> ResolveAsync(DataPackageView data)
	{
		if (!Equals(_offered, data) || _reading == null)
		{
			_offered = data;
			_reading = ReadAsync(data);
		}
		return _reading;
	}

	async Task<FileDragPayload> ReadAsync(DataPackageView data)
	{
		if (data.Contains(Format))
		{
			var token = await data.GetDataAsync(Format) as string;
			return _current is { } current && current.Token == token ? current : throw new InvalidOperationException("拖放会话已失效，请重新拖动。");
		}
		if (!data.Contains(StandardDataFormats.StorageItems)) { throw new InvalidOperationException("拖动内容不是可读取的文件或文件夹。"); }
		var paths = (await data.GetStorageItemsAsync()).Select(item => item.Path).ToArray();
		if (paths.Length == 0 || paths.Any(path => !Path.IsPathFullyQualified(path))) { throw new InvalidOperationException("拖动项目没有可用的文件路径。"); }
		return new FileDragPayload(paths, FileDropEffect.Copy | FileDropEffect.Move,
			paths.All(path => FileOperationRules.IsDesktopFile(path, session.DesktopFolders())));
	}

	internal FileDropEffect Effect(FileDragPayload payload, FenceSettings? target, FileDropEffect allowed, bool control, bool shift, bool alt, string? hover = null)
	{
		if (session.IsPreview || session.Takeover?.IsActive != true || hover != null && !payload.Paths.Contains(hover, StringComparer.OrdinalIgnoreCase))
		{
			return FileDropEffect.None;
		}
		string folder = target?.IsPortal == true ? target.FolderPath : session.DesktopFolders()[0];
		return FileDropRules.Choose(payload.Paths, folder, payload.Desktop && target?.IsPortal != true, allowed & payload.Allowed, control, shift, alt);
	}

	internal async Task<DataPackageOperation> DropAsync(FileDragPayload payload, DesktopSurface surface, FenceSettings? target, DrawPoint position,
		FileDropEffect allowed, bool control = false, bool shift = false, bool alt = false, string? hover = null)
	{
		var effect = Effect(payload, target, allowed, control, shift, alt, hover);
		if (effect == FileDropEffect.None) { return DataPackageOperation.None; }
		var result = await session.Commands.DropAsync(surface.Handle, target, payload.Paths, effect == FileDropEffect.Move);
		if (result == null) { return DataPackageOperation.None; }
		if (target == null && result.Completed.Count > 0)
		{
			var paths = result.Completed.Select(item => item.Destination!).ToArray();
			DrawPoint? delta = payload.Origin is { } origin && result.Completed.All(item => PathUtil.AreEqual(item.Source, item.Destination))
				? new DrawPoint(position.X - origin.X, position.Y - origin.Y) : null;
			await session.Takeover!.PlaceAsync(paths, position, delta, payload.Cells ?? new Dictionary<string, DrawRectangle>());
		}
		// 移动由 Shell 已经完成，回报 None，防止外部拖放源再次删除源文件。
		return effect == FileDropEffect.Copy && !result.Aborted && result.Error == null ? DataPackageOperation.Copy : DataPackageOperation.None;
	}

	internal void End()
	{
		if (_current == null && _reading == null) { return; }
		_current = null;
		_offered = null;
		_reading = null;
		session.UpdateInteraction();
		foreach (var surface in session.Surfaces.Where(item => !item.IsClosed)) { surface.SetInputCapture(false); }
		session.FilesCompleted(false);
	}

	void Failed(Exception error)
	{
		Log.Error("拖放未完成", error);
		session.Notice(error.Message);
	}
}
