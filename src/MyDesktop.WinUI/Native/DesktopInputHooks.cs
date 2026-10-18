using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

/// <summary>
/// 接管期间保护隐藏的 Explorer 列表，只把桌面输入交回 WinUI；不改动其他程序或 Win 快捷键的输入。
/// </summary>
internal sealed class DesktopInputHooks : IDisposable
{
	readonly DispatcherQueue _ui;
	readonly Windows.System.DispatcherQueueController _worker = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
	readonly LowLevelKeyboardProc _keyboard;
	readonly LowLevelMouseProc _mouse;
	readonly DesktopKeyRouting _routing = new();
	nint _keyboardHook, _mouseHook, _desktop;
	volatile bool _enabled;

	public DesktopInputHooks(DispatcherQueue ui)
	{
		_ui = ui;
		_keyboard = Keyboard;
		_mouse = Mouse;
	}

	public nint Desktop { set => Volatile.Write(ref _desktop, value); }
	public bool Enabled { get => _enabled; set => _enabled = value; }
	public event Action<DesktopInputCommand, uint, bool>? Command;
	public event Action? BlankPressed;
	public event Action<string>? Failed;
	public event Action? NativeEditDetected;

	public Task StartAsync()
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_worker.DispatcherQueue.TryEnqueue(() =>
		{
			_keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboard, GetModuleHandle(null), 0);
			_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouse, GetModuleHandle(null), 0);
			if (_keyboardHook == 0 || _mouseHook == 0)
			{
				completion.SetException(new Win32Exception());
			}
			else
			{
				completion.SetResult();
			}
		});
		return completion.Task;
	}

	nint Keyboard(int code, nint message, nint data)
	{
		if (code < 0)
		{
			return CallNextHookEx(_keyboardHook, code, message, data);
		}
		try
		{
			var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data).vkCode;
			bool down = (int)message is WM_KEYDOWN or WM_SYSKEYDOWN;
			var foreground = GetForegroundWindow();
			bool desktop = _enabled && foreground != 0 && foreground == Volatile.Read(ref _desktop);
			var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
			bool menu = desktop && GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info)
				&& (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0;
			bool edit = desktop && GetClassName(info.hwndFocus) == "Edit";
			if (edit && !IsWindowVisible(info.hwndFocus))
			{
				_ui.TryEnqueue(() => NativeEditDetected?.Invoke());
			}
			bool shift = Pressed(VK_SHIFT);
			var route = _routing.Route(key, down, desktop, Pressed(VK_LWIN) || Pressed(VK_RWIN), Pressed(VK_CONTROL), shift, Pressed(VK_MENU), menu || edit);
			if (route.Command is { } command)
			{
				_ui.TryEnqueue(() => Command?.Invoke(command, key, shift));
			}
			if (route.Intercept)
			{
				return 1;
			}
		}
		catch (Exception ex)
		{
			_enabled = false;
			_ui.TryEnqueue(() => Failed?.Invoke(ex.Message));
			return 1;
		}
		return CallNextHookEx(_keyboardHook, code, message, data);
	}

	nint Mouse(int code, nint message, nint data)
	{
		// 移动和滚轮不参与本阶段的选择逻辑，直接放行，避免影响其他程序的鼠标响应。
		if (code >= 0 && _enabled && (int)message is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
		{
			try
			{
				var point = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data).pt;
				if (GetAncestor(WindowFromPoint(point), 2) == Volatile.Read(ref _desktop))
				{
					_ui.TryEnqueue(() => BlankPressed?.Invoke());
				}
			}
			catch (Exception ex)
			{
				_enabled = false;
				_ui.TryEnqueue(() => Failed?.Invoke(ex.Message));
			}
		}
		return CallNextHookEx(_mouseHook, code, message, data);
	}

	static bool Pressed(int key) => GetAsyncKeyState(key) < 0;

	public void Dispose()
	{
		_enabled = false;
		_worker.DispatcherQueue.TryEnqueue(() =>
		{
			if (_keyboardHook != 0)
			{
				UnhookWindowsHookEx(_keyboardHook);
			}
			if (_mouseHook != 0)
			{
				UnhookWindowsHookEx(_mouseHook);
			}
		});
		_ = _worker.ShutdownQueueAsync();
	}
}
