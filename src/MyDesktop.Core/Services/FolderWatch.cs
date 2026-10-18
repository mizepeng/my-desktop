using MyDesktop.Core;

namespace MyDesktop.Services;

internal sealed record FolderWatch(string Folder, bool Recursive)
{
	public static IReadOnlyList<FolderWatch> Build(IEnumerable<string> desktopFolders, IEnumerable<string> portalFolders)
	{
		var portals = portalFolders.Where(Path.IsPathFullyQualified).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var contents = desktopFolders.Concat(portals).Where(Path.IsPathFullyQualified).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		// 不为嵌套目录各持有一个句柄，否则子目录的监视句柄会阻止 Windows 给父目录改名。
		var result = contents.Where(folder => !contents.Any(other => PathUtil.IsUnder(folder, other)))
			.Select(folder => new FolderWatch(folder, contents.Any(other => PathUtil.IsUnder(other, folder)))).ToList();
		foreach (string parent in portals.Select(folder => Path.GetDirectoryName(folder)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!result.Any(watch => PathUtil.AreEqual(watch.Folder, parent) || watch.Recursive && PathUtil.IsUnder(parent, watch.Folder)))
			{
				result.Add(new FolderWatch(parent, false));
			}
		}
		return result;
	}

	static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
