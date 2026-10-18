using System.Text.Json;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;

var root = Path.GetFullPath(args.Length == 1 ? args[0] : Path.Combine("artifacts", "verification", "core", Guid.NewGuid().ToString("N")));
AppPaths.UseDataDir(root);
Directory.CreateDirectory(root);
Log.Init(root);
var checks = new (string Name, Action Run)[]
{
	("配置原子保存及上一版恢复", SettingsRoundTrip),
	("旧版毛玻璃字段只读迁移", LegacyBackground),
	("导入配置拒绝无效结构", InvalidImport),
	("标签组单框架与内容独立", SingleFramePerGroup),
	("分组布局同步且布局字典不共享", GroupFrameSynchronization),
	("失效标签组沿用旧版规范化规则", NormalizeGroups),
	("非法标签和 DPI 显式失败", InvalidFrameChanges),
	("拼音资源随核心程序集迁移", PinyinResource),
	("整理规则优先级及兜底语义不变", OrganizeRules),
	("核心程序集不依赖 WPF 或 WinUI", FrameworkIndependence),
};
int failures = 0;
foreach (var check in checks)
{
	try
	{
		check.Run();
		Console.WriteLine($"通过：{check.Name}");
	}
	catch (Exception ex)
	{
		failures++;
		Console.Error.WriteLine($"失败：{check.Name}{Environment.NewLine}{ex}");
	}
}
Console.WriteLine($"共 {checks.Length} 项，通过 {checks.Length - failures} 项。测试数据：{root}");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

static void Throws<T>(Action operation) where T : Exception
{
	try
	{
		operation();
	}
	catch (T)
	{
		return;
	}
	throw new InvalidOperationException($"预期抛出 {typeof(T).Name}。");
}

static AppSettings Fixture()
{
	var first = new FenceSettings { Title = "工作", X = 100, Y = 150, Width = 600, Height = 420, LayoutDpi = 144, Members = [@"C:\Desktop\工作.txt"] };
	var second = new FenceSettings { Title = "资料", IsPortal = true, FolderPath = @"C:\资料", Color = "#123456" };
	var standalone = new FenceSettings { Title = "其他", X = 900, Y = 150, Width = 500, Height = 420 };
	return new AppSettings
	{
		Fences = [first, second, standalone],
		Groups = [new FenceGroup { Members = [first.Id, second.Id], Active = first.Id }],
	};
}

static void SettingsRoundTrip()
{
	var settings = Fixture();
	SettingsStore.Save(settings);
	settings.Fences[0].Title = "工作（已更新）";
	SettingsStore.Save(settings);
	var loaded = SettingsStore.Load(out bool firstRun);
	Require(!firstRun && loaded.Fences[0].Title == "工作（已更新）", "当前配置没有完整保存。");
	Require(loaded.Groups[0].Members.SequenceEqual(settings.Groups[0].Members), "标签顺序发生变化。");
	File.WriteAllText(AppPaths.SettingsFile, "{损坏配置");
	var restored = SettingsStore.Load(out _);
	Require(restored.Fences[0].Title == "工作", "没有恢复上一版原子保存的配置。");
	Require(SettingsStore.ReadFrom(AppPaths.SettingsFile).Fences[0].Title == "工作", "恢复没有写回主配置。");
	Require(!File.Exists(AppPaths.SettingsFile + ".tmp"), "临时配置未完成替换。");
}

static void LegacyBackground()
{
	string file = Path.Combine(AppPaths.DataDir, "legacy.json");
	File.WriteAllText(file, """{"DefaultBlur":false,"Fences":[{"Title":"旧配置","Blur":true}]}""");
	var settings = SettingsStore.ReadFrom(file);
	Require(settings.DefaultBackground == BackgroundEffect.Translucent && settings.Fences[0].Background == BackgroundEffect.Blur, "旧版字段转换错误。");
	SettingsStore.SaveTo(settings, file);
	using var saved = JsonDocument.Parse(File.ReadAllText(file));
	Require(!saved.RootElement.TryGetProperty("DefaultBlur", out _) && !saved.RootElement.GetProperty("Fences")[0].TryGetProperty("Blur", out _), "遗留字段被重新写出。");
}

static void InvalidImport()
{
	string file = Path.Combine(AppPaths.DataDir, "invalid.json");
	foreach (string text in new[] { "null", "[]", "{}", "{\"Fences\":\"wrong\"}" })
	{
		File.WriteAllText(file, text);
		Throws<InvalidDataException>(() => SettingsStore.ReadFrom(file));
	}
}

static void SingleFramePerGroup()
{
	var settings = Fixture();
	var workspace = new FenceWorkspace(settings);
	Require(workspace.Frames.Count == 2, "两个标签被错误地投影成两个框架。");
	var frame = workspace.Frames[0];
	var members = frame.Members;
	frame.Activate(settings.Fences[1].Id);
	Require(ReferenceEquals(frame, workspace.Frames[0]) && ReferenceEquals(members, frame.Members), "切换标签重新创建了框架。");
	Require(frame.Active.IsPortal && frame.Active.FolderPath == @"C:\资料", "切换标签后没有保留映射路径。");
	Require(settings.Fences[0].Members.Single() == @"C:\Desktop\工作.txt", "切换标签覆盖了其他标签的成员。");
	Require(settings.Groups[0].Active == settings.Fences[1].Id, "当前标签没有写入配置模型。");
}

static void GroupFrameSynchronization()
{
	var settings = Fixture();
	settings.Fences[0].BoundsByDpi[96] = new FenceBounds(10, 20, 400, 300);
	var frame = new FenceWorkspace(settings).Frames[0];
	frame.MoveTo(-400, 250, 192);
	frame.SetRolledUp(true);
	Require(frame.Members.All(member => member.X == -400 && member.Y == 250 && member.LayoutDpi == 192 && member.RolledUp), "组内布局或卷起状态不同步。");
	frame.Members[0].BoundsByDpi[96] = new FenceBounds(50, 60, 800, 600);
	Require(frame.Members[1].BoundsByDpi[96].X == 10, "组内布局字典共用了可变实例。");
	Require(settings.Fences[2].X == 900 && !settings.Fences[2].RolledUp, "组外分区被修改。");
}

static void NormalizeGroups()
{
	var settings = Fixture();
	settings.Groups[0].Members.Insert(0, Guid.NewGuid());
	settings.Groups[0].Members.Add(settings.Fences[0].Id);
	settings.Groups[0].Active = Guid.NewGuid();
	settings.Groups.Add(new FenceGroup { Members = [settings.Fences[2].Id], Active = settings.Fences[2].Id });
	Require(FenceGroups.Normalize(settings), "失效配置没有被标记为发生修复。");
	Require(settings.Groups.Count == 1 && settings.Groups[0].Members.Count == 2, "失效成员、重复成员或单成员组没有按旧规则处理。");
	Require(settings.Groups[0].Active == settings.Fences[0].Id, "无效活动标签没有恢复成组内第一个标签。");
}

static void InvalidFrameChanges()
{
	var frame = new FenceWorkspace(Fixture()).Frames[0];
	Throws<ArgumentException>(() => frame.Activate(Guid.NewGuid()));
	Throws<ArgumentOutOfRangeException>(() => frame.MoveTo(0, 0, 0));
}

static void PinyinResource()
{
	Require(PinyinMatcher.Rank("记事本", PinyinMatcher.NormalizeQuery("j s b")) == 2, "核心程序集中的拼音资源未能参与搜索。");
	Require(PinyinMatcher.Rank("记事本", "jishib") == 2, "全拼与首字母混输发生变化。");
}

static void OrganizeRules()
{
	var rules = OrganizeRule.CreateDefaults();
	Require(OrganizeRule.Match(rules, "a.PDF", false)?.Name == "文档", "文件扩展名匹配发生变化。");
	Require(OrganizeRule.Match(rules, "a", true)?.Name == "文件夹", "文件夹规则发生变化。");
	Require(OrganizeRule.Match(rules, "a.xyz", false)?.IsFallback == true, "其他项目没有进入兜底规则。");
}

static void FrameworkIndependence()
{
	var names = typeof(AppSettings).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
	Require(!names.Any(name => name is "PresentationFramework" or "PresentationCore" or "WindowsBase" || name!.StartsWith("Microsoft.WinUI")), "核心仍依赖界面框架。");
}
