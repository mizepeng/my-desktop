using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;

namespace MyDesktop.WinUI.Services;

internal sealed class FileRename(AppSettings settings, Action save)
{
	internal event Action? Changed;

	public void Rename(FenceSettings fence, string path, string displayName, string input, nint owner)
	{
		if (!ItemRename.CanRename(fence, path))
		{
			throw new InvalidOperationException("此项目不是当前目录中的普通文件或文件夹，暂不支持在这里改名。");
		}
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
		ItemRename.UpdateReferences(settings, path, target, directory);
		save();
		UsageStats.Rename(path, target);
		Changed?.Invoke();
	}
}
