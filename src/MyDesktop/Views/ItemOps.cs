using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MyDesktop.Views;

/// <summary>
/// 本程序发起的拖动，只在拖动期间存在。放置目标据此识别拖来的是哪些项目，系统图标（没有文件路径）也能拖进分区。
/// </summary>
/// <param name="Keys">项目标识：桌面项目为完整解析名，映射分区的项目为文件路径。</param>
/// <param name="Files">其中真正的文件和文件夹，只有它们可以移动、复制（系统图标不行，用户文件夹的解析名虽是路径也不行）。</param>
/// <param name="FromDesktop">是否为桌面上的项目（散放图标或桌面分区中的图标）。</param>
/// <param name="Source">发起拖动的窗口。</param>
internal sealed record DesktopDrag(IReadOnlyList<string> Keys, IReadOnlyList<string> Files, bool FromDesktop, object Source)
{
	public static DesktopDrag? Current { get; private set; }

	/// <summary>
	/// 拖动开始和结束时在界面线程上触发，散放图标层据此在拖动期间接收放置。
	/// </summary>
	public static event Action? CurrentChanged;

	public static void Run(DesktopDrag drag, Action doDragDrop)
	{
		Current = drag;
		CurrentChanged?.Invoke();
		try
		{
			doDragDrop();
		}
		finally
		{
			Current = null;
			CurrentChanged?.Invoke();
		}
	}
}

/// <summary>
/// 驱动系统的拖放预览图（与资源管理器一样显示文件缩略图和"移动到 xx"提示）。
/// </summary>
internal sealed class DropPreview
{
	IDropTargetHelper? _helper;

	public void Enter(IntPtr hwnd, DragEventArgs e)
	{
		try
		{
			_helper ??= (IDropTargetHelper)new DragDropHelper();
			if (e.Data is ComIDataObject data)
			{
				var point = NativeMethods.GetCursorPos();
				_helper.DragEnter(hwnd, data, ref point, (int)e.Effects);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("初始化拖放预览失败", ex);
			_helper = null;
		}
	}

	public void Over(DragEventArgs e)
	{
		if (_helper == null)
		{
			return;
		}
		var point = NativeMethods.GetCursorPos();
		_helper.DragOver(ref point, (int)e.Effects);
	}

	public void Leave() => _helper?.DragLeave();

	public void Drop(DragEventArgs e, DragDropEffects effect)
	{
		if (_helper == null || e.Data is not ComIDataObject data)
		{
			return;
		}
		var point = NativeMethods.GetCursorPos();
		_helper.Drop(data, ref point, (int)effect);
	}
}

/// <summary>
/// 把拖动转给图标自己的放置目标：拖到文件夹里、拖给程序打开、拖进回收站删除，与资源管理器一致。
/// 图标不接受这次拖动时，由调用方按自己的规则处理（调整位置、归入分区等）。
/// </summary>
internal sealed class ItemDropForwarder
{
	FenceItem? _item;
	IOleDropTarget? _target;
	bool _accepts;

	/// <summary>
	/// 拖动经过 item（为 null 表示不在图标上）。图标接受时返回它给出的效果，否则返回 null。
	/// </summary>
	public DragDropEffects? Over(IntPtr hwnd, FenceItem? item, bool desktop, DragEventArgs e)
	{
		var point = NativeMethods.GetCursorPos();
		uint keys = (uint)e.KeyStates;
		uint effect = (uint)e.AllowedEffects;
		if (item != _item)
		{
			Leave();
			if (item == null || e.Data is not ComIDataObject data || CreateTarget(hwnd, item, desktop) is not IOleDropTarget target)
			{
				return null;
			}
			_item = item;
			_target = target;
			_accepts = Call(() => target.DragEnter(data, keys, point, ref effect)) && effect != 0;
		}
		else if (_target != null)
		{
			var target = _target;
			_accepts = Call(() => target.DragOver(keys, point, ref effect)) && effect != 0;
		}
		if (_item != null)
		{
			_item.IsDropTarget = _accepts;
		}
		return _accepts ? (DragDropEffects)effect : null;
	}

	public void Leave()
	{
		if (_target != null)
		{
			var target = _target;
			Call(target.DragLeave);
		}
		Release();
	}

	/// <summary>
	/// 松手：图标接受时由它完成放置并返回效果，否则返回 null。
	/// </summary>
	public DragDropEffects? Drop(DragEventArgs e)
	{
		if (_target == null || !_accepts || e.Data is not ComIDataObject data)
		{
			Leave();
			return null;
		}
		var target = _target;
		var point = NativeMethods.GetCursorPos();
		uint keys = (uint)e.KeyStates;
		uint effect = (uint)e.AllowedEffects;
		if (!Call(() => target.Drop(data, keys, point, ref effect)))
		{
			effect = 0;
		}
		Release();
		return (DragDropEffects)effect;
	}

	void Release()
	{
		if (_target != null)
		{
			Marshal.ReleaseComObject(_target);
			_target = null;
		}
		if (_item != null)
		{
			_item.IsDropTarget = false;
			_item = null;
		}
		_accepts = false;
	}

	static bool Call(Func<int> action)
	{
		try
		{
			return action() == 0;
		}
		catch (Exception ex)
		{
			Log.Warn("转发拖放到图标失败", ex);
			return false;
		}
	}

	static IOleDropTarget? CreateTarget(IntPtr hwnd, FenceItem item, bool desktop)
	{
		try
		{
			using var set = ItemOps.CreateItemSet([item], desktop);
			if (set == null || set.GetAttributes(SFGAO_DROPTARGET) == 0)
			{
				return null;
			}
			var ptr = set.GetUIObject(hwnd, typeof(IOleDropTarget).GUID);
			try
			{
				return (IOleDropTarget)Marshal.GetObjectForIUnknown(ptr);
			}
			finally
			{
				Marshal.Release(ptr);
			}
		}
		catch (Exception ex)
		{
			Log.Warn($"获取图标的放置目标失败：{item.FullPath}", ex);
			return null;
		}
	}
}

/// <summary>
/// 分区和散放图标层共用的项目操作：打开、删除、剪贴板、改名、右键菜单、拖出（含预览图）。
/// 系统图标（此电脑、回收站等）不对应文件：不能复制，改名、删除走它们自己的 Shell 命令（删除即从桌面移除图标）。
/// </summary>
internal static class ItemOps
{
	const int ErrorCancelled = 1223;

	// 多选拖动时数量徽标的底色，与资源管理器一致的系统蓝
	static readonly SolidColorBrush CountBadgeBrush = new(Color.FromRgb(0x00, 0x78, 0xD4));

	/// <summary>
	/// 为一组项目创建 Shell 项集合：桌面项目统一以桌面根为父（系统图标、用户桌面和公共桌面的文件可以混选），
	/// 其他项目按所在文件夹；没有可用项时返回 null。
	/// </summary>
	public static ShellItemSet? CreateItemSet(IReadOnlyList<FenceItem> items, bool desktop)
	{
		var keys = items.Select(i => i.FullPath).ToList();
		return desktop ? ShellItemSet.CreateDesktop(keys) : ShellItemSet.Create(keys);
	}

	public static void Open(IntPtr hwnd, IEnumerable<FenceItem> items, bool desktop)
	{
		foreach (var item in items)
		{
			if (item.IsVirtual)
			{
				OpenVirtual(hwnd, item, desktop);
				continue;
			}
			try
			{
				var startInfo = new ProcessStartInfo(item.FullPath)
				{
					UseShellExecute = true,
					WorkingDirectory = Path.GetDirectoryName(item.FullPath) ?? string.Empty,
				};
				Process.Start(startInfo)?.Dispose();
			}
			catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
			{
				// 用户在 UAC 等提示中取消
			}
			catch (Exception ex)
			{
				Log.Warn($"打开失败：{item.FullPath}", ex);
				MessageDialog.Show("打开失败", $"无法打开「{item.DisplayName}」：\n{ex.Message}", "确定");
			}
		}
	}

	static void OpenVirtual(IntPtr hwnd, FenceItem item, bool desktop)
	{
		try
		{
			using var set = CreateItemSet([item], desktop);
			if (set != null)
			{
				ShellContextMenu.InvokeDefault(hwnd, set, NativeMethods.GetCursorPos());
			}
		}
		catch (Exception ex)
		{
			Log.Warn($"打开失败：{item.FullPath}", ex);
		}
	}

	public static void ShowContextMenu(IntPtr hwnd, IReadOnlyList<FenceItem> items, bool desktop, POINT point, IReadOnlyList<(string Text, Action Action)> extras,
			Func<string, bool> handleVerb)
	{
		using var set = CreateItemSet(items, desktop);
		if (set != null)
		{
			ShellContextMenu.Show(hwnd, set, point, extras, handleVerb);
		}
	}

	/// <summary>
	/// 打开属性对话框（Alt+Enter）。
	/// </summary>
	public static void ShowProperties(IntPtr hwnd, IReadOnlyList<FenceItem> items, bool desktop)
	{
		try
		{
			using var set = CreateItemSet(items, desktop);
			if (set != null)
			{
				ShellContextMenu.InvokeVerb(hwnd, set, "properties");
			}
		}
		catch (Exception ex)
		{
			Log.Warn("打开属性失败", ex);
		}
	}

	public static void Delete(IntPtr hwnd, IEnumerable<FenceItem> items, bool permanent)
	{
		var list = items.ToList();
		var paths = list.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToList();
		if (paths.Count > 0)
		{
			ShellFileOps.Delete(hwnd, paths, !permanent);
		}
		// 此电脑、控制面板这类系统图标交给它们自己的「删除」命令，效果是从桌面移除图标，与在资源管理器里删除一致。
		// 用户文件夹等解析名是真实路径的系统图标不经键盘删除（可以用右键菜单），以免误伤整个文件夹
		var shellItems = list.Where(i => i.IsVirtual && i.CanDelete && i.FullPath.StartsWith("::", StringComparison.Ordinal)).ToList();
		if (shellItems.Count == 0)
		{
			return;
		}
		try
		{
			using var set = CreateItemSet(shellItems, true);
			if (set != null)
			{
				ShellContextMenu.InvokeVerb(hwnd, set, "delete");
			}
		}
		catch (Exception ex)
		{
			Log.Warn("删除系统图标失败", ex);
		}
	}

	/// <summary>
	/// 剪贴板里被剪切（而不是复制）的文件，用于把它们显示成半透明；不是剪切时返回空集合。
	/// </summary>
	public static HashSet<string> GetCutPaths()
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (Clipboard.ContainsFileDropList()
					&& Clipboard.GetData("Preferred DropEffect") is MemoryStream stream
					&& stream.Length >= 4
					&& (BitConverter.ToInt32(stream.ToArray(), 0) & 2) != 0)
			{
				foreach (var file in Clipboard.GetFileDropList())
				{
					if (file != null)
					{
						result.Add(file);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("读取剪贴板失败", ex);
		}
		return result;
	}

	public static void CopyToClipboard(IEnumerable<FenceItem> items, bool cut)
	{
		var paths = items.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToArray();
		if (paths.Length == 0)
		{
			return;
		}
		try
		{
			var files = new StringCollection();
			files.AddRange(paths);
			var data = new DataObject();
			data.SetFileDropList(files);
			// 资源管理器的约定：剪切为 DROPEFFECT_MOVE(2)，复制为 COPY|LINK(5)
			data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
			Clipboard.SetDataObject(data, true);
		}
		catch (Exception ex)
		{
			Log.Warn("写入剪贴板失败", ex);
		}
	}

	/// <summary>
	/// 把剪贴板中的文件粘贴到文件夹，返回粘贴后预期出现的路径（重名改名时可能不准）。
	/// </summary>
	public static List<string> Paste(IntPtr hwnd, string folder)
	{
		try
		{
			if (!Clipboard.ContainsFileDropList())
			{
				return [];
			}
			var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
			bool cut = Clipboard.GetData("Preferred DropEffect") is MemoryStream stream
					&& stream.Length >= 4
					&& (BitConverter.ToInt32(stream.ToArray(), 0) & 2) != 0;
			if (!cut)
			{
				ShellFileOps.Copy(hwnd, files, folder);
			}
			else if (ShellFileOps.Move(hwnd, files, folder))
			{
				Clipboard.Clear();
			}
			return files.Select(f => Path.Combine(folder, Path.GetFileName(f.TrimEnd('\\')))).ToList();
		}
		catch (Exception ex)
		{
			Log.Warn("粘贴失败", ex);
			return [];
		}
	}

	public static bool ClipboardHasFiles()
	{
		try
		{
			return Clipboard.ContainsFileDropList();
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// 按用户输入的新显示名改名，返回新的完整路径；没有改动、名称不合法或改名失败时返回 null。
	/// 系统图标（如此电脑）用 Shell 改显示名，完整解析名不变。
	/// </summary>
	public static string? Rename(IntPtr hwnd, FenceItem item, string input)
	{
		var newName = input.Trim();
		if (!item.CanRename || newName.Length == 0 || newName == item.DisplayName)
		{
			return null;
		}
		if (item.IsVirtual)
		{
			try
			{
				using var set = CreateItemSet([item], true);
				return set?.SetName(hwnd, newName) == true ? item.FullPath : null;
			}
			catch (Exception ex)
			{
				Log.Warn($"重命名系统图标失败：{item.FullPath}", ex);
				return null;
			}
		}
		var target = newName;
		var extension = Path.GetExtension(item.FileName);
		// 显示名省略了扩展名（快捷方式，或系统设置为隐藏已知扩展名）时保留原扩展名
		if (!item.IsFolder && extension.Length > 0 && !item.DisplayName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
		{
			target += extension;
		}
		if (target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			MessageDialog.Show("重命名", "文件名不能包含下列任何字符：\n\\ / : * ? \" < > |", "确定");
			return null;
		}
		var directory = Path.GetDirectoryName(item.FullPath);
		if (directory == null)
		{
			return null;
		}
		var path = Path.Combine(directory, target);
		return ShellFileOps.Rename(hwnd, item.FullPath, path) ? path : null;
	}

	/// <summary>
	/// 拖入时的动作，与资源管理器一致：默认按 preferMove 移动或复制；Ctrl 复制、Shift 移动、Alt（或 Ctrl+Shift）创建快捷方式。
	/// </summary>
	public static DragDropEffects ChooseDropEffect(DragEventArgs e, bool preferMove)
	{
		var keys = e.KeyStates;
		bool ctrl = keys.HasFlag(DragDropKeyStates.ControlKey);
		bool shift = keys.HasFlag(DragDropKeyStates.ShiftKey);
		bool alt = keys.HasFlag(DragDropKeyStates.AltKey);
		DragDropEffects wanted;
		if (alt || (ctrl && shift))
		{
			wanted = DragDropEffects.Link;
		}
		else if (ctrl)
		{
			wanted = DragDropEffects.Copy;
		}
		else if (shift)
		{
			wanted = DragDropEffects.Move;
		}
		else
		{
			wanted = preferMove ? DragDropEffects.Move : DragDropEffects.Copy;
		}
		if ((e.AllowedEffects & wanted) != 0)
		{
			return wanted;
		}
		foreach (var fallback in new[] { DragDropEffects.Move, DragDropEffects.Copy, DragDropEffects.Link })
		{
			if ((e.AllowedEffects & fallback) != 0)
			{
				return fallback;
			}
		}
		return DragDropEffects.None;
	}

	/// <summary>
	/// 把文件移动、复制到文件夹，或在其中创建快捷方式；返回操作后预期出现的路径。
	/// </summary>
	public static List<string> Transfer(IntPtr hwnd, IReadOnlyList<string> paths, string folder, DragDropEffects effect)
	{
		try
		{
			if (effect == DragDropEffects.Link)
			{
				return ShellFileOps.CreateShortcuts(paths, folder);
			}
			if (effect == DragDropEffects.Move)
			{
				ShellFileOps.Move(hwnd, paths, folder);
			}
			else if (effect == DragDropEffects.Copy)
			{
				ShellFileOps.Copy(hwnd, paths, folder);
			}
			else
			{
				return [];
			}
			return paths.Select(p => Path.Combine(folder, Path.GetFileName(p.TrimEnd('\\')))).ToList();
		}
		catch (Exception ex)
		{
			Log.Error("拖放文件失败", ex);
			MessageDialog.Show("拖放失败", ex.Message, "确定");
			return [];
		}
	}

	/// <summary>
	/// 以 Shell 数据对象拖出项目，拖动期间登记为本程序的拖动。
	/// </summary>
	public static void DragOut(IntPtr hwnd, IReadOnlyList<FenceItem> items, bool desktop, object source, SHDRAGIMAGE? image)
	{
		var bitmap = image?.hbmpDragImage ?? IntPtr.Zero;
		try
		{
			using var set = CreateItemSet(items, desktop);
			if (set == null)
			{
				return;
			}
			// 位图交给拖放后由系统释放
			bitmap = IntPtr.Zero;
			var drag = new DesktopDrag(items.Select(i => i.FullPath).ToList(), items.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToList(), desktop, source);
			DesktopDrag.Run(drag, () => ShellFileOps.DoDragDrop(hwnd, set, image));
		}
		catch (Exception ex)
		{
			Log.Warn("拖出文件失败", ex);
		}
		finally
		{
			if (bitmap != IntPtr.Zero)
			{
				DeleteObject(bitmap);
			}
		}
	}

	/// <summary>
	/// 按项目在界面上的实际样子（图标 + 名称，不含选中底色）生成拖动预览图，与原图标重合地跟随鼠标；
	/// 多选时在图标右上角标出数量。生成失败返回 null，由系统生成默认预览图。
	/// </summary>
	public static SHDRAGIMAGE? CreateDragImage(ListBoxItem? container, int count, double scale)
	{
		if (container == null
				|| FindDescendant<ContentPresenter>(container) is not ContentPresenter content
				|| content.ActualWidth <= 0
				|| content.ActualHeight <= 0)
		{
			return null;
		}
		var size = new Size(content.ActualWidth, content.ActualHeight);
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			var brush = new VisualBrush(content)
			{
				Viewbox = new Rect(size),
				ViewboxUnits = BrushMappingMode.Absolute,
			};
			context.DrawRectangle(brush, null, new Rect(size));
			if (count > 1 && FindDescendant<Image>(content) is Image icon)
			{
				DrawCountBadge(context, icon.TransformToAncestor(content).TransformBounds(new Rect(icon.RenderSize)), size, count, scale);
			}
		}
		var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
		image.Render(visual);
		var bitmap = CreateDragBitmap(image);
		if (bitmap == IntPtr.Zero)
		{
			return null;
		}
		var origin = content.PointToScreen(new Point());
		var cursor = NativeMethods.GetCursorPos();
		return new SHDRAGIMAGE
		{
			sizeDragImage = new SIZE(image.PixelWidth, image.PixelHeight),
			ptOffset = new POINT(Math.Clamp(cursor.X - (int)Math.Round(origin.X), 0, image.PixelWidth), Math.Clamp(cursor.Y - (int)Math.Round(origin.Y), 0, image.PixelHeight)),
			hbmpDragImage = bitmap,
			// 带 alpha 通道的位图不需要透明色
			crColorKey = 0xFFFFFFFF,
		};
	}

	static void DrawCountBadge(DrawingContext context, Rect icon, Size bounds, int count, double scale)
	{
		var text = new FormattedText(count.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11, Brushes.White, scale);
		double height = 18;
		double width = Math.Max(height, text.Width + 10);
		// 徽标中心落在图标右上角，同时不超出预览图范围
		double x = Math.Clamp(icon.Right - width / 2, 0, Math.Max(0, bounds.Width - width));
		double y = Math.Clamp(icon.Top - height / 2, 0, Math.Max(0, bounds.Height - height));
		context.DrawRoundedRectangle(CountBadgeBrush, null, new Rect(x, y, width, height), height / 2, height / 2);
		context.DrawText(text, new Point(x + (width - text.Width) / 2, y + (height - text.Height) / 2));
	}

	/// <summary>
	/// 转成 32 位自上而下、预乘 alpha 的 DIB，这是拖放预览图要求的格式。
	/// </summary>
	static IntPtr CreateDragBitmap(BitmapSource source)
	{
		int width = source.PixelWidth;
		int height = source.PixelHeight;
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
		if (bitmap != IntPtr.Zero)
		{
			source.CopyPixels(Int32Rect.Empty, bits, width * height * 4, width * 4);
		}
		return bitmap;
	}

	public static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if (child is T match)
			{
				return match;
			}
			var nested = FindDescendant<T>(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	public static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if (child is T match && match.Name == name)
			{
				return match;
			}
			var nested = FindChild<T>(child, name);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	public static bool IsInside<T>(object source) where T : DependencyObject
	{
		var current = source as DependencyObject;
		while (current != null)
		{
			if (current is T)
			{
				return true;
			}
			current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
		}
		return false;
	}
}
