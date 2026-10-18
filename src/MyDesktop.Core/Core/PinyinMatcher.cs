namespace MyDesktop.Core;

/// <summary>
/// 按名称或拼音匹配：名称里的汉字可以用全拼、首字母或全拼的前几个字母代替，并且可以混着输（jsb、jishib、记事b 都能匹配「记事本」）；
/// 英文、数字按原样比较，不区分大小写；名称里的空格和标点可以跳过。拼音表见 Assets/pinyin.txt，取自 Unicode 汉字数据库。
/// </summary>
internal static class PinyinMatcher
{
	static Dictionary<char, string[]>? _table;

	static Dictionary<char, string[]> Table => _table ??= Load();

	/// <summary>
	/// 去掉空白并转成小写，作为 Rank 的输入。
	/// </summary>
	public static string NormalizeQuery(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

	/// <summary>
	/// 匹配程度，越小越靠前，不匹配时返回 null：0 名称以输入开头，1 名称包含输入，2 从名称开头按拼音匹配，3 从名称中间按拼音匹配。
	/// </summary>
	/// <param name="query">已经过 NormalizeQuery 处理的输入。</param>
	public static int? Rank(string name, string query)
	{
		if (query.Length == 0)
		{
			return null;
		}
		int index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
		if (index >= 0)
		{
			return index == 0 ? 0 : 1;
		}
		var text = name.ToLowerInvariant();
		var memo = new Dictionary<(int, int), bool>();
		for (int start = 0; start < text.Length; start++)
		{
			if (Match(text, start, query, 0, memo))
			{
				return start == 0 ? 2 : 3;
			}
		}
		return null;
	}

	/// <summary>
	/// 从名称第 ci 个字符、输入第 qi 个字符起，能否把剩下的输入匹配完（名称可以有剩余）。
	/// </summary>
	static bool Match(string text, int ci, string query, int qi, Dictionary<(int, int), bool> memo)
	{
		if (qi == query.Length)
		{
			return true;
		}
		if (ci == text.Length)
		{
			return false;
		}
		if (memo.TryGetValue((ci, qi), out bool known))
		{
			return known;
		}
		char c = text[ci];
		bool result = c == query[qi] && Match(text, ci + 1, query, qi + 1, memo);
		if (!result && Table.TryGetValue(c, out var syllables))
		{
			foreach (var syllable in syllables)
			{
				// 全拼的前 1～n 个字母都可以代表这个字，先试最长的
				int n = 0;
				while (n < syllable.Length && qi + n < query.Length && SameLetter(syllable[n], query[qi + n]))
				{
					n++;
				}
				for (int k = n; k >= 1 && !result; k--)
				{
					result = Match(text, ci + 1, query, qi + k, memo);
				}
				if (result)
				{
					break;
				}
			}
		}
		// 名称里的空格、标点可以跳过：「旅行 vlog」也能用 lxvlog 搜到
		if (!result && !char.IsLetterOrDigit(c))
		{
			result = Match(text, ci + 1, query, qi, memo);
		}
		memo[(ci, qi)] = result;
		return result;
	}

	/// <summary>
	/// 拼音表里 ü 写作 v，输入 u 也算对上（绿：lv、lu 都行）。
	/// </summary>
	static bool SameLetter(char pinyin, char typed) => pinyin == typed || (pinyin == 'v' && typed == 'u');

	static Dictionary<char, string[]> Load()
	{
		using var stream = typeof(PinyinMatcher).Assembly.GetManifestResourceStream("MyDesktop.pinyin.txt")
				?? throw new InvalidOperationException("程序里缺少拼音表资源");
		using var reader = new StreamReader(stream);
		var lists = new Dictionary<char, List<string>>();
		while (reader.ReadLine() is { } line)
		{
			int space = line.IndexOf(' ');
			if (line.StartsWith('#') || space <= 0)
			{
				continue;
			}
			var syllable = line[..space];
			foreach (char c in line.AsSpan(space + 1))
			{
				if (!lists.TryGetValue(c, out var list))
				{
					lists[c] = list = [];
				}
				list.Add(syllable);
			}
		}
		return lists.ToDictionary(p => p.Key, p => p.Value.ToArray());
	}
}
