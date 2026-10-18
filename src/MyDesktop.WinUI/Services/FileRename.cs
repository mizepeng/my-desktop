using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;

namespace MyDesktop.WinUI.Services;

internal sealed class FileRename(AppSettings settings, Action save, Func<IReadOnlyList<string>> desktopFolders)
{
	internal event Action<string, string, bool>? Changed;

	public bool CanRenameDesktop(DesktopEntry entry) => ItemRename.CanRenameDesktop(entry, desktopFolders());

	public void RenameDesktop(DesktopEntry entry, string input, nint owner)
	{
		if (!CanRenameDesktop(entry))
		{
			throw new InvalidOperationException("此项目不是桌面中的普通文件或文件夹，或系统不允许改名。");
		}
		Rename(entry.Key, entry.DisplayName, input, owner);
	}

	public void Rename(FenceSettings fence, string path, string displayName, string input, nint owner)
	{
		if (!ItemRename.CanRename(fence, path))
		{
			throw new InvalidOperationException("此项目不是当前目录中的普通文件或文件夹，暂不支持在这里改名。");
		}
		Rename(path, displayName, input, owner);
	}

	void Rename(string path, string displayName, string input, nint owner)
	{
		var attributes = File.GetAttributes(path);
		bool directory = attributes.HasFlag(FileAttributes.Directory);
		string target = ItemRename.TargetPath(path, displayName, input, directory);
		if (string.Equals(path, target, StringComparison.Ordinal))
		{
			return;
		}
		if (!PathUtil.AreEqual(path, target) && (File.Exists(target) || Directory.Exists(target)))
		{
			throw new IOException("此目录中已有同名项目，请换一个名称。");
		}
		if (!ShellFileOps.Rename(owner, path, target))
		{
			throw new IOException("系统未完成重命名，请检查项目是否被占用及目录权限。");
		}
		Observe(path, target, directory);
	}

	public void Observe(string oldPath, string newPath, bool directory)
	{
		if (ItemRename.UpdateReferences(settings, oldPath, newPath, directory))
		{
			save();
		}
		UsageStats.Rename(oldPath, newPath, directory);
		Changed?.Invoke(oldPath, newPath, directory);
	}
}
