using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

internal static class ItemRename
{
	public static bool CanRename(FenceSettings fence, string path)
	{
		if (!Path.IsPathFullyQualified(path))
		{
			return false;
		}
		string? parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'));
		// 系统入口可能解析为用户目录等真实路径，只允许操作当前目录的直接子项。
		return fence.IsPortal
			? Path.IsPathFullyQualified(fence.FolderPath) && PathUtil.AreEqual(parent, fence.FolderPath)
			: fence.Members.Any(member => PathUtil.AreEqual(member, path)) && (PathUtil.AreEqual(parent, AppPaths.Desktop) || PathUtil.AreEqual(parent, AppPaths.CommonDesktop));
	}

	public static string TargetPath(string path, string displayName, string input, bool directory)
	{
		PathUtil.ValidateFileName(input);
		string extension = Path.GetExtension(path);
		// Shell 隐藏扩展名时，编辑的仍是显示名称，实际扩展名需要保留。
		string name = !directory && extension.Length > 0 && !displayName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
			? input + extension : input;
		PathUtil.ValidateFileName(name);
		return Path.Combine(Path.GetDirectoryName(path) ?? throw new ArgumentException("项目缺少父目录。"), name);
	}

	public static void UpdateReferences(AppSettings settings, string oldPath, string newPath, bool directory)
	{
		string Rewrite(string path)
		{
			if (!Path.IsPathFullyQualified(path))
			{
				return path;
			}
			if (PathUtil.AreEqual(path, oldPath))
			{
				return newPath;
			}
			return directory && PathUtil.IsUnder(path, oldPath) ? Path.Combine(newPath, Path.GetRelativePath(oldPath, path)) : path;
		}
		foreach (var fence in settings.Fences)
		{
			fence.FolderPath = Rewrite(fence.FolderPath);
			fence.Members = fence.Members.Select(Rewrite).ToList();
			fence.CustomOrder = fence.CustomOrder.Select(Rewrite).ToList();
		}
	}
}
