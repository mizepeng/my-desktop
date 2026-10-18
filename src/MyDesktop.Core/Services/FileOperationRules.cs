using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

internal sealed record SelectedFile(string Path, bool IsFileSystem, bool CanDelete);
internal sealed record ClipboardFiles(IReadOnlyList<string> Paths, bool Cut, uint Sequence);
internal sealed record CompletedFile(string Source, string? Destination, bool Directory);
internal sealed record FileOperationResult(IReadOnlyList<CompletedFile> Completed, bool Aborted, string? Error);

internal enum FileAction
{
	Copy,
	Move,
	Delete,
}

internal interface IFileClipboard
{
	ClipboardFiles? Read(nint owner);
	void Write(nint owner, IReadOnlyList<string> paths, bool cut);
	void Clear(nint owner, uint expectedSequence);
}

internal static class FileOperationRules
{
	public static string[] ValidateSelection(IReadOnlyList<SelectedFile> items, bool destructive)
	{
		if (items.Count == 0 || items.Any(item => !item.IsFileSystem || !Path.IsPathFullyQualified(item.Path) || destructive && !item.CanDelete))
		{
			throw new InvalidOperationException("请选择可操作的普通文件或文件夹；系统入口暂不支持这项操作。");
		}
		var paths = items.Select(item => Path.GetFullPath(item.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		return paths.Where(path => !paths.Any(other => PathUtil.IsUnder(path, other))).ToArray();
	}

	public static bool IsDesktopFile(string path, IEnumerable<string> folders) => Path.IsPathFullyQualified(path)
		&& folders.Any(folder => PathUtil.AreEqual(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)), folder));

	public static void ValidatePaste(IReadOnlyList<string> paths, string folder)
	{
		if (paths.Count == 0 || !Path.IsPathFullyQualified(folder)
			|| paths.Any(path => !Path.IsPathFullyQualified(path) || path.Contains('\0') || PathUtil.AreEqual(path, folder) || PathUtil.IsUnder(folder, path)))
		{
			throw new InvalidOperationException("粘贴来源或目标无效，不能把目录放进它自己或子目录中。");
		}
	}

	public static void Assign(AppSettings settings, IReadOnlyList<string> paths, FenceSettings? target)
	{
		if (target != null && (target.IsPortal || !settings.Fences.Contains(target)))
		{
			throw new ArgumentException("归属目标必须是当前配置中的桌面分区。");
		}
		var keys = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var fence in settings.Fences.Where(fence => !fence.IsPortal && fence != target))
		{
			fence.Members.RemoveAll(keys.Contains);
			fence.CustomOrder.RemoveAll(keys.Contains);
		}
		if (target != null)
		{
			foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (!target.Members.Contains(path, StringComparer.OrdinalIgnoreCase))
				{
					target.Members.Add(path);
				}
			}
		}
	}
}
