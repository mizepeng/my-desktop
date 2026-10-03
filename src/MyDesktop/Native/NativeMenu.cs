using System.Runtime.InteropServices;
using System.Windows.Media;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// Win32 原生弹出菜单：自动跟随系统深浅色主题和 DPI，与资源管理器的右键菜单观感一致。
/// </summary>
internal sealed class NativeMenu : IDisposable
{
	readonly IntPtr _handle;
	readonly List<Action?> _commands;
	readonly List<IntPtr> _bitmaps;
	readonly bool _isRoot;

	public NativeMenu()
	{
		_handle = CreatePopupMenu();
		_commands = [];
		_bitmaps = [];
		_isRoot = true;
	}

	NativeMenu(NativeMenu root)
	{
		_handle = CreatePopupMenu();
		_commands = root._commands;
		_bitmaps = root._bitmaps;
	}

	public NativeMenu Add(string text, Action? action, bool isChecked = false, bool enabled = true, bool radio = false, bool isDefault = false, IntPtr bitmap = default)
	{
		_commands.Add(action);
		var info = new MENUITEMINFO
		{
			cbSize = Marshal.SizeOf<MENUITEMINFO>(),
			fMask = MIIM_ID | MIIM_STRING | MIIM_FTYPE | MIIM_STATE | (bitmap != IntPtr.Zero ? MIIM_BITMAP : 0),
			fType = radio ? MFT_RADIOCHECK : 0,
			fState = (isChecked ? MFS_CHECKED : 0) | (enabled ? 0 : MFS_DISABLED) | (isDefault ? MFS_DEFAULT : 0),
			wID = (uint)_commands.Count,
			dwTypeData = Escape(text),
			hbmpItem = bitmap,
		};
		InsertMenuItem(_handle, (uint)GetMenuItemCount(_handle), true, ref info);
		return this;
	}

	public NativeMenu AddSeparator()
	{
		var info = new MENUITEMINFO
		{
			cbSize = Marshal.SizeOf<MENUITEMINFO>(),
			fMask = MIIM_FTYPE,
			fType = MFT_SEPARATOR,
		};
		InsertMenuItem(_handle, (uint)GetMenuItemCount(_handle), true, ref info);
		return this;
	}

	public NativeMenu AddSubMenu(string text, Action<NativeMenu> build, bool enabled = true)
	{
		var sub = new NativeMenu(this);
		build(sub);
		var info = new MENUITEMINFO
		{
			cbSize = Marshal.SizeOf<MENUITEMINFO>(),
			fMask = MIIM_STRING | MIIM_SUBMENU | MIIM_STATE,
			fState = enabled ? 0 : MFS_DISABLED,
			hSubMenu = sub._handle,
			dwTypeData = Escape(text),
		};
		InsertMenuItem(_handle, (uint)GetMenuItemCount(_handle), true, ref info);
		return this;
	}

	/// <summary>
	/// 生成一个纯色小方块位图作为菜单项图标，菜单释放时一并销毁。
	/// </summary>
	public IntPtr ColorSwatch(Color color)
	{
		int size = Math.Max(12, GetSystemMetrics(SM_CXMENUCHECK));
		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = size,
				biHeight = -size,
				biPlanes = 1,
				biBitCount = 32,
			},
		};
		var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
		if (bitmap == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		int fill = unchecked((int)0xFF000000) | (color.R << 16) | (color.G << 8) | color.B;
		int border = unchecked((int)0xFF8A8A8A);
		var pixels = new int[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				bool edgeX = x == 0 || x == size - 1;
				bool edgeY = y == 0 || y == size - 1;
				// 四个角留空，形成圆角观感
				pixels[y * size + x] = edgeX && edgeY ? 0 : edgeX || edgeY ? border : fill;
			}
		}
		Marshal.Copy(pixels, 0, bits, pixels.Length);
		_bitmaps.Add(bitmap);
		return bitmap;
	}

	/// <summary>
	/// 在屏幕坐标处显示菜单，选中项的回调在菜单关闭后同步执行。
	/// </summary>
	public void Show(IntPtr owner, POINT point)
	{
		// 不先置前台的话，点击菜单外部时菜单不会消失（托盘菜单的经典问题）
		SetForegroundWindow(owner);
		int command = TrackPopupMenuEx(_handle, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, owner, IntPtr.Zero);
		PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
		if (command > 0 && command <= _commands.Count)
		{
			_commands[command - 1]?.Invoke();
		}
	}

	public void Dispose()
	{
		if (!_isRoot)
		{
			return;
		}
		// DestroyMenu 会递归销毁挂在其上的子菜单
		DestroyMenu(_handle);
		_bitmaps.ForEach(b => DeleteObject(b));
	}

	static string Escape(string text) => text.Replace("&", "&&");
}
