using System.Text.Encodings.Web;
using System.Text.Json;
using MyDesktop.Core;

namespace MyDesktop.Services;

/// <summary>
/// 记录桌面图标的打开情况（分区里和桌面上双击、回车，搜索框里打开），给搜索框排「常用」：
/// 每打开一次加 1 分，分数按 14 天的半衰期随时间衰减，用得多、用得近的排在前面。
/// 存在数据目录的 usage.json，与配置分开，备份、导出配置时不带它。
/// </summary>
internal static class UsageStats
{
	/// <summary>
	/// 分数衰减到这以下的记录在保存时丢掉（打开过一次、之后两个月左右没再打开）。
	/// </summary>
	const double MinScore = 0.05;

	const int MaxEntries = 500;

	static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

	static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>
	/// 分数 Score 是 Updated 那一刻的值，用的时候再按经过的时间衰减。
	/// </summary>
	sealed class Record
	{
		public double Score { get; set; }

		public DateTime Updated { get; set; }
	}

	static Dictionary<string, Record>? _records;

	static string FilePath => Path.Combine(AppPaths.DataDir, "usage.json");

	static Dictionary<string, Record> Records => _records ??= Load();

	/// <summary>
	/// 记一次打开。key 与 FenceItem.FullPath 相同（文件为完整路径，系统图标为 ::{CLSID}）。
	/// </summary>
	public static void Add(string key)
	{
		var now = DateTime.UtcNow;
		if (!Records.TryGetValue(key, out var record))
		{
			record = new Record();
			Records[key] = record;
		}
		record.Score = Decayed(record, now) + 1;
		record.Updated = now;
		Save();
	}

	/// <summary>
	/// 当前分数，从没打开过为 0。
	/// </summary>
	public static double Score(string key) => Records.TryGetValue(key, out var record) ? Decayed(record, DateTime.UtcNow) : 0;

	/// <summary>
	/// 文件改名后记录跟着换成新路径；同一次改名可能从几处各通知一次，旧路径没有记录时什么也不做。
	/// </summary>
	public static void Rename(string oldKey, string newKey)
	{
		if (Records.Remove(oldKey, out var record))
		{
			Records[newKey] = record;
			Save();
		}
	}

	static double Decayed(Record record, DateTime now) => record.Score * Math.Pow(0.5, (now - record.Updated) / HalfLife);

	static Dictionary<string, Record> Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var records = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(FilePath), Options);
				return new Dictionary<string, Record>(records ?? [], StringComparer.OrdinalIgnoreCase);
			}
		}
		catch (Exception ex)
		{
			// 只影响「常用」的排序，从头记起
			Log.Error("读取图标打开记录失败，从头记起", ex);
		}
		return new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 丢掉衰减得差不多的记录、最多留 500 条，先写临时文件再替换。
	/// </summary>
	static void Save()
	{
		var now = DateTime.UtcNow;
		var kept = Records
				.Select(pair => (pair.Key, pair.Value, Score: Decayed(pair.Value, now)))
				.Where(x => x.Score >= MinScore)
				.OrderByDescending(x => x.Score)
				.Take(MaxEntries)
				.ToList();
		if (kept.Count < Records.Count)
		{
			_records = kept.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
		}
		try
		{
			Directory.CreateDirectory(AppPaths.DataDir);
			var temp = FilePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(Records, Options));
			File.Move(temp, FilePath, true);
		}
		catch (Exception ex)
		{
			Log.Error("保存图标打开记录失败", ex);
		}
	}
}
