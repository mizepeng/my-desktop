using System.Runtime.InteropServices;
using System.Windows.Input;
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
	/// <param name="extraItems">追加在菜单末尾的自定义项。</param>
	/// <param name="handleVerb">拦截指定动词（如 rename），返回 true 表示已自行处理。</param>
	public static void Show(IntPtr hwnd, ShellItemSet items, POINT point, IReadOnlyList<(string Text, Action Action)> extraItems, Func<string, bool> handleVerb)
	{
		var menuPtr = items.GetUIObject(hwnd, typeof(IContextMenu).GUID);
		var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
		var menu = CreatePopupMenu();
		try
		{
			bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
			uint flags = CMF_NORMAL | CMF_CANRENAME | (shift ? CMF_EXTENDEDVERBS : 0);
			Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(menu, 0, FirstShellId, LastShellId, flags));
			if (extraItems.Count > 0)
			{
				InsertMenu(menu, uint.MaxValue, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
				for (int i = 0; i < extraItems.Count; i++)
				{
					InsertMenu(menu, uint.MaxValue, MF_BYPOSITION | MF_STRING, (UIntPtr)(uint)(FirstExtraId + i), extraItems[i].Text);
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
				if (index < extraItems.Count)
				{
					extraItems[index].Action();
				}
			}
			else if (command >= FirstShellId)
			{
				uint offset = (uint)(command - FirstShellId);
				if (!handleVerb(GetVerb(contextMenu, offset)))
				{
					Invoke(contextMenu, hwnd, offset, point);
				}
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

	static void Invoke(IContextMenu contextMenu, IntPtr hwnd, uint offset, POINT point)
	{
		var modifiers = Keyboard.Modifiers;
		var info = new CMINVOKECOMMANDINFOEX
		{
			cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
			fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
					| ((modifiers & ModifierKeys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0)
					| ((modifiers & ModifierKeys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0),
			hwnd = hwnd,
			lpVerb = (IntPtr)offset,
			lpVerbW = (IntPtr)offset,
			nShow = SW_SHOWNORMAL,
			ptInvoke = point,
		};
		int hr = contextMenu.InvokeCommand(ref info);
		if (hr < 0)
		{
			Log.Warn($"执行菜单命令失败：0x{hr:X8}");
		}
	}
}
