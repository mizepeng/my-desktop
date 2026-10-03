using System.Runtime.InteropServices;
using System.Text;

namespace MyDesktop.Native;

/// <summary>
/// Win32 API 声明（user32 / shell32 / gdi32 / shcore / comdlg32 / uxtheme）。
/// </summary>
internal static class NativeMethods
{
	public const int GWL_STYLE = -16;
	public const int GWL_EXSTYLE = -20;
	public const int GWLP_HWNDPARENT = -8;

	public const long WS_MINIMIZEBOX = 0x00020000L;
	public const long WS_MAXIMIZEBOX = 0x00010000L;
	public const long WS_EX_TOOLWINDOW = 0x00000080L;
	public const long WS_EX_APPWINDOW = 0x00040000L;

	public static readonly IntPtr HWND_BOTTOM = new(1);
	public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

	public const uint SWP_NOSIZE = 0x0001;
	public const uint SWP_NOMOVE = 0x0002;
	public const uint SWP_NOZORDER = 0x0004;
	public const uint SWP_NOACTIVATE = 0x0010;

	public const int SW_HIDE = 0;
	public const int SW_SHOWNORMAL = 1;
	public const int SW_SHOWNA = 8;

	public const int WM_NULL = 0x0000;
	public const int WM_SETTINGCHANGE = 0x001A;
	public const int WM_DRAWITEM = 0x002B;
	public const int WM_MEASUREITEM = 0x002C;
	public const int WM_WINDOWPOSCHANGING = 0x0046;
	public const int WM_DISPLAYCHANGE = 0x007E;
	public const int WM_NCHITTEST = 0x0084;
	public const int WM_SYSCOMMAND = 0x0112;
	public const int WM_INITMENUPOPUP = 0x0117;
	public const int WM_MENUCHAR = 0x0120;
	public const int WM_LBUTTONDOWN = 0x0201;
	public const int WM_LBUTTONUP = 0x0202;
	public const int WM_RBUTTONUP = 0x0205;
	public const int WM_SIZING = 0x0214;
	public const int WM_MOVING = 0x0216;
	public const int WM_ENTERSIZEMOVE = 0x0231;
	public const int WM_EXITSIZEMOVE = 0x0232;
	public const int WM_APP = 0x8000;

	public const int SC_MINIMIZE = 0xF020;
	public const int SC_MAXIMIZE = 0xF030;
	public const int SC_CLOSE = 0xF060;

	public const int HTLEFT = 10;
	public const int HTRIGHT = 11;
	public const int HTTOP = 12;
	public const int HTTOPLEFT = 13;
	public const int HTTOPRIGHT = 14;
	public const int HTBOTTOM = 15;
	public const int HTBOTTOMLEFT = 16;
	public const int HTBOTTOMRIGHT = 17;

	public const int WMSZ_LEFT = 1;
	public const int WMSZ_RIGHT = 2;
	public const int WMSZ_TOP = 3;
	public const int WMSZ_TOPLEFT = 4;
	public const int WMSZ_TOPRIGHT = 5;
	public const int WMSZ_BOTTOM = 6;
	public const int WMSZ_BOTTOMLEFT = 7;
	public const int WMSZ_BOTTOMRIGHT = 8;

	public const int WH_MOUSE_LL = 14;

	public const int SM_CXDOUBLECLK = 36;
	public const int SM_CYDOUBLECLK = 37;
	public const int SM_CXSMICON = 49;
	public const int SM_CXMENUCHECK = 71;

	public const uint MONITOR_DEFAULTTONULL = 0;
	public const uint MONITOR_DEFAULTTOPRIMARY = 1;
	public const uint MONITOR_DEFAULTTONEAREST = 2;
	public const int MDT_EFFECTIVE_DPI = 0;

	public const uint MSGFLT_ALLOW = 1;

	public const uint GW_HWNDNEXT = 2;
	public const uint GW_HWNDPREV = 3;

	public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
	public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
	public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

	public const uint MIIM_STATE = 0x0001;
	public const uint MIIM_ID = 0x0002;
	public const uint MIIM_SUBMENU = 0x0004;
	public const uint MIIM_STRING = 0x0040;
	public const uint MIIM_BITMAP = 0x0080;
	public const uint MIIM_FTYPE = 0x0100;
	public const uint MFT_RADIOCHECK = 0x0200;
	public const uint MFT_SEPARATOR = 0x0800;
	public const uint MFS_DISABLED = 0x0003;
	public const uint MFS_CHECKED = 0x0008;
	public const uint MFS_DEFAULT = 0x1000;
	public const uint MF_BYPOSITION = 0x0400;
	public const uint MF_STRING = 0x0000;
	public const uint MF_SEPARATOR = 0x0800;
	public const uint TPM_RIGHTBUTTON = 0x0002;
	public const uint TPM_RETURNCMD = 0x0100;

	public const uint NIM_ADD = 0;
	public const uint NIM_MODIFY = 1;
	public const uint NIM_DELETE = 2;
	public const uint NIF_MESSAGE = 0x01;
	public const uint NIF_ICON = 0x02;
	public const uint NIF_TIP = 0x04;
	public const uint NIF_INFO = 0x10;
	public const uint NIIF_INFO = 0x01;

	public const uint SHGFI_DISPLAYNAME = 0x0200;
	public const uint SHGFI_TYPENAME = 0x0400;

	public const int CC_RGBINIT = 0x0001;
	public const int CC_FULLOPEN = 0x0002;

	#region user32：窗口

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

	public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

	[DllImport("user32.dll")]
	public static extern bool ShowWindowAsync(IntPtr hwnd, int cmdShow);

	[DllImport("user32.dll")]
	public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

	[DllImport("user32.dll")]
	public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

	[DllImport("user32.dll")]
	public static extern bool IsWindow(IntPtr hwnd);

	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(IntPtr hwnd);

	[DllImport("user32.dll")]
	public static extern IntPtr WindowFromPoint(POINT point);

	[DllImport("user32.dll")]
	public static extern IntPtr GetParent(IntPtr hwnd);

	[DllImport("user32.dll")]
	public static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr hwnd);

	[DllImport("user32.dll")]
	public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int RegisterWindowMessage(string name);

	[DllImport("user32.dll")]
	public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, uint action, IntPtr changeFilterStruct);

	[DllImport("user32.dll")]
	public static extern uint GetDoubleClickTime();

	[DllImport("user32.dll")]
	public static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(IntPtr hwnd);

	#endregion

	#region user32：显示器

	[DllImport("user32.dll")]
	public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

	[DllImport("user32.dll")]
	public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

	[DllImport("shcore.dll")]
	public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

	#endregion

	#region user32：钩子

	public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

	[DllImport("user32.dll")]
	public static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll")]
	public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr GetModuleHandle(string? moduleName);

	public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);

	[DllImport("user32.dll")]
	public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll")]
	public static extern bool UnhookWinEvent(IntPtr hook);

	#endregion

	#region user32：菜单与图标

	[DllImport("user32.dll")]
	public static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll")]
	public static extern bool DestroyMenu(IntPtr menu);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern bool InsertMenuItem(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern bool InsertMenu(IntPtr menu, uint position, uint flags, UIntPtr newItemId, string? text);

	[DllImport("user32.dll")]
	public static extern int GetMenuItemCount(IntPtr menu);

	[DllImport("user32.dll")]
	public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

	[DllImport("user32.dll")]
	public static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, IntPtr iconIds, uint count, uint flags);

	#endregion

	#region gdi32

	[DllImport("gdi32.dll")]
	public static extern bool DeleteObject(IntPtr obj);

	[DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
	public static extern int GetObject(IntPtr obj, int size, out DIBSECTION section);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

	#endregion

	#region shell32 / shlwapi

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

	[DllImport("shell32.dll")]
	public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr folder, out IntPtr pidlLast);

	[DllImport("shell32.dll")]
	public static extern void ILFree(IntPtr pidl);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

	[DllImport("shell32.dll")]
	public static extern int SHDoDragDrop(IntPtr hwnd, IntPtr dataObject, IntPtr dropSource, uint okEffects, out uint effect);

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
	public static extern int StrCmpLogicalW(string a, string b);

	#endregion

	#region comdlg32 / uxtheme

	[DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
	public static extern bool ChooseColor(ref CHOOSECOLOR choose);

	/// <summary>
	/// 未公开 API（uxtheme 序号 135），让 Win32 原生菜单跟随深色/浅色主题。
	/// 参数：0 默认、1 允许深色、2 强制深色、3 强制浅色。
	/// </summary>
	[DllImport("uxtheme.dll", EntryPoint = "#135")]
	public static extern int SetPreferredAppMode(int mode);

	/// <summary>
	/// 未公开 API（uxtheme 序号 136），刷新菜单主题缓存。
	/// </summary>
	[DllImport("uxtheme.dll", EntryPoint = "#136")]
	public static extern void FlushMenuThemes();

	#endregion

	public static string GetClassName(IntPtr hwnd)
	{
		var sb = new StringBuilder(256);
		return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
	}

	public static RECT GetWindowRect(IntPtr hwnd)
	{
		GetWindowRect(hwnd, out var rect);
		return rect;
	}

	public static POINT GetCursorPos()
	{
		GetCursorPos(out var point);
		return point;
	}

	/// <summary>
	/// 获取显示器工作区（物理像素）与缩放比例。
	/// </summary>
	public static (RECT WorkArea, double Scale) GetMonitorWorkArea(IntPtr monitor)
	{
		var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
		GetMonitorInfo(monitor, ref info);
		double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
		return (info.rcWork, scale);
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
	public int X;
	public int Y;

	public POINT(int x, int y)
	{
		X = x;
		Y = y;
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
	public int Left;
	public int Top;
	public int Right;
	public int Bottom;

	public RECT(int left, int top, int right, int bottom)
	{
		Left = left;
		Top = top;
		Right = right;
		Bottom = bottom;
	}

	public readonly int Width => Right - Left;

	public readonly int Height => Bottom - Top;

	public readonly bool IntersectsWith(RECT other) => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

	public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

	public readonly RECT Inflate(int size) => new(Left - size, Top - size, Right + size, Bottom + size);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
	public int cx;
	public int cy;

	public SIZE(int cx, int cy)
	{
		this.cx = cx;
		this.cy = cy;
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
	public IntPtr hwnd;
	public IntPtr hwndInsertAfter;
	public int x;
	public int y;
	public int cx;
	public int cy;
	public uint flags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFO
{
	public int cbSize;
	public RECT rcMonitor;
	public RECT rcWork;
	public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
	public POINT pt;
	public uint mouseData;
	public uint flags;
	public uint time;
	public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MENUITEMINFO
{
	public int cbSize;
	public uint fMask;
	public uint fType;
	public uint fState;
	public uint wID;
	public IntPtr hSubMenu;
	public IntPtr hbmpChecked;
	public IntPtr hbmpUnchecked;
	public IntPtr dwItemData;
	[MarshalAs(UnmanagedType.LPWStr)]
	public string? dwTypeData;
	public uint cch;
	public IntPtr hbmpItem;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATA
{
	public int cbSize;
	public IntPtr hWnd;
	public uint uID;
	public uint uFlags;
	public uint uCallbackMessage;
	public IntPtr hIcon;
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
	public string szTip;
	public uint dwState;
	public uint dwStateMask;
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
	public string szInfo;
	public uint uTimeoutOrVersion;
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
	public string szInfoTitle;
	public uint dwInfoFlags;
	public Guid guidItem;
	public IntPtr hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEOPSTRUCT
{
	public IntPtr hwnd;
	public uint wFunc;
	[MarshalAs(UnmanagedType.LPWStr)]
	public string pFrom;
	[MarshalAs(UnmanagedType.LPWStr)]
	public string? pTo;
	public ushort fFlags;
	[MarshalAs(UnmanagedType.Bool)]
	public bool fAnyOperationsAborted;
	public IntPtr hNameMappings;
	[MarshalAs(UnmanagedType.LPWStr)]
	public string? lpszProgressTitle;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFO
{
	public IntPtr hIcon;
	public int iIcon;
	public uint dwAttributes;
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
	public string szDisplayName;
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
	public string szTypeName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CHOOSECOLOR
{
	public int lStructSize;
	public IntPtr hwndOwner;
	public IntPtr hInstance;
	public int rgbResult;
	public IntPtr lpCustColors;
	public int Flags;
	public IntPtr lCustData;
	public IntPtr lpfnHook;
	public IntPtr lpTemplateName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
	public int bmType;
	public int bmWidth;
	public int bmHeight;
	public int bmWidthBytes;
	public ushort bmPlanes;
	public ushort bmBitsPixel;
	public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
	public uint biSize;
	public int biWidth;
	public int biHeight;
	public ushort biPlanes;
	public ushort biBitCount;
	public uint biCompression;
	public uint biSizeImage;
	public int biXPelsPerMeter;
	public int biYPelsPerMeter;
	public uint biClrUsed;
	public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
	public BITMAPINFOHEADER bmiHeader;
	public uint bmiColors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DIBSECTION
{
	public BITMAP dsBm;
	public BITMAPINFOHEADER dsBmih;
	public uint dsBitfields0;
	public uint dsBitfields1;
	public uint dsBitfields2;
	public IntPtr dshSection;
	public uint dsOffset;
}
