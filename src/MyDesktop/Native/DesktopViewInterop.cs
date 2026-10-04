using System.Runtime.InteropServices;

namespace MyDesktop.Native;

/// <summary>
/// 已打开的 Shell 窗口集合；用 FindWindowSW(SWC_DESKTOP) 找到资源管理器的桌面视图。
/// 只用到 FindWindowSW，前面的方法按虚表顺序占位。
/// </summary>
[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
	[PreserveSig]
	int GetCount(out int count);

	[PreserveSig]
	int Item(IntPtr index, out IntPtr folder);

	[PreserveSig]
	int NewEnum(out IntPtr enumerator);

	[PreserveSig]
	int Register(IntPtr dispatch, int hwnd, int windowClass, out int cookie);

	[PreserveSig]
	int RegisterPending(int threadId, IntPtr location, IntPtr locationRoot, int windowClass, out int cookie);

	[PreserveSig]
	int Revoke(int cookie);

	[PreserveSig]
	int OnNavigate(int cookie, IntPtr location);

	[PreserveSig]
	int OnActivated(int cookie, short active);

	[PreserveSig]
	int FindWindowSW(ref object location, ref object locationRoot, int windowClass, out int hwnd, int options,
			[MarshalAs(UnmanagedType.IDispatch)] out object dispatch);
}

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
	[PreserveSig]
	int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
}

/// <summary>
/// 只用到 QueryActiveShellView，前面的方法（含 IOleWindow 的两个）按虚表顺序占位。
/// </summary>
[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
	[PreserveSig]
	int GetWindow(out IntPtr hwnd);

	[PreserveSig]
	int ContextSensitiveHelp(int enterMode);

	[PreserveSig]
	int InsertMenusSB(IntPtr menu, IntPtr widths);

	[PreserveSig]
	int SetMenuSB(IntPtr menu, IntPtr shared, IntPtr activeObject);

	[PreserveSig]
	int RemoveMenusSB(IntPtr menu);

	[PreserveSig]
	int SetStatusTextSB(IntPtr text);

	[PreserveSig]
	int EnableModelessSB(int enable);

	[PreserveSig]
	int TranslateAcceleratorSB(IntPtr message, short id);

	[PreserveSig]
	int BrowseObject(IntPtr pidl, uint flags);

	[PreserveSig]
	int GetViewStateStream(uint mode, out IntPtr stream);

	[PreserveSig]
	int GetControlWindow(uint id, out IntPtr hwnd);

	[PreserveSig]
	int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);

	[PreserveSig]
	int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
}

/// <summary>
/// 文件夹视图（含 IFolderView 的方法），用来读写桌面图标的位置、排列方式、间距和图标大小。
/// 没用到的方法按虚表顺序占位。
/// </summary>
[ComImport]
[Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
	[PreserveSig]
	int GetCurrentViewMode(out uint mode);

	[PreserveSig]
	int SetCurrentViewMode(uint mode);

	[PreserveSig]
	int GetFolder(ref Guid iid, out IntPtr folder);

	[PreserveSig]
	int Item(int index, out IntPtr pidl);

	[PreserveSig]
	int ItemCount(uint flags, out int count);

	[PreserveSig]
	int Items(uint flags, ref Guid iid, out IntPtr items);

	[PreserveSig]
	int GetSelectionMarkedItem(out int item);

	[PreserveSig]
	int GetFocusedItem(out int item);

	[PreserveSig]
	int GetItemPosition(IntPtr pidl, out POINT point);

	[PreserveSig]
	int GetSpacing(out POINT spacing);

	[PreserveSig]
	int GetDefaultSpacing(out POINT spacing);

	/// <summary>
	/// 自动排列开着时返回 S_OK（0），否则 S_FALSE。
	/// </summary>
	[PreserveSig]
	int GetAutoArrange();

	[PreserveSig]
	int SelectItem(int index, uint flags);

	[PreserveSig]
	int SelectAndPositionItems(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
			[MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] POINT[] points, uint flags);

	[PreserveSig]
	int SetGroupBy(IntPtr key, int ascending);

	[PreserveSig]
	int GetGroupBy(IntPtr key, out int ascending);

	[PreserveSig]
	int SetViewProperty(IntPtr pidl, IntPtr key, IntPtr value);

	[PreserveSig]
	int GetViewProperty(IntPtr pidl, IntPtr key, IntPtr value);

	[PreserveSig]
	int SetTileViewProperties(IntPtr pidl, IntPtr properties);

	[PreserveSig]
	int SetExtendedTileViewProperties(IntPtr pidl, IntPtr properties);

	[PreserveSig]
	int SetText(int type, IntPtr text);

	[PreserveSig]
	int SetCurrentFolderFlags(uint mask, uint flags);

	[PreserveSig]
	int GetCurrentFolderFlags(out uint flags);

	[PreserveSig]
	int GetSortColumnCount(out int count);

	[PreserveSig]
	int SetSortColumns(IntPtr columns, int count);

	[PreserveSig]
	int GetSortColumns(IntPtr columns, int count);

	[PreserveSig]
	int GetItem(int index, ref Guid iid, out IntPtr item);

	[PreserveSig]
	int GetVisibleItem(int start, int previous, out int item);

	[PreserveSig]
	int GetSelectedItem(int start, out int item);

	[PreserveSig]
	int GetSelection(int noneImpliesFolder, out IntPtr items);

	[PreserveSig]
	int GetSelectionState(IntPtr pidl, out uint flags);

	[PreserveSig]
	int InvokeVerbOnSelection(IntPtr verb);

	[PreserveSig]
	int SetViewModeAndIconSize(uint mode, int size);

	[PreserveSig]
	int GetViewModeAndIconSize(out uint mode, out int size);
}
