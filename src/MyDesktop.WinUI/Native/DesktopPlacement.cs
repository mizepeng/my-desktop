using System.ComponentModel;
using System.Runtime.InteropServices;
using MyDesktop.Core;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

internal sealed class DesktopPlacement : IDisposable
{
	public const uint RefreshMessage = 0x8051;
	readonly nint _hwnd;
	readonly nint _root = GetDesktopWindow();
	readonly WinEventProc _callback;
	readonly nint _foregroundHook, _windowHook;
	nint _desktop;
	bool _started, _queued, _disposed;

	public DesktopPlacement(nint hwnd)
	{
		_hwnd = hwnd;
		_callback = OnWindowEvent;
		_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0,
			WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		if (_foregroundHook == 0)
		{
			throw new Win32Exception();
		}
		_windowHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_REORDER, 0, _callback, 0, 0,
			WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
		if (_windowHook == 0)
		{
			int error = Marshal.GetLastWin32Error();
			UnhookWinEvent(_foregroundHook);
			throw new Win32Exception(error);
		}
	}

	public void Show()
	{
		_started = true;
		Refresh();
	}

	public nint DesktopHandle => FindDesktop();

	public void Refresh()
	{
		_queued = false;
		if (_disposed || !_started)
		{
			return;
		}
		var desktop = FindDesktop();
		if (desktop == 0)
		{
			WindowApi.ShowWindow(_hwnd, SW_HIDE);
			return;
		}
		var (anchor, adjacent) = AnchorAbove(desktop);
		if (!NeedsMove(anchor, adjacent) && IsWindowVisible(_hwnd))
		{
			return;
		}
		// 具体窗口只在自己的 WM_WINDOWPOSCHANGING 中写入，避免跨权限和置顶层切换的已知问题。
		// 无上方参照时直接传 TOPMOST 临时提起；TOP 会被解析为宿主名下的隐藏 IME 窗口，导致整个窗口组掉到底层。
		var insertAfter = anchor == 0 ? HWND_TOPMOST : HWND_TOP;
		if (!SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
		{
			throw new Win32Exception();
		}
	}

	public void Constrain(nint lParam)
	{
		var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
		if ((position.flags & SWP_NOZORDER) != 0)
		{
			return;
		}
		var desktop = FindDesktop();
		if (desktop == 0)
		{
			position.flags |= SWP_NOZORDER;
		}
		else
		{
			var (anchor, adjacent) = AnchorAbove(desktop);
			if (NeedsMove(anchor, adjacent))
			{
				position.hwndInsertAfter = anchor == 0 ? HWND_TOPMOST : anchor;
			}
			else
			{
				// 已经正确时保留层级，避免重复移动宿主反而把它名下的弹出窗口压到下面。
				position.flags |= SWP_NOZORDER;
			}
		}
		position.flags |= SWP_NOACTIVATE;
		Marshal.StructureToPtr(position, lParam, false);
	}

	bool NeedsMove(nint anchor, bool adjacent)
	{
		bool anchorTopmost = anchor != 0 && (GetWindowLongPtr(anchor, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
		bool currentlyTopmost = (GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
		// 桌面最前且没有参照时保留临时置顶；普通应用回到桌面上方后，随其参照撤销置顶。
		return !adjacent || (currentlyTopmost && anchor != 0 && !anchorTopmost);
	}

	nint FindDesktop()
	{
		if (_desktop != 0 && FindWindowEx(_desktop, 0, "SHELLDLL_DefView", null) != 0)
		{
			return _desktop;
		}
		var found = FindWindowEx(0, 0, "Progman", null);
		if (found == 0 || FindWindowEx(found, 0, "SHELLDLL_DefView", null) == 0)
		{
			found = 0;
			EnumWindows((window, _) =>
			{
				if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) == 0)
				{
					return true;
				}
				found = window;
				return false;
			}, 0);
		}
		if (_desktop != found)
		{
			_desktop = found;
			Log.Info(found == 0 ? "桌面宿主暂不可用，等待窗口重建。" : $"桌面宿主已连接：{GetClassName(found)} (0x{found:X})。");
		}
		return found;
	}

	(nint Anchor, bool Adjacent) AnchorAbove(nint desktop)
	{
		bool adjacent = false;
		for (var above = GetWindow(desktop, GW_HWNDPREV); above != 0; above = GetWindow(above, GW_HWNDPREV))
		{
			if (above == _hwnd)
			{
				adjacent = true;
				continue;
			}
			if (!IsWindowVisible(above) || IsIconic(above) || OwnedByHost(above))
			{
				continue;
			}
			if (DwmGetWindowAttribute(above, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
			{
				continue;
			}
			const long transparent = WS_EX_LAYERED | WS_EX_TRANSPARENT;
			if ((GetWindowLongPtr(above, GWL_EXSTYLE).ToInt64() & transparent) == transparent)
			{
				continue;
			}
			return (above, adjacent);
		}
		return (0, adjacent);
	}

	bool OwnedByHost(nint window)
	{
		for (var owner = GetWindow(window, GW_OWNER); owner != 0; owner = GetWindow(owner, GW_OWNER))
		{
			if (owner == _hwnd)
			{
				return true;
			}
		}
		return false;
	}

	void OnWindowEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
	{
		if (_disposed || _queued)
		{
			return;
		}
		// 顶层重排通知来自桌面根窗口的客户区，不能按普通窗口对象过滤。
		bool rootReorder = kind == EVENT_OBJECT_REORDER && window == _root;
		if (!rootReorder && (objectId != 0 || childId != 0))
		{
			return;
		}
		if (kind != EVENT_SYSTEM_FOREGROUND && window != _root && window != _desktop
			&& GetClassName(window) is not ("Progman" or "WorkerW" or "SHELLDLL_DefView"))
		{
			return;
		}
		_queued = true;
		if (!PostMessage(_hwnd, (int)RefreshMessage, 0, 0))
		{
			throw new Win32Exception();
		}
	}

	public void Dispose()
	{
		_disposed = true;
		UnhookWinEvent(_windowHook);
		UnhookWinEvent(_foregroundHook);
	}
}
