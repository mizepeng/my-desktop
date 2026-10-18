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
	volatile bool _bandPending;
	int _bandVersion, _moveQueued;
	long _latestPoint;

	public DesktopInputHooks(DispatcherQueue ui)
	{
		_ui = ui;
		_keyboard = Keyboard;
		_mouse = Mouse;
	}

	public nint Desktop { set => Volatile.Write(ref _desktop, value); }
	public bool Enabled
	{
		get => _enabled;
		set
		{
			_enabled = value;
			if (!value) { _bandPending = false; Interlocked.Increment(ref _bandVersion); }
		}
	}
	public event Action<DesktopInputCommand, uint, bool>? Command;
	public event Action? BlankPressed;
	public event Action<System.Drawing.Point, bool>? BandStarted;
	public event Action<System.Drawing.Point>? BandMoved;
	public event Action<System.Drawing.Point>? BandEnded;
	public event Action? BandCanceled;
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
			if (down && (key is 0x1B or VK_LWIN or VK_RWIN || key == 0x09 && Pressed(VK_MENU)))
			{
				_bandPending = false;
				Interlocked.Increment(ref _bandVersion);
				_ui.TryEnqueue(() => BandCanceled?.Invoke());
			}
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
		// 只有从桌面空白开始框选后才读取移动；所有鼠标消息始终放行，不破坏系统按键状态。
		int kind = (int)message;
		if (code >= 0 && _enabled && (kind is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN || kind == WM_MOUSEMOVE && _bandPending))
		{
			try
			{
				var point = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data).pt;
				var position = new System.Drawing.Point(point.X, point.Y);
				if (kind == WM_RBUTTONDOWN)
				{
					_bandPending = false;
					Interlocked.Increment(ref _bandVersion);
					_ui.TryEnqueue(() => BandCanceled?.Invoke());
				}
				if (kind == WM_MOUSEMOVE)
				{
					Interlocked.Exchange(ref _latestPoint, (long)(uint)point.X << 32 | (uint)point.Y);
					int version = _bandVersion;
					if (Interlocked.Exchange(ref _moveQueued, 1) == 0)
					{
						_ui.TryEnqueue(() =>
						{
							Interlocked.Exchange(ref _moveQueued, 0);
							long packed = Interlocked.Read(ref _latestPoint);
							if (version == Volatile.Read(ref _bandVersion)) { BandMoved?.Invoke(new((int)(packed >> 32), (int)packed)); }
						});
					}
				}
				else if (kind == WM_LBUTTONUP && _bandPending)
				{
					_bandPending = false;
					_ui.TryEnqueue(() => BandEnded?.Invoke(position));
				}
				else if (kind == WM_LBUTTONDOWN && GetAncestor(WindowFromPoint(point), 2) == Volatile.Read(ref _desktop)
					&& !Pressed(VK_LWIN) && !Pressed(VK_RWIN))
				{
					_bandPending = true;
					Interlocked.Increment(ref _bandVersion);
					bool additive = Pressed(VK_CONTROL) || Pressed(VK_SHIFT);
					_ui.TryEnqueue(() => BandStarted?.Invoke(position, additive));
				}
				else if (kind == WM_RBUTTONDOWN && GetAncestor(WindowFromPoint(point), 2) == Volatile.Read(ref _desktop))
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
