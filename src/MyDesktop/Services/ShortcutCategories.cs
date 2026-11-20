using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 快捷方式分类表：数据目录的 shortcut-categories.json，类别 → 目标程序列表，用户可以直接编辑。
/// 文件不存在时写入内置的分类表（OrganizeRule.DefaultShortcutCategories）；按修改时间缓存，改完保存后下次整理就生效。
/// </summary>
internal static class ShortcutCategories
{
	static readonly JsonSerializerOptions ReadOptions = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	static Dictionary<string, List<string>> _table = new(StringComparer.OrdinalIgnoreCase);

	static DateTime? _loadedTime;

	public static string FilePath => Path.Combine(AppPaths.DataDir, "shortcut-categories.json");

	/// <summary>
	/// 上次读分类表出的错（文件格式不对等），读成功后清空；出错时按类别的规则都匹配不到，一键整理前据此提示用户。
	/// </summary>
	public static string? LastError { get; private set; }

	/// <summary>
	/// 某个类别的程序列表，没有这个类别（或分类表读不出来）时为空。
	/// </summary>
	public static IReadOnlyList<string> Patterns(string category)
	{
		Refresh();
		return _table.TryGetValue(category, out var list) ? list : [];
	}

	/// <summary>
	/// 分类表里的类别名，按文件里的顺序。
	/// </summary>
	public static IReadOnlyList<string> Names()
	{
		Refresh();
		return _table.Keys.ToList();
	}

	/// <summary>
	/// 文件不存在就写入内置的分类表，返回文件路径；给「编辑分类表」用。
	/// </summary>
	public static string EnsureFile()
	{
		var file = FilePath;
		if (!File.Exists(file))
		{
			WriteDefaults(file);
		}
		return file;
	}

	static void Refresh()
	{
		var file = EnsureFile();
		var modified = File.GetLastWriteTimeUtc(file);
		if (_loadedTime == modified)
		{
			return;
		}
		_loadedTime = modified;
		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file), ReadOptions)
					?? throw new JsonException("文件内容为空");
			_table = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (var (name, patterns) in parsed)
			{
				_table[name.Trim()] = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
			}
			LastError = null;
			Log.Info($"已读取快捷方式分类表：{_table.Count} 个类别，{_table.Values.Sum(l => l.Count)} 个程序");
		}
		catch (Exception ex)
		{
			_table = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			LastError = ex.Message;
			Log.Warn($"快捷方式分类表格式有误，按类别的整理规则不生效：{file}", ex);
		}
	}

	static void WriteDefaults(string file)
	{
		var builder = new StringBuilder();
		builder.AppendLine("// MyDesktop 快捷方式分类表：整理规则里的「快捷方式类别」按这里的列表归类快捷方式。");
		builder.AppendLine("// 格式：\"类别名\": [ \"目标程序\", ... ]，可以增删类别和程序，保存后下次整理生效。");
		builder.AppendLine("// 程序写 exe 文件名（如 \"Photoshop.exe\"），可以用 * 和 ? 通配（如 \"gimp*.exe\"）；");
		builder.AppendLine("// 含有 \\ / : 的按完整目标匹配，如 \"D:\\\\Games\\\\*\"、网址快捷方式 \"steam://*\"。不区分大小写。");
		builder.AppendLine("// 新加的类别要在设置的「整理规则」里建一条规则，「快捷方式类别」填类别名才会用上。");
		builder.Append(JsonSerializer.Serialize(OrganizeRule.DefaultShortcutCategories, WriteOptions));
		builder.AppendLine();
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, builder.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
		Log.Info($"已写入内置的快捷方式分类表：{file}");
	}
}
