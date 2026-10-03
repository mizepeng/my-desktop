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
