using System.Runtime.InteropServices;

namespace MyDesktop.Native;

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
	void Advise(IFileOperationProgressSink sink, out uint cookie);
	void Unadvise(uint cookie);
	void SetOperationFlags(uint flags);
	void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
	void SetProgressDialog(nint dialog);
	void SetProperties(nint properties);
	void SetOwnerWindow(nint owner);
	void ApplyPropertiesToItem(IShellItem item);
	void ApplyPropertiesToItems(nint items);
	void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
	void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
	void MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
	void MoveItems(nint items, IShellItem folder);
	void CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
	void CopyItems(nint items, IShellItem folder);
	void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
	void DeleteItems(nint items);
	void NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
		[MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
	void PerformOperations();
	void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
	[PreserveSig] int StartOperations();
	[PreserveSig] int FinishOperations(int result);
	[PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? name);
	[PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
	[PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
	[PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
	[PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
	[PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
	[PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
	[PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
	[PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
	[PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name,
		[MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
	[PreserveSig] int UpdateProgress(uint total, uint completed);
	[PreserveSig] int ResetTimer();
	[PreserveSig] int PauseTimer();
	[PreserveSig] int ResumeTimer();
}
