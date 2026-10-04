using System.Runtime.InteropServices;
using System.Text;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MyDesktop.Native;

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
	[PreserveSig]
	int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, out uint eaten, out IntPtr pidl, ref uint attributes);

	[PreserveSig]
	int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);

	[PreserveSig]
	int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);

	[PreserveSig]
	int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);

	[PreserveSig]
	int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

	[PreserveSig]
	int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr result);

	[PreserveSig]
	int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);

	[PreserveSig]
	int GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr result);

	[PreserveSig]
	int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);

	[PreserveSig]
	int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
	[PreserveSig]
	int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

	[PreserveSig]
	int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);

	[PreserveSig]
	int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
}

/// <summary>
/// COM 接口继承在 .NET 中不会自动带上基接口的虚表槽，因此这里重复声明 IContextMenu 的方法。
/// </summary>
[ComImport]
[Guid("000214F4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
	[PreserveSig]
	int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

	[PreserveSig]
	int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);

	[PreserveSig]
	int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);

	[PreserveSig]
	int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
}

[ComImport]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
	[PreserveSig]
	int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

	[PreserveSig]
	int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);

	[PreserveSig]
	int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);

	[PreserveSig]
	int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);

	[PreserveSig]
	int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CMINVOKECOMMANDINFOEX
{
	public int cbSize;
	public uint fMask;
	public IntPtr hwnd;
	public IntPtr lpVerb;
	public IntPtr lpParameters;
	public IntPtr lpDirectory;
	public int nShow;
	public uint dwHotKey;
	public IntPtr hIcon;
	public IntPtr lpTitle;
	public IntPtr lpVerbW;
	public IntPtr lpParametersW;
	public IntPtr lpDirectoryW;
	public IntPtr lpTitleW;
	public POINT ptInvoke;
}

[ComImport]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
	[PreserveSig]
	int GetImage(SIZE size, uint flags, out IntPtr bitmap);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
	void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);

	void GetParent(out IShellItem parent);

	void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
	void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);

	void GetPropertyStore(int flags, ref Guid iid, out IntPtr result);

	void GetPropertyDescriptionList(IntPtr keyType, ref Guid iid, out IntPtr result);

	void GetAttributes(int flags, uint mask, out uint attributes);

	void GetCount(out uint count);

	void GetItemAt(uint index, out IShellItem item);
}

/// <summary>
/// Windows 11 右键菜单扩展的命令接口；参数中的 IShellItemArray 用指针接收，按需再转换。
/// </summary>
[ComImport]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerCommand
{
	[PreserveSig]
	int GetTitle(IntPtr items, out IntPtr name);

	[PreserveSig]
	int GetIcon(IntPtr items, out IntPtr icon);

	[PreserveSig]
	int GetToolTip(IntPtr items, out IntPtr infoTip);

	[PreserveSig]
	int GetCanonicalName(out Guid name);

	[PreserveSig]
	int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);

	[PreserveSig]
	int Invoke(IntPtr items, IntPtr bindContext);

	[PreserveSig]
	int GetFlags(out uint flags);

	[PreserveSig]
	int EnumSubCommands(out IntPtr enumerator);
}

[ComImport]
[Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumExplorerCommand
{
	/// <param name="fetched">可选参数，调用方可能传空指针，所以用指针接收。</param>
	[PreserveSig]
	int Next(uint count, IntPtr commands, IntPtr fetched);

	[PreserveSig]
	int Skip(uint count);

	[PreserveSig]
	int Reset();

	[PreserveSig]
	int Clone(out IntPtr enumerator);
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
	[PreserveSig]
	int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);

	[PreserveSig]
	int LockServer([MarshalAs(UnmanagedType.Bool)] bool doLock);
}

[ComImport]
[Guid("4657278B-411B-11D2-839A-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTargetHelper
{
	[PreserveSig]
	int DragEnter(IntPtr hwndTarget, ComIDataObject dataObject, ref POINT point, int effect);

	[PreserveSig]
	int DragLeave();

	[PreserveSig]
	int DragOver(ref POINT point, int effect);

	[PreserveSig]
	int Drop(ComIDataObject dataObject, ref POINT point, int effect);

	[PreserveSig]
	int Show([MarshalAs(UnmanagedType.Bool)] bool show);
}

[ComImport]
[Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDragSourceHelper
{
	[PreserveSig]
	int InitializeFromBitmap(ref SHDRAGIMAGE dragImage, IntPtr dataObject);

	[PreserveSig]
	int InitializeFromWindow(IntPtr hwnd, ref POINT point, IntPtr dataObject);
}

[ComImport]
[Guid("000214FA-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtractIconW
{
	[PreserveSig]
	int GetIconLocation(uint flags, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconFile, int maxIconFile, out int index, out uint resultFlags);

	[PreserveSig]
	int Extract([MarshalAs(UnmanagedType.LPWStr)] string iconFile, uint index, out IntPtr largeIcon, out IntPtr smallIcon, uint iconSizes);
}

/// <summary>
/// CLSID_DragDropHelper：提供与资源管理器一致的拖放预览图。
/// </summary>
[ComImport]
[Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
internal class DragDropHelper
{
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
	void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);

	void GetIDList(out IntPtr pidl);

	void SetIDList(IntPtr pidl);

	void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

	void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

	void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

	void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

	void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);

	void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

	void GetHotkey(out short hotkey);

	void SetHotkey(short hotkey);

	void GetShowCmd(out int showCmd);

	void SetShowCmd(int showCmd);

	void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);

	void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

	void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

	void Resolve(IntPtr hwnd, uint flags);

	void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLink
{
}

/// <summary>
/// 同一文件夹下一组文件的 Shell 表示（父 IShellFolder + 子 PIDL），用于获取系统右键菜单和拖放数据对象。
/// </summary>
internal sealed class ShellItemSet : IDisposable
{
	readonly List<IntPtr> _absolutePidls;
	readonly IntPtr[] _childPidls;
	readonly IntPtr _parentPtr;
	readonly IShellFolder _parent;

	ShellItemSet(List<IntPtr> absolutePidls, IntPtr[] childPidls, IntPtr parentPtr)
	{
		_absolutePidls = absolutePidls;
		_childPidls = childPidls;
		_parentPtr = parentPtr;
		_parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
	}

	/// <summary>
	/// 为一组路径创建 Shell 项集合，所有路径必须位于同一文件夹；无可用项时返回 null。
	/// </summary>
	public static ShellItemSet? Create(IEnumerable<string> paths)
	{
		var absolute = new List<IntPtr>();
		var parentPtr = IntPtr.Zero;
		try
		{
			foreach (var path in paths)
			{
				if (NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
				{
					absolute.Add(pidl);
				}
			}
			if (absolute.Count == 0)
			{
				return null;
			}
			var children = new IntPtr[absolute.Count];
			for (int i = 0; i < absolute.Count; i++)
			{
				var iid = typeof(IShellFolder).GUID;
				Marshal.ThrowExceptionForHR(NativeMethods.SHBindToParent(absolute[i], ref iid, out var folder, out children[i]));
				if (i == 0)
				{
					parentPtr = folder;
				}
				else
				{
					Marshal.Release(folder);
				}
			}
			return new ShellItemSet(absolute, children, parentPtr);
		}
		catch
		{
			if (parentPtr != IntPtr.Zero)
			{
				Marshal.Release(parentPtr);
			}
			absolute.ForEach(NativeMethods.ILFree);
			throw;
		}
	}

	/// <summary>
	/// 获取这组项的 UI 对象（IContextMenu、IDataObject 等），返回的指针由调用方 Release。
	/// </summary>
	public IntPtr GetUIObject(IntPtr hwnd, Guid iid)
	{
		Marshal.ThrowExceptionForHR(_parent.GetUIObjectOf(hwnd, (uint)_childPidls.Length, _childPidls, ref iid, IntPtr.Zero, out var result));
		return result;
	}

	public void Dispose()
	{
		Marshal.ReleaseComObject(_parent);
		Marshal.Release(_parentPtr);
		_absolutePidls.ForEach(NativeMethods.ILFree);
	}
}
