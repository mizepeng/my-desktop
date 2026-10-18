using MyDesktop.Core;

namespace MyDesktop.Services;

[Flags]
internal enum FileDropEffect
{
	None = 0,
	Copy = 1,
	Move = 2,
}

internal static class FileDropRules
{
	public static FileDropEffect Choose(IReadOnlyList<string> paths, string folder, bool desktopTransfer, FileDropEffect allowed,
		bool control, bool shift, bool alt)
	{
		if (paths.Count == 0 || alt || control && shift || !Path.IsPathFullyQualified(folder)
			|| paths.Any(path => !Path.IsPathFullyQualified(path) || PathUtil.AreEqual(path, folder) || PathUtil.IsUnder(folder, path)))
		{
			return FileDropEffect.None;
		}
		// 桌面内部拖放只改归属，和旧入口一样不因 Ctrl 复制出另一份文件。
		if (desktopTransfer) { return allowed & FileDropEffect.Move; }
		if (paths.All(path => PathUtil.AreEqual(Path.GetDirectoryName(path), folder))) { return FileDropEffect.None; }
		var preferred = control ? FileDropEffect.Copy : shift || paths.All(path => PathUtil.SameVolume(path, folder)) ? FileDropEffect.Move : FileDropEffect.Copy;
		if ((allowed & preferred) != 0) { return preferred; }
		return (allowed & FileDropEffect.Move) != 0 ? FileDropEffect.Move : allowed & FileDropEffect.Copy;
	}
}
