using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using MyDesktop.Core;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 基于 SHFileOperation 的文件操作：与资源管理器行为一致（进度框、重名确认、可用 Ctrl+Z 撤销、删除进回收站）。
/// </summary>
internal static class ShellFileOps
{
	const uint FO_MOVE = 1;
	const uint FO_COPY = 2;
	const uint FO_DELETE = 3;
	const uint FO_RENAME = 4;
	const ushort FOF_SILENT = 0x0004;
	const ushort FOF_RENAMEONCOLLISION = 0x0008;
	const ushort FOF_NOCONFIRMATION = 0x0010;
	const ushort FOF_ALLOWUNDO = 0x0040;
	const ushort FOF_NOCONFIRMMKDIR = 0x0200;
	const ushort FOF_NOERRORUI = 0x0400;
	const ushort FOF_WANTNUKEWARNING = 0x4000;
	const int ERROR_CANCELLED = 0x4C7;
	const uint DROPEFFECT_COPY = 1;
	const uint DROPEFFECT_MOVE = 2;
	const uint DROPEFFECT_LINK = 4;
	static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

	/// <param name="silent">静默模式（自动整理用）：不弹任何界面，重名时自动改名。</param>
	public static bool Move(IntPtr owner, IEnumerable<string> sources, string targetFolder, bool renameOnCollision = false, bool silent = false)
	{
		return Run(owner, FO_MOVE, sources, targetFolder, TransferFlags(renameOnCollision, silent));
	}

	public static bool Copy(IntPtr owner, IEnumerable<string> sources, string targetFolder)
	{
		return Run(owner, FO_COPY, sources, targetFolder, TransferFlags(false, false));
	}

	public static bool Delete(IntPtr owner, IEnumerable<string> paths, bool toRecycleBin)
	{
		return Run(owner, FO_DELETE, paths, null, toRecycleBin ? (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING) : (ushort)0);
	}

	public static bool Rename(IntPtr owner, string from, string to)
	{
		return Run(owner, FO_RENAME, [from], to, FOF_ALLOWUNDO);
	}

	/// <summary>
	/// 在目标文件夹中为每个路径创建快捷方式（命名沿用资源管理器的「xxx - 快捷方式」）。
	/// </summary>
	public static void CreateShortcuts(IEnumerable<string> targets, string folder)
	{
		foreach (var target in targets)
		{
			var trimmed = target.TrimEnd('\\');
			var name = Path.GetFileName(trimmed);
			if (string.IsNullOrEmpty(name))
			{
				name = trimmed.Replace(":", string.Empty);
			}
			var linkPath = PathUtil.UniquePath(folder, $"{name} - 快捷方式", ".lnk");
			var link = (IShellLinkW)new ShellLink();
			try
			{
				link.SetPath(target);
				var workingDirectory = Path.GetDirectoryName(trimmed);
				if (!string.IsNullOrEmpty(workingDirectory))
				{
					link.SetWorkingDirectory(workingDirectory);
				}
				((IPersistFile)link).Save(linkPath, true);
			}
			finally
			{
				Marshal.ReleaseComObject(link);
			}
		}
	}

	/// <summary>
	/// 以 Shell 数据对象发起拖放，拖到资源管理器、桌面或其他程序时行为与资源管理器一致（含拖动预览图）。
	/// </summary>
	public static void DoDragDrop(IntPtr hwnd, IReadOnlyList<string> paths)
	{
		using var items = ShellItemSet.Create(paths);
		if (items == null)
		{
			return;
		}
		var dataObject = items.GetUIObject(hwnd, IID_IDataObject);
		try
		{
			SHDoDragDrop(hwnd, dataObject, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out _);
		}
		finally
		{
			Marshal.Release(dataObject);
		}
	}

	static ushort TransferFlags(bool renameOnCollision, bool silent)
	{
		ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR;
		if (silent)
		{
			// 静默时必须带 RENAMEONCOLLISION，否则 NOCONFIRMATION 会直接覆盖同名文件
			flags |= FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_RENAMEONCOLLISION;
		}
		else if (renameOnCollision)
		{
			flags |= FOF_RENAMEONCOLLISION;
		}
		return flags;
	}

	static bool Run(IntPtr owner, uint function, IEnumerable<string> from, string? to, ushort flags)
	{
		var list = from.Where(p => !string.IsNullOrEmpty(p)).ToList();
		if (list.Count == 0)
		{
			return false;
		}
		// 目标文件夹不存在时，SHFileOperation 会把单个源文件当作「改名为目标路径」处理，必须先确认目标存在
		if ((function == FO_MOVE || function == FO_COPY) && (to == null || !Directory.Exists(to)))
		{
			Log.Warn($"目标文件夹不存在，已取消文件操作：{to}");
			return false;
		}
		var operation = new SHFILEOPSTRUCT
		{
			hwnd = owner,
			wFunc = function,
			pFrom = string.Join('\0', list) + '\0',
			pTo = to == null ? null : to + '\0',
			fFlags = flags,
		};
		int result = SHFileOperation(ref operation);
		if (result != 0 && result != ERROR_CANCELLED)
		{
			Log.Warn($"SHFileOperation({function}) 失败：0x{result:X}，首个源路径：{list[0]}");
		}
		return result == 0 && !operation.fAnyOperationsAborted;
	}
}
