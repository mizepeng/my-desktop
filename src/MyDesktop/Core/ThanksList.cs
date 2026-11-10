namespace MyDesktop.Core;

/// <summary>
/// 致谢名单：读取打包进程序的仓库 docs/THANKS.md（唯一的名单来源），取出表格里每人的昵称、来源和简述。
/// 表格按表头的列名找列，列的顺序可以调整；格式不对时抛出异常，由调用方记日志并在界面上提示。
/// </summary>
internal static class ThanksList
{
	/// <summary>
	/// 名单里的一个人，界面按 Name、Source、Summary 绑定。
	/// </summary>
	internal sealed record Person(string Name, string Source, string Summary);

	/// <summary>
	/// 从嵌入资源读出全部名单，顺序和 THANKS.md 一致。
	/// </summary>
	public static List<Person> Load()
	{
		using var stream = typeof(ThanksList).Assembly.GetManifestResourceStream("MyDesktop.THANKS.md")
				?? throw new InvalidOperationException("程序里缺少致谢名单资源 THANKS.md");
		using var reader = new StreamReader(stream);
		return Parse(reader.ReadToEnd());
	}

	static List<Person> Parse(string text)
	{
		var rows = text.Split('\n')
				.Select(line => line.Trim())
				.Where(line => line.StartsWith('|'))
				.ToList();
		if (rows.Count < 3)
		{
			throw new FormatException("THANKS.md 里没有找到名单表格");
		}
		var header = Cells(rows[0]);
		int nameColumn = Column(header, "昵称");
		int sourceColumn = Column(header, "来自");
		int summaryColumn = Column(header, "简述");
		var people = new List<Person>();
		// 第二行是表头下面的分隔行（| --- | --- |）
		for (int i = 2; i < rows.Count; i++)
		{
			var cells = Cells(rows[i]);
			if (cells.Length != header.Length)
			{
				throw new FormatException($"THANKS.md 名单第 {i - 1} 行有 {cells.Length} 列，表头是 {header.Length} 列：{rows[i]}");
			}
			var person = new Person(cells[nameColumn], cells[sourceColumn], cells[summaryColumn]);
			if (person.Name.Length == 0 || person.Source.Length == 0 || person.Summary.Length == 0)
			{
				throw new FormatException($"THANKS.md 名单第 {i - 1} 行的昵称、来自或简述是空的：{rows[i]}");
			}
			people.Add(person);
		}
		if (people.Count == 0)
		{
			throw new FormatException("THANKS.md 的名单表格里没有人");
		}
		return people;
	}

	/// <summary>
	/// 拆出一行表格的各个单元格，去掉两头的竖线和空白。
	/// </summary>
	static string[] Cells(string row) => row.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

	static int Column(string[] header, string name)
	{
		int index = Array.IndexOf(header, name);
		if (index < 0)
		{
			throw new FormatException($"THANKS.md 名单表头里没有「{name}」这一列");
		}
		return index;
	}
}
