using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 显示与资源管理器一致的系统文件右键菜单（打开方式、发送到、属性以及第三方扩展项）。
/// </summary>
internal static class ShellContextMenu
{
	const uint CMF_NORMAL = 0x0000;
	const uint CMF_DEFAULTONLY = 0x0001;
	const uint CMF_CANRENAME = 0x0010;
	const uint CMF_EXTENDEDVERBS = 0x0100;
	const uint GCS_VERBW = 0x0004;
	const uint CMIC_MASK_UNICODE = 0x00004000;
	const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
	const uint CMIC_MASK_PTINVOKE = 0x20000000;
	const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
	const int FirstShellId = 1;
	const int LastShellId = 0x7FFF;
	const int FirstExtraId = 0x8000;

	static IContextMenu2? _active2;
	static IContextMenu3? _active3;

	/// <summary>
	/// 菜单显示期间，宿主窗口需把绘制/子菜单初始化消息转交给 Shell，"发送到""打开方式"等动态子菜单才能正常展开。
	/// </summary>
	public static bool TryHandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
	{
		result = IntPtr.Zero;
		if (msg != WM_INITMENUPOPUP && msg != WM_DRAWITEM && msg != WM_MEASUREITEM && msg != WM_MENUCHAR)
		{
			return false;
		}
		try
		{
			if (_active3 != null)
			{
				return _active3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
			}
			if (_active2 != null)
			{
				return _active2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
			}
		}
		catch (Exception ex)
		{
			Log.Warn("转发菜单消息失败", ex);
		}
		return false;
	}

	/// <summary>
	/// 显示系统右键菜单。
	/// </summary>
	/// <param name="hwnd">菜单宿主窗口，需在其窗口过程中调用 <see cref="TryHandleMenuMessage"/>。</param>
	/// <param name="items">要弹出菜单的一组项目。</param>
	/// <param name="point">屏幕坐标（物理像素）。</param>
	/// <param name="extraItems">自定义项，插在「属性」前面（「属性」留在最后，和资源管理器一样）；没有「属性」时加在末尾。</param>
	/// <param name="handleVerb">拦截指定动词（如 rename），返回 true 表示已自行处理。</param>
	public static void Show(IntPtr hwnd, ShellItemSet items, POINT point, IReadOnlyList<ExtraMenuItem> extraItems, Func<string, bool> handleVerb)
	{
		Show(hwnd, items.GetUIObject(hwnd, typeof(IContextMenu).GUID), CMF_CANRENAME, null, point, extraItems, handleVerb);
	}

	/// <summary>
	/// 显示资源管理器在文件夹空白处右键时的系统菜单（新建、粘贴、撤销、属性以及第三方扩展项），命令作用于这个文件夹。
	/// </summary>
	/// <param name="hwnd">菜单宿主窗口，需在其窗口过程中调用 <see cref="TryHandleMenuMessage"/>。</param>
	/// <param name="folder">文件夹路径。</param>
	/// <param name="point">屏幕坐标（物理像素）。</param>
	/// <param name="extraItems">自定义项，插在「属性」前面。</param>
	public static void ShowFolderBackground(IntPtr hwnd, string folder, POINT point, IReadOnlyList<ExtraMenuItem> extraItems)
	{
		var menuPtr = GetFolderBackgroundMenu(hwnd, folder);
		// 资源管理器的背景菜单以文件夹视图为站点，「授予访问权限」、PowerToys 的「新建+」等扩展从站点取当前文件夹，
		// 没有站点时子菜单是空的、展不开（实测）。菜单用完前撤掉站点，免得扩展一直引用着它
		var withSite = (IObjectWithSite)Marshal.GetObjectForIUnknown(menuPtr);
		try
		{
			Marshal.ThrowExceptionForHR(withSite.SetSite(new FolderViewSite(folder)));
			Show(hwnd, menuPtr, CMF_NORMAL, folder, point, extraItems, _ => false);
		}
		finally
		{
			withSite.SetSite(null);
			Marshal.ReleaseComObject(withSite);
		}
	}

	/// <summary>
	/// 取文件夹背景的 IContextMenu：和资源管理器一样，由文件夹自己的 IShellFolder.CreateViewObject 创建。
	/// </summary>
	static IntPtr GetFolderBackgroundMenu(IntPtr hwnd, string folder)
	{
		Marshal.ThrowExceptionForHR(SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _));
		try
		{
			Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var desktopPtr));
			var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
			try
			{
				var folderIid = typeof(IShellFolder).GUID;
				Marshal.ThrowExceptionForHR(desktop.BindToObject(pidl, IntPtr.Zero, ref folderIid, out var folderPtr));
				var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
				try
				{
					var menuIid = typeof(IContextMenu).GUID;
					Marshal.ThrowExceptionForHR(shellFolder.CreateViewObject(hwnd, ref menuIid, out var menuPtr));
					return menuPtr;
				}
				finally
				{
					Marshal.ReleaseComObject(shellFolder);
					Marshal.Release(folderPtr);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(desktop);
				Marshal.Release(desktopPtr);
			}
		}
		finally
		{
			ILFree(pidl);
		}
	}

	/// <param name="menuPtr">系统菜单的 IContextMenu 指针，用完后由本方法释放。</param>
	/// <param name="baseFlags">QueryContextMenu 的标志，按住 Shift 时再加上扩展命令。</param>
	/// <param name="directory">执行命令时传给 Shell 的工作目录，见 <see cref="Invoke"/>。</param>
	static void Show(IntPtr hwnd, IntPtr menuPtr, uint baseFlags, string? directory, POINT point, IReadOnlyList<ExtraMenuItem> extraItems, Func<string, bool> handleVerb)
	{
		var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
		var menu = CreatePopupMenu();
		var actions = new List<Action?>();
		var bitmaps = new List<IntPtr>();
		try
		{
			bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
			uint flags = baseFlags | (shift ? CMF_EXTENDEDVERBS : 0);
			Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(menu, 0, FirstShellId, LastShellId, flags));
			if (extraItems.Count > 0)
			{
				int properties = FindProperties(contextMenu, menu);
				if (properties < 0)
				{
					InsertMenu(menu, uint.MaxValue, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
					int position = GetMenuItemCount(menu);
					AddExtras(menu, extraItems, actions, bitmaps, ref position);
				}
				else
				{
					// 「属性」上面一般已经有一条分隔线，自定义项插在分隔线和「属性」之间，后面再补一条，「属性」仍在最后
					int position = properties;
					if (position > 0 && (GetMenuState(menu, (uint)position - 1, MF_BYPOSITION) & MF_SEPARATOR) == 0)
					{
						InsertMenu(menu, (uint)position++, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
					}
					AddExtras(menu, extraItems, actions, bitmaps, ref position);
					InsertMenu(menu, (uint)position, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
				}
			}

			int command;
			_active2 = contextMenu as IContextMenu2;
			_active3 = contextMenu as IContextMenu3;
			try
			{
				// 宿主不在前台时，点击菜单外部菜单不会消失（用键盘在桌面上呼出菜单时前台还是资源管理器）
				ForceForegroundWindow(hwnd);
				command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, hwnd, IntPtr.Zero);
			}
			finally
			{
				_active2 = null;
				_active3 = null;
			}
			if (command >= FirstExtraId)
			{
				int index = command - FirstExtraId;
				if (index < actions.Count)
				{
					actions[index]?.Invoke();
				}
			}
			else if (command >= FirstShellId)
			{
				uint offset = (uint)(command - FirstShellId);
				if (!handleVerb(GetVerb(contextMenu, offset)))
				{
					Invoke(contextMenu, hwnd, offset, point, directory);
				}
			}
		}
		finally
		{
			// DestroyMenu 会递归销毁挂在其上的子菜单
			DestroyMenu(menu);
			bitmaps.ForEach(b => DeleteObject(b));
			Marshal.ReleaseComObject(contextMenu);
			Marshal.Release(menuPtr);
		}
	}

	/// <summary>
	/// 系统菜单里「属性」（动词 properties）的位置，没有时返回 -1。从下往上找：它一般就是最后一项，不用挨个问其他扩展的动词。
	/// </summary>
	static int FindProperties(IContextMenu contextMenu, IntPtr menu)
	{
		for (int i = GetMenuItemCount(menu) - 1; i >= 0; i--)
		{
			uint id = GetMenuItemID(menu, i);
			if (id >= FirstShellId && id <= LastShellId && string.Equals(GetVerb(contextMenu, id - FirstShellId), "properties", StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>
	/// 把自定义项从 position 起依次插入 menu，子菜单递归加；菜单项的编号从 FirstExtraId 起按 actions 里的位置排。
	/// </summary>
	static void AddExtras(IntPtr menu, IReadOnlyList<ExtraMenuItem> items, List<Action?> actions, List<IntPtr> bitmaps, ref int position)
	{
		foreach (var item in items)
		{
			if (ReferenceEquals(item, ExtraMenuItem.Separator))
			{
				InsertMenu(menu, (uint)position++, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
				continue;
			}
			var bitmap = item.Swatch is { } color ? NativeMenu.CreateSwatch(color) : IntPtr.Zero;
			if (bitmap != IntPtr.Zero)
			{
				bitmaps.Add(bitmap);
			}
			var info = new MENUITEMINFO
			{
				cbSize = Marshal.SizeOf<MENUITEMINFO>(),
				fMask = MIIM_STRING | MIIM_FTYPE | MIIM_STATE | (bitmap != IntPtr.Zero ? MIIM_BITMAP : 0),
				fType = item.Radio ? MFT_RADIOCHECK : 0,
				fState = (item.Checked ? MFS_CHECKED : 0) | (item.Enabled ? 0 : MFS_DISABLED),
				dwTypeData = NativeMenu.Escape(item.Text),
				hbmpItem = bitmap,
			};
			if (item.Children != null)
			{
				var sub = CreatePopupMenu();
				int subPosition = 0;
				AddExtras(sub, item.Children, actions, bitmaps, ref subPosition);
				info.fMask |= MIIM_SUBMENU;
				info.hSubMenu = sub;
			}
			else
			{
				actions.Add(item.Action);
				info.fMask |= MIIM_ID;
				info.wID = (uint)(FirstExtraId + actions.Count - 1);
			}
			InsertMenuItem(menu, (uint)position++, true, ref info);
		}
	}

	/// <summary>
	/// 执行默认命令，相当于在资源管理器里双击（用于此电脑、回收站这类不对应文件的系统图标）。
	/// </summary>
	public static void InvokeDefault(IntPtr hwnd, ShellItemSet items, POINT point)
	{
		var menuPtr = items.GetUIObject(hwnd, typeof(IContextMenu).GUID);
		var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
		var menu = CreatePopupMenu();
		try
		{
			Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(menu, 0, FirstShellId, LastShellId, CMF_DEFAULTONLY));
			int command = unchecked((int)GetMenuDefaultItem(menu, 0, 0));
			if (command >= FirstShellId)
			{
				Invoke(contextMenu, hwnd, (uint)(command - FirstShellId), point);
			}
		}
		finally
		{
			DestroyMenu(menu);
			Marshal.ReleaseComObject(contextMenu);
			Marshal.Release(menuPtr);
		}
	}

	/// <summary>
	/// 按动词执行命令，例如 properties（属性，对应 Alt+Enter）。
	/// </summary>
	public static void InvokeVerb(IntPtr hwnd, ShellItemSet items, string verb)
	{
		var menuPtr = items.GetUIObject(hwnd, typeof(IContextMenu).GUID);
		var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
		var menu = CreatePopupMenu();
		var verbA = Marshal.StringToHGlobalAnsi(verb);
		var verbW = Marshal.StringToHGlobalUni(verb);
		try
		{
			// 部分扩展要求先建好菜单才能按动词执行
			contextMenu.QueryContextMenu(menu, 0, FirstShellId, LastShellId, CMF_NORMAL);
			var info = new CMINVOKECOMMANDINFOEX
			{
				cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
				fMask = CMIC_MASK_UNICODE,
				hwnd = hwnd,
				lpVerb = verbA,
				lpVerbW = verbW,
				nShow = SW_SHOWNORMAL,
			};
			int hr = contextMenu.InvokeCommand(ref info);
			if (hr < 0)
			{
				Log.Warn($"执行菜单命令 {verb} 失败：0x{hr:X8}");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(verbA);
			Marshal.FreeHGlobal(verbW);
			DestroyMenu(menu);
			Marshal.ReleaseComObject(contextMenu);
			Marshal.Release(menuPtr);
		}
	}

	static string GetVerb(IContextMenu contextMenu, uint offset)
	{
		const int capacity = 256;
		var buffer = Marshal.AllocHGlobal(capacity * 2);
		try
		{
			Marshal.WriteInt16(buffer, 0);
			int hr = contextMenu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, capacity);
			return hr == 0 ? Marshal.PtrToStringUni(buffer) ?? string.Empty : string.Empty;
		}
		catch (Exception ex)
		{
			// 部分第三方扩展的 GetCommandString 实现不规范，取不到动词时按普通命令执行
			Log.Warn("获取菜单命令动词失败", ex);
			return string.Empty;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	/// <param name="directory">
	/// 工作目录：文件夹背景菜单必须传这个文件夹，注册表里用 %V 取当前文件夹的命令（如「通过 Code 打开」）靠它，
	/// 不传时执行失败（0x80070483，实测）；文件的菜单不需要。
	/// </param>
	static void Invoke(IContextMenu contextMenu, IntPtr hwnd, uint offset, POINT point, string? directory = null)
	{
		var modifiers = Keyboard.Modifiers;
		var directoryA = directory == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(directory);
		var directoryW = directory == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(directory);
		var info = new CMINVOKECOMMANDINFOEX
		{
			cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
			fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
					| ((modifiers & ModifierKeys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0)
					| ((modifiers & ModifierKeys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0),
			hwnd = hwnd,
			lpVerb = (IntPtr)offset,
			lpVerbW = (IntPtr)offset,
			lpDirectory = directoryA,
			lpDirectoryW = directoryW,
			nShow = SW_SHOWNORMAL,
			ptInvoke = point,
		};
		try
		{
			int hr = contextMenu.InvokeCommand(ref info);
			if (hr < 0)
			{
				Log.Warn($"执行菜单命令失败：0x{hr:X8}");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(directoryA);
			Marshal.FreeHGlobal(directoryW);
		}
	}
}

/// <summary>
/// 文件夹背景菜单的站点，代替资源管理器的文件夹视图告诉扩展当前是哪个文件夹。
/// 只实现扩展实际会调用的几个方法（实测「授予访问权限」用 Items 取选中项），其余返回 E_NOTIMPL。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class FolderViewSite(string folder) : IOleServiceProvider, IFolderView
{
	const int S_OK = 0;
	const int E_NOTIMPL = unchecked((int)0x80004001);
	const int E_NOINTERFACE = unchecked((int)0x80004002);
	const uint SVGIO_BACKGROUND = 0x0;
	const uint SVGIO_SELECTION = 0x1;
	const uint SVGIO_TYPE_MASK = 0xF;
	static readonly Guid BHID_SFObject = new("3981E224-F559-11D3-8E3A-00C04F6837D5");

	public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
	{
		// 扩展按 SID_SFolderView（与 IID_IFolderView 相同）取视图；浏览器（IShellBrowser）等其他服务不提供
		if (iid == typeof(IFolderView).GUID)
		{
			result = Marshal.GetComInterfaceForObject(this, typeof(IFolderView));
			return S_OK;
		}
		result = IntPtr.Zero;
		return E_NOINTERFACE;
	}

	/// <summary>
	/// 当前文件夹：IShellItem 一类直接创建，IShellFolder 一类从 IShellItem 绑定。
	/// </summary>
	public int GetFolder(ref Guid iid, out IntPtr result)
	{
		if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out result) == S_OK)
		{
			return S_OK;
		}
		var itemIid = typeof(IShellItem).GUID;
		int hr = SHCreateItemFromParsingName(folder, IntPtr.Zero, ref itemIid, out var itemPtr);
		if (hr != S_OK)
		{
			return hr;
		}
		var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
		try
		{
			var handler = BHID_SFObject;
			item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out result);
			return S_OK;
		}
		catch (COMException ex)
		{
			result = IntPtr.Zero;
			return ex.HResult;
		}
		finally
		{
			Marshal.ReleaseComObject(item);
			Marshal.Release(itemPtr);
		}
	}

	/// <summary>
	/// 在空白处右键没有选中项，把当前文件夹本身当作操作对象：「授予访问权限」拿到空的选中项时会把子菜单清空（实测）。
	/// </summary>
	public int Items(uint flags, ref Guid iid, out IntPtr result)
	{
		result = IntPtr.Zero;
		uint type = flags & SVGIO_TYPE_MASK;
		if (type != SVGIO_SELECTION && type != SVGIO_BACKGROUND)
		{
			return E_NOTIMPL;
		}
		var itemIid = typeof(IShellItem).GUID;
		int hr = SHCreateItemFromParsingName(folder, IntPtr.Zero, ref itemIid, out var itemPtr);
		if (hr != S_OK)
		{
			return hr;
		}
		try
		{
			return SHCreateShellItemArrayFromShellItem(itemPtr, ref iid, out result);
		}
		finally
		{
			Marshal.Release(itemPtr);
		}
	}

	public int ItemCount(uint flags, out int count)
	{
		uint type = flags & SVGIO_TYPE_MASK;
		count = type == SVGIO_SELECTION || type == SVGIO_BACKGROUND ? 1 : 0;
		return count == 1 ? S_OK : E_NOTIMPL;
	}

	public int GetCurrentViewMode(out uint mode)
	{
		mode = 0;
		return E_NOTIMPL;
	}

	public int SetCurrentViewMode(uint mode) => E_NOTIMPL;

	public int Item(int index, out IntPtr pidl)
	{
		pidl = IntPtr.Zero;
		return E_NOTIMPL;
	}

	public int GetSelectionMarkedItem(out int item)
	{
		item = -1;
		return E_NOTIMPL;
	}

	public int GetFocusedItem(out int item)
	{
		item = -1;
		return E_NOTIMPL;
	}

	public int GetItemPosition(IntPtr pidl, out POINT point)
	{
		point = default;
		return E_NOTIMPL;
	}

	public int GetSpacing(IntPtr point) => E_NOTIMPL;

	public int GetDefaultSpacing(IntPtr point) => E_NOTIMPL;

	public int GetAutoArrange() => E_NOTIMPL;

	public int SelectItem(int item, uint flags) => E_NOTIMPL;

	public int SelectAndPositionItems(uint count, IntPtr pidls, IntPtr points, uint flags) => E_NOTIMPL;
}

/// <summary>
/// 追加在系统右键菜单末尾的自定义项：Children 不为 null 时是子菜单；Swatch 是菜单项前的颜色方块；Radio 时勾选显示成圆点。
/// </summary>
internal sealed record ExtraMenuItem(string Text, Action? Action = null, IReadOnlyList<ExtraMenuItem>? Children = null, Color? Swatch = null,
		bool Checked = false, bool Radio = false, bool Enabled = true)
{
	/// <summary>
	/// 分隔线。
	/// </summary>
	public static readonly ExtraMenuItem Separator = new("-");
}
