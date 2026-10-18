namespace MyDesktop.Core;

internal static class PathUtil
{
	static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL",
		"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
	};

	public static string Normalize(string path)
	{
		try
		{
			return Path.GetFullPath(path).TrimEnd('\\', '/');
		}
		catch (Exception)
		{
			return path.TrimEnd('\\', '/');
		}
	}

	public static bool AreEqual(string? a, string? b)
	{
		return a != null && b != null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// path 是否位于 folder 之内（不含 folder 本身）。
	/// </summary>
	public static bool IsUnder(string path, string folder)
	{
		return Normalize(path).StartsWith(Normalize(folder) + "\\", StringComparison.OrdinalIgnoreCase);
	}

	public static bool SameVolume(string a, string b)
	{
		return string.Equals(Path.GetPathRoot(Normalize(a)), Path.GetPathRoot(Normalize(b)), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 把任意文本转成合法的文件夹名：替换非法字符，避开系统保留名。
	/// </summary>
	public static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
		if (result.Length == 0)
		{
			return "分区";
		}
		return ReservedNames.Contains(result) ? result + "_" : result;
	}

	public static void ValidateFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
		{
			throw new ArgumentException("名称不能为空、包含非法字符，或以空格、句点结尾。");
		}
		string stem = name.Split('.')[0].TrimEnd(' ');
		if (ReservedNames.Contains(stem) || stem.ToUpperInvariant() is "COM¹" or "COM²" or "COM³" or "LPT¹" or "LPT²" or "LPT³")
		{
			throw new ArgumentException("此名称由 Windows 保留，请换一个名称。");
		}
	}

	/// <summary>
	/// 在 folder 中生成不冲突的路径，冲突时依次追加 " (2)"、" (3)"。
	/// </summary>
	public static string UniquePath(string folder, string name, string extension = "")
	{
		var path = Path.Combine(folder, name + extension);
		for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
		{
			path = Path.Combine(folder, $"{name} ({i}){extension}");
		}
		return path;
	}
}
