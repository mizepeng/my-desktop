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
	("改名保留隐藏扩展名且拒绝非法文件名", RenameNames),
	("改名严格区分普通子项和系统入口", RenameScope),
	("目录改名同步成员、映射及自定义顺序", RenameReferences),
	("散放图标排除全部标签成员并避开分区", LooseIconArrangement),
	("自由排列沿用 Explorer 坐标和显示器缩放", LooseIconCoordinates),
	("无法完整绘制散放图标时拒绝接管", InvalidDesktopLayout),
	("桌面键盘保护成对处理且放行系统快捷键", DesktopKeyboardProtection),
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

static void RenameNames()
{
	const string path = @"C:\资料\原名.txt";
	Require(ItemRename.TargetPath(path, "原名", "新名", false) == @"C:\资料\新名.txt", "隐藏扩展名没有保留。");
	Require(ItemRename.TargetPath(path, "原名.txt", "新名.md", false) == @"C:\资料\新名.md", "显示扩展名时无法显式修改扩展名。");
	Require(ItemRename.TargetPath(path, "原名", "目录", true) == @"C:\资料\目录", "目录被错误追加扩展名。");
	Require(ItemRename.TargetPath(path, "原名", ".资料", false) == @"C:\资料\.资料.txt", "合法的点开头名称被拒绝。");
	foreach (string name in new[] { "", " ", ".", "..", "a.", "a ", "a/b", "a\\b", "a:b", "a?b", "a\nb", "NUL.txt", "CON", "COM1.pdf", "LPT²", "CON .txt" })
	{
		Throws<ArgumentException>(() => ItemRename.TargetPath(path, "原名", name, false));
	}
}

static void RenameScope()
{
	var portal = new FenceSettings { IsPortal = true, FolderPath = @"C:\资料" };
	Require(ItemRename.CanRename(portal, @"C:\资料\文件.txt"), "映射目录的直接子项不能改名。");
	foreach (string path in new[] { @"C:\资料", @"C:\资料\下级\文件.txt", @"C:\资料相邻\文件.txt", @"C:\Users\Max", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "文件.txt" })
	{
		Require(!ItemRename.CanRename(portal, path), "误将目录本身、跨目录项目或系统入口当作普通子项。");
	}
	string desktopFile = Path.Combine(AppPaths.Desktop, "文件.txt");
	var desktop = new FenceSettings { Members = [desktopFile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] };
	Require(ItemRename.CanRename(desktop, desktopFile), "已归入分区的桌面文件不能改名。");
	Require(!ItemRename.CanRename(desktop, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "用户文件夹入口会被当作普通目录改名。");
	Require(!ItemRename.CanRename(desktop, Path.Combine(AppPaths.Desktop, "未归入.txt")), "不在此分区的文件可以从此分区改名。");
}

static void RenameReferences()
{
	var settings = Fixture();
	settings.Fences[0].Members = [@"C:\资料", @"C:\资料\子目录\文件.txt", @"C:\资料相邻\文件.txt", "::{CLSID}"];
	settings.Fences[0].CustomOrder = [@"C:\资料\子目录\文件.txt"];
	settings.Fences[2].IsPortal = true;
	settings.Fences[2].FolderPath = @"C:\资料\子目录";
	ItemRename.UpdateReferences(settings, @"C:\资料", @"C:\新资料", true);
	Require(settings.Fences[0].Members.SequenceEqual(new[] { @"C:\新资料", @"C:\新资料\子目录\文件.txt", @"C:\资料相邻\文件.txt", "::{CLSID}" }), "目录前缀替换误伤其他目录或漏掉子项。");
	Require(settings.Fences[0].CustomOrder.Single() == @"C:\新资料\子目录\文件.txt", "排序路径没有更新。");
	Require(settings.Fences[1].FolderPath == @"C:\新资料" && settings.Fences[2].FolderPath == @"C:\新资料\子目录", "直接映射或子目录映射没有更新。");
	ItemRename.UpdateReferences(settings, @"C:\新资料\子目录\文件.txt", @"C:\新资料\子目录\文件.TXT", false);
	Require(settings.Fences[0].Members[1].EndsWith("文件.TXT", StringComparison.Ordinal), "仅修改大小写时引用未更新。");
}

static DesktopSnapshot DesktopFixture(bool autoArrange = true)
{
	return new DesktopSnapshot(autoArrange, new System.Drawing.Point(100, 100), 48, new System.Drawing.Rectangle(0, 0, 1000, 800),
	[
		new DesktopEntry("a", "甲", true, false, new System.Drawing.Point(0, 0)),
		new DesktopEntry("b", "乙", true, false, new System.Drawing.Point(0, 100)),
		new DesktopEntry("c", "丙", true, false, new System.Drawing.Point(0, 200)),
	]);
}

static void LooseIconArrangement()
{
	var snapshot = DesktopFixture();
	var fences = new[] { new FenceSettings { Members = ["A"] }, new FenceSettings { IsPortal = true, Members = ["b"] } };
	var monitors = new[] { new DesktopMonitor(snapshot.ListBounds, 1) };
	var positions = DesktopLayout.Build(snapshot, fences, [new System.Drawing.Rectangle(0, 0, 100, 200)], monitors);
	Require(positions.Select(item => item.Entry.Key).SequenceEqual(new[] { "b", "c" }), "隐藏标签的成员未排除，或映射分区错误地认领了桌面项目。");
	Require(positions[0].Bounds.Top == 200 && positions[1].Bounds.Top == 300, "自动排列没有压紧并跳过分区。");
	Require(positions.All(item => item.Bounds.Size == new System.Drawing.Size(100, 100)), "未沿用 Explorer 的格子间距。");
}

static void LooseIconCoordinates()
{
	var snapshot = new DesktopSnapshot(false, new System.Drawing.Point(100, 100), 48, new System.Drawing.Rectangle(-1920, 0, 3840, 1080),
		[new DesktopEntry("file", "文件", true, false, new System.Drawing.Point(100, 10))]);
	var monitors = new[] { new DesktopMonitor(new System.Drawing.Rectangle(-1920, 0, 1920, 1080), 1.5),
		new DesktopMonitor(new System.Drawing.Rectangle(0, 0, 1920, 1080), 1) };
	var placement = DesktopLayout.Build(snapshot, [], [], monitors).Single();
	Require(placement.Bounds.Left == -1834 && placement.Bounds.Top == 13, "自由排列没有按所在显示器缩放换算图标与格子偏移。");
}

static void InvalidDesktopLayout()
{
	var snapshot = DesktopFixture();
	var monitors = new[] { new DesktopMonitor(snapshot.ListBounds, 1) };
	Throws<InvalidOperationException>(() => DesktopLayout.Build(snapshot, [], [snapshot.ListBounds], monitors));
	Throws<InvalidDataException>(() => DesktopLayout.Build(snapshot, [], [], []));
	var invalid = new DesktopSnapshot(true, System.Drawing.Point.Empty, 48, snapshot.ListBounds, snapshot.Items);
	Throws<InvalidDataException>(() => DesktopLayout.Build(invalid, [], [], monitors));
	var missingBounds = new DesktopSnapshot(true, snapshot.Spacing, 48, System.Drawing.Rectangle.Empty, snapshot.Items);
	Throws<InvalidDataException>(() => DesktopLayout.Build(missingBounds, [], [], monitors));
}

static void DesktopKeyboardProtection()
{
	var routing = new DesktopKeyRouting();
	var down = routing.Route(0x2E, true, true, false, false, false, false, false);
	Require(down.Intercept && down.Command == DesktopInputCommand.Unsupported, "Delete 会落到隐藏的系统图标上。");
	Require(routing.Route(0x2E, false, false, true, false, false, false, false).Intercept, "切换前台后松开没有与吞下的按下配对。");
	Require(!routing.Route(0x2E, false, false, false, false, false, false, false).Intercept, "没有对应按下时误吞了松开。");
	Require(!routing.Route(0x44, true, true, true, false, false, false, false).Intercept, "Win+D 被拦截。");
	Require(!routing.Route(0x41, true, true, false, true, false, false, true).Intercept, "系统菜单或编辑框中的按键被拦截。");
	Require(!routing.Route(0x2E, true, false, false, false, false, false, false).Intercept, "影响了其他程序中的 Delete。");
	Require(routing.Route(0x41, true, true, false, true, false, false, false).Command == DesktopInputCommand.SelectAll, "全选没有交给 WinUI 图标视图。");
}
