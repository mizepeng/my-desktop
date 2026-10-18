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
	("散放改名要求桌面直接子项及 Shell 权限", LooseRenameScope),
	("目录改名同步成员、映射及自定义顺序", RenameReferences),
	("目录改名同步子项使用记录且重复通知不丢失", RenameUsage),
	("外部快捷方式替换不改写分区成员", ShortcutReplacement),
	("嵌套映射合并监视且保留根目录改名通知", NestedFolderWatches),
	("散放图标排除全部标签成员并避开分区", LooseIconArrangement),
	("自由排列沿用 Explorer 坐标和显示器缩放", LooseIconCoordinates),
	("无法完整绘制散放图标时拒绝接管", InvalidDesktopLayout),
	("桌面键盘保护成对处理且放行系统快捷键", DesktopKeyboardProtection),
	("跨分区与散放图标共享选择且隐藏标签不残留", SharedItemSelection),
	("文件操作拒绝系统入口并去重父子项", FileSelectionProtection),
	("桌面剪切只转移归属并保留目标原有顺序", DesktopMembershipTransfer),
	("粘贴拒绝目录自身与子目录目标", InvalidPasteTargets),
	("框选支持反向与负坐标并在缩回时取消临时命中", RectangleSelection),
	("追加框选以开始时的选择为基准且取消可恢复", AdditiveRectangleSelection),
	("拖放区分桌面归属与文件复制移动并拒绝非法目标", FileDropDecisions),
	("拖回桌面保持相对位置并按目标显示器缩放写回", DesktopDropPositions),
	("分区移动吸附按绝对位移与缩放计算", FenceMoveSnapping),
	("分区调整大小保持对边与最小尺寸", FenceResizeMinimum),
	("分区扩展止于邻区且不修正原有重叠", FenceResizeNeighbors),
	("分区整行整列吸附独立于边缘吸附", FenceResizeGrid),
	("分区重叠寻找最近空位且放不下时返回无位置", FenceFreeSpot),
	("调整分区尺寸同步标签组并拒绝无效值", FenceResizeGroup),
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

static void FenceMoveSnapping()
{
	var work = new System.Drawing.Rectangle(-1200, 0, 1200, 900);
	var start = new System.Drawing.Rectangle(-1000, 100, 240, 180);
	var settings = new AppSettings();
	Require(FenceGeometry.Move(start, new(-195, 0), work, [], settings, 1.5).Left == -1200, "负坐标屏幕边缘没有吸附。");
	Require(FenceGeometry.Move(start, new(-160, 0), work, [], settings, 1.5).Left == -1160, "越过阈值后仍粘在边缘。");
	settings.SnapGap = 8;
	start = new(100, 100, 150, 150);
	work = new(0, 0, 2000, 1200);
	Require(FenceGeometry.Move(start, new(137, 0), work, [new(400, 100, 100, 100)], settings, 1.5).Right == 388, "吸附间距没有随缩放换算。");
	Require(FenceGeometry.Move(start, new(137, 0), work, [new(400, 700, 100, 100)], settings, 1.5).Right == 387, "远处分区延长线不应吸住当前位置。");
	settings.SnapToEdges = false;
	Require(FenceGeometry.Move(start, new(137, 0), work, [new(400, 100, 100, 100)], settings, 1.5).Right == 387, "关闭边缘吸附没有生效。");
}

static void FenceResizeMinimum()
{
	var settings = new AppSettings { SnapToEdges = false, SnapToGrid = false };
	var start = new System.Drawing.Rectangle(100, 100, 300, 200);
	var result = FenceGeometry.Resize(start, new(500, 500), FenceEdges.Left | FenceEdges.Top, new(0, 0, 2000, 1200), [], settings, 1,
		new(120, 36), [], []);
	Require(result.Right == start.Right && result.Bottom == start.Bottom && result.Width == 120 && result.Height == 36, "拖左上角时对边或最小尺寸不正确。");
	Throws<ArgumentException>(() => FenceGeometry.Resize(start, new(1, 1), FenceEdges.Left | FenceEdges.Right, new(0, 0, 2000, 1200), [], settings, 1,
		new(120, 36), [], []));
}

static void FenceResizeNeighbors()
{
	var settings = new AppSettings { SnapToEdges = false, SnapToGrid = false, SnapGap = 8 };
	var start = new System.Drawing.Rectangle(100, 100, 200, 180);
	System.Drawing.Rectangle Resize(FenceEdges edge, System.Drawing.Point delta, System.Drawing.Rectangle other) =>
		FenceGeometry.Resize(start, delta, edge, new(0, 0, 2000, 1200), [other], settings, 1.5, new(180, 54), [], []);
	Require(Resize(FenceEdges.Right, new(400, 0), new(400, 100, 200, 180)).Right == 388, "右边应止于相邻分区并留出间距。");
	Require(Resize(FenceEdges.Right, new(100, 0), new(250, 100, 200, 180)).Right == 400, "不应阻止原本已重叠的分区调整大小。");
	var corner = Resize(FenceEdges.Right | FenceEdges.Bottom, new(200, 100), new(400, 300, 200, 180));
	Require(corner.Right == 500 && corner.Bottom == 288, "同时压住两条边时没有选择退让更少的边。");
}

static void FenceResizeGrid()
{
	var settings = new AppSettings { SnapToEdges = false, SnapToGrid = true };
	var result = FenceGeometry.Resize(new(100, 100, 220, 200), new(15, 9), FenceEdges.Right | FenceEdges.Bottom, new(0, 0, 2000, 1200), [],
		settings, 1, new(120, 36), [240, 336], [210, 300]);
	Require(result.Width == 240 && result.Height == 210 && result.Location == new System.Drawing.Point(100, 100), "整行整列吸附或固定对边不正确。");
}

static void FenceFreeSpot()
{
	var rect = new System.Drawing.Rectangle(450, 150, 200, 200);
	var other = new System.Drawing.Rectangle(400, 100, 300, 300);
	var free = FenceGeometry.FindFreeSpot(rect, new(0, 0, 1000, 800), [other], 10);
	Require(free is { } value && !value.IntersectsWith(other)
		&& Math.Pow(value.Left - rect.Left, 2) + Math.Pow(value.Top - rect.Top, 2) == 67600, "没有选择最近的可用空位。");
	Require(FenceGeometry.FindFreeSpot(rect, new(0, 0, 500, 400), [new(0, 0, 500, 400)], 10) == null, "空间不足时不应返回仍然重叠的位置。");
	var separate = new System.Drawing.Rectangle(710, 150, 200, 200);
	Require(FenceGeometry.FindFreeSpot(separate, new(0, 0, 1000, 800), [other], 10) == separate, "未重叠的分区不应被重新摆放。");
}

static void FenceResizeGroup()
{
	var settings = Fixture();
	var frame = new FenceWorkspace(settings).Frames.First();
	frame.SetBounds(-500, 80, 720, 450, 144);
	Require(frame.Members.All(member => member.X == -500 && member.Y == 80 && member.Width == 720 && member.Height == 450 && member.LayoutDpi == 144),
		"分区尺寸没有同步给同组标签。");
	Throws<ArgumentOutOfRangeException>(() => frame.SetBounds(0, 0, 0, 100, 96));
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
	Require(down.Intercept && down.Command == DesktopInputCommand.Delete, "Delete 没有交给文件删除流程。");
	Require(routing.Route(0x2E, false, false, true, false, false, false, false).Intercept, "切换前台后松开没有与吞下的按下配对。");
	Require(!routing.Route(0x2E, false, false, false, false, false, false, false).Intercept, "没有对应按下时误吞了松开。");
	Require(!routing.Route(0x44, true, true, true, false, false, false, false).Intercept, "Win+D 被拦截。");
	Require(!routing.Route(0x41, true, true, false, true, false, false, true).Intercept, "系统菜单或编辑框中的按键被拦截。");
	Require(!routing.Route(0x2E, true, false, false, false, false, false, false).Intercept, "影响了其他程序中的 Delete。");
	Require(routing.Route(0x41, true, true, false, true, false, false, false).Command == DesktopInputCommand.SelectAll, "全选没有交给 WinUI 图标视图。");
	Require(routing.Route(0x71, true, true, false, false, false, false, false).Command == DesktopInputCommand.Rename, "F2 没有交给 WinUI 改名流程。");
	Require(routing.Route(0x71, false, false, false, false, false, false, false).Intercept, "F2 的松开没有与按下配对。");
	foreach (var (key, command) in new[] { (0x43u, DesktopInputCommand.Copy), (0x58u, DesktopInputCommand.Cut), (0x56u, DesktopInputCommand.Paste) })
	{
		Require(routing.Route(key, true, true, false, true, false, false, false).Command == command, "剪贴板快捷键没有交给文件命令。");
		Require(routing.Route(key, false, false, false, false, false, false, false).Intercept, "剪贴板快捷键没有成对处理。");
	}
	Require(routing.Route(0x2E, true, true, false, false, true, false, false).Command == DesktopInputCommand.Unsupported, "永久删除被意外放行。");
}

static void LooseRenameScope()
{
	string folder = Path.Combine(AppPaths.DataDir, "模拟桌面");
	var entry = new DesktopEntry(Path.Combine(folder, "文件.txt"), "文件", true, false, default);
	Require(ItemRename.CanRenameDesktop(entry, [folder]), "普通桌面文件被拒绝改名。");
	Require(!ItemRename.CanRenameDesktop(entry with { IsFileSystem = false }, [folder]), "系统入口被当作普通文件改名。");
	Require(!ItemRename.CanRenameDesktop(entry with { CanRename = false }, [folder]), "忽略了 Shell 不允许改名的标记。");
	Require(!ItemRename.CanRenameDesktop(entry with { Key = folder }, [folder]), "允许把桌面目录本身改名。");
	Require(!ItemRename.CanRenameDesktop(entry with { Key = Path.Combine(folder, "子目录", "文件.txt") }, [folder]), "允许改名非桌面直接子项。");
	Require(!ItemRename.CanRenameDesktop(entry with { Key = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" }, [folder]), "允许按文件路径改名 CLSID 入口。");
}

static void RenameUsage()
{
	string oldFolder = Path.Combine(AppPaths.DataDir, "旧目录");
	string newFolder = Path.Combine(AppPaths.DataDir, "新目录");
	string child = Path.Combine(oldFolder, "子项.txt");
	UsageStats.Add(oldFolder);
	UsageStats.Add(child);
	UsageStats.Rename(oldFolder, newFolder, true);
	Require(UsageStats.Score(oldFolder) == 0 && UsageStats.Score(child) == 0, "旧路径仍保留使用记录。");
	string newChild = Path.Combine(newFolder, "子项.txt");
	Require(UsageStats.Score(newFolder) > 0 && UsageStats.Score(newChild) > 0, "目录或子项的使用记录没有同步。");
	UsageStats.Rename(oldFolder, newFolder, true);
	Require(UsageStats.Score(newFolder) > 0 && UsageStats.Score(newChild) > 0, "重复通知清空了新路径的使用记录。");
}

static void ShortcutReplacement()
{
	Require(!ItemRename.ShouldFollowExternalRename(@"C:\桌面\应用.lnk", @"C:\桌面\应用.lnk.bak"), "快捷方式更新时误把成员改成备份路径。");
	Require(ItemRename.ShouldFollowExternalRename(@"C:\桌面\应用.lnk", @"C:\桌面\新应用.lnk"), "正常的快捷方式改名被忽略。");
	Require(ItemRename.ShouldFollowExternalRename(@"C:\桌面\资料.txt", @"C:\桌面\资料.bak"), "普通文件改扩展名被当成快捷方式替换。");
}

static void NestedFolderWatches()
{
	string desktop = Path.Combine(AppPaths.DataDir, "桌面");
	string portal = Path.Combine(AppPaths.DataDir, "映射");
	string child = Path.Combine(portal, "子目录");
	var watches = FolderWatch.Build([desktop, desktop.ToUpperInvariant()], [portal, child]);
	Require(watches.Single(watch => PathUtil.AreEqual(watch.Folder, portal)).Recursive, "嵌套映射没有通过父目录递归监视。");
	Require(!watches.Any(watch => PathUtil.AreEqual(watch.Folder, child)), "仍然为子目录持有阻止父目录改名的独立句柄。");
	Require(watches.Count(watch => PathUtil.AreEqual(watch.Folder, desktop)) == 1 && !watches.Single(watch => PathUtil.AreEqual(watch.Folder, desktop)).Recursive,
		"没有嵌套需求的桌面目录被重复或递归监视。");
	Require(watches.Any(watch => PathUtil.AreEqual(watch.Folder, AppPaths.DataDir) && !watch.Recursive), "缺少对映射根目录改名的父目录监视。");
	Require(FolderWatch.Build([], [@"C:\"]).Single().Folder == @"C:\", "盘符根目录被改成相对当前目录的路径。");
}

static void SharedItemSelection()
{
	var selected = new ItemSelection();
	var first = Guid.NewGuid();
	var second = Guid.NewGuid();
	selected.Set(first, [@"C:\桌面\工作.txt"], false);
	selected.Click(Guid.Empty, @"C:\桌面\散放.txt", true);
	Require(selected.Keys.Count == 2, "追加散放选择清掉了分区选择。");
	selected.Set(second, [@"C:\资料\映射.txt"], true);
	Require(selected.Keys.Count == 3, "追加另一个分区选择清掉了其他区域。");
	selected.Remove(first);
	Require(selected.Keys.Count == 2 && selected.InScope(first).Count == 0, "切换标签后保留了隐藏标签的选择。");
	selected.Click(Guid.Empty, @"C:\桌面\散放.txt", true);
	Require(selected.Keys.Single() == @"C:\资料\映射.txt", "追加点击未取消原来的散放选择。");
	selected.Set(second, [@"C:\资料\映射.txt"], false);
	selected.Rename(@"C:\资料", @"C:\新资料", true);
	Require(selected.Keys.Single() == @"C:\新资料\映射.txt", "目录改名没有同步选择。");
	selected.Retain(second, []);
	Require(selected.Keys.Count == 0, "文件消失后仍被选中。");
}

static void FileSelectionProtection()
{
	Throws<InvalidOperationException>(() => FileOperationRules.ValidateSelection([new SelectedFile(AppPaths.Desktop, false, true)], true));
	Throws<InvalidOperationException>(() => FileOperationRules.ValidateSelection([new SelectedFile(@"C:\桌面\受保护.txt", true, false)], true));
	var paths = FileOperationRules.ValidateSelection([new SelectedFile(@"C:\桌面\目录", true, true),
		new SelectedFile(@"C:\桌面\目录\子项.txt", true, true), new SelectedFile(@"c:\桌面\目录", true, true)], true);
	Require(paths.Length == 1 && paths[0] == @"C:\桌面\目录", "父子项或大小写重复被执行了多次。");
	Require(!FileOperationRules.IsDesktopFile(AppPaths.Desktop, [AppPaths.Desktop]), "把桌面目录本身当成了直接子项。");
}

static void DesktopMembershipTransfer()
{
	var settings = Fixture();
	var first = settings.Fences[0];
	var target = settings.Fences[2];
	string key = first.Members[0];
	first.CustomOrder.Add(key);
	target.Members.Add(@"C:\Desktop\原有.txt");
	target.CustomOrder.Add(@"C:\Desktop\原有.txt");
	FileOperationRules.Assign(settings, [key], target);
	Require(!first.Members.Contains(key) && !first.CustomOrder.Contains(key) && target.Members.Last() == key, "没有把归属从源分区移到目标。");
	FileOperationRules.Assign(settings, [@"C:\Desktop\原有.txt"], target);
	Require(target.Members[0] == @"C:\Desktop\原有.txt" && target.CustomOrder.Single() == @"C:\Desktop\原有.txt", "粘贴到原分区改变了既有顺序。");
	FileOperationRules.Assign(settings, [key], null);
	Require(settings.Fences.All(fence => !fence.Members.Contains(key)), "移回散放区仍留着分区归属。");
	Throws<ArgumentException>(() => FileOperationRules.Assign(settings, [key], settings.Fences[1]));
}

static void InvalidPasteTargets()
{
	Throws<InvalidOperationException>(() => FileOperationRules.ValidatePaste([@"C:\资料"], @"C:\资料"));
	Throws<InvalidOperationException>(() => FileOperationRules.ValidatePaste([@"C:\资料"], @"C:\资料\子目录"));
	Throws<InvalidOperationException>(() => FileOperationRules.ValidatePaste(["::{CLSID}"], @"C:\资料"));
	FileOperationRules.ValidatePaste([@"C:\资料\文件.txt"], @"C:\桌面");
}

static void RectangleSelection()
{
	var selected = new ItemSelection();
	var scope = Guid.NewGuid();
	SelectionTarget[] targets = [new(scope, "左侧", new(-200, 20, 30, 30)), new(scope, "右侧", new(20, 20, 30, 30))];
	var band = new SelectionBand(selected, new(80, 80), targets, false);
	band.Update(new(-250, 0));
	Require(selected.Keys.Count == 2 && band.Bounds.Left == -250, "反向或跨显示器负坐标框选错误。");
	band.Update(new(60, 60));
	Require(selected.Keys.Count == 0, "缩回选框后仍保留暂时命中的项目。");
	band.Update(new(80, 80));
	Require(selected.Keys.Count == 0, "零面积框选命中了项目。");
}

static void AdditiveRectangleSelection()
{
	var selected = new ItemSelection();
	var scope = Guid.NewGuid();
	selected.Set(scope, ["原分区项目"], false);
	selected.Click(Guid.Empty, "原散放项目", true);
	var band = new SelectionBand(selected, new(0, 0), [new(Guid.Empty, "新项目", new(20, 20, 30, 30))], true);
	band.Update(new(70, 70));
	Require(selected.Keys.Count == 3, "追加框选丢失其他区域的选择。");
	band.Update(new(10, 10));
	Require(selected.Keys.Count == 2, "追加框选没有按起始选择重算。");
	band.Cancel();
	Require(selected.InScope(scope).Single() == "原分区项目" && selected.InScope(Guid.Empty).Single() == "原散放项目", "取消框选没有恢复原选择。");
}

static void FileDropDecisions()
{
	var all = FileDropEffect.Copy | FileDropEffect.Move;
	Require(FileDropRules.Choose([@"C:\桌面\文件.txt"], @"C:\桌面", true, all, true, false, false) == FileDropEffect.Move, "桌面拖放没有保持只转移归属。");
	Require(FileDropRules.Choose([@"C:\来源\文件.txt"], @"C:\目标", false, all, false, false, false) == FileDropEffect.Move, "同盘默认没有移动。");
	Require(FileDropRules.Choose([@"C:\来源\文件.txt"], @"D:\目标", false, all, false, false, false) == FileDropEffect.Copy, "跨盘默认没有复制。");
	Require(FileDropRules.Choose([@"C:\来源\文件.txt"], @"C:\目标", false, all, true, false, false) == FileDropEffect.Copy, "Ctrl 没有选择复制。");
	Require(FileDropRules.Choose([@"C:\来源\文件.txt"], @"D:\目标", false, all, false, true, false) == FileDropEffect.Move, "Shift 没有选择移动。");
	Require(FileDropRules.Choose([@"C:\目录"], @"C:\目录\子目录", false, all, false, false, false) == FileDropEffect.None, "允许目录拖进自身。");
	Require(FileDropRules.Choose([@"C:\来源\文件.txt"], @"C:\目标", false, all, false, false, true) == FileDropEffect.None, "未实现快捷方式却回报了成功动作。");
	Require(FileDropRules.Choose([@"C:\目录\文件.txt"], @"C:\目录", false, all, false, false, false) == FileDropEffect.None, "映射目录原地拖动意外移动了文件。");
}

static void DesktopDropPositions()
{
	var snapshot = new DesktopSnapshot(false, new(100, 100), 48, new(-1920, 0, 3840, 1080), []);
	DesktopMonitor[] monitors = [new(new(-1920, 0, 1920, 1080), 1), new(new(0, 0, 1920, 1080), 1.5)];
	var cells = new Dictionary<string, System.Drawing.Rectangle> { ["一"] = new(-1800, 100, 100, 100), ["二"] = new(-1800, 200, 100, 100) };
	var moved = DesktopLayout.DropPositions(snapshot, ["一", "二"], new(0, 0), new(50, 25), cells, monitors);
	Require(moved[0] == new System.Drawing.Point(196, 123) && moved[1] == new System.Drawing.Point(196, 223), "多选拖动没有保持格子之间的相对位置。");
	var placed = DesktopLayout.DropPositions(snapshot, ["一", "二"], new(150, 350), null, cells, monitors);
	Require(placed[0] == new System.Drawing.Point(2034, 322) && placed[1] == new System.Drawing.Point(2034, 422), "跨屏放置没有采用目标显示器的缩放及列表原点。");
	var arranged = new DesktopSnapshot(true, snapshot.Spacing, snapshot.IconSize, snapshot.ListBounds, []);
	Require(DesktopLayout.DropPositions(arranged, ["一"], new(150, 350), null, cells, monitors).Single() == new System.Drawing.Point(2020, 325),
		"自动排列错误套用了自由排列的图像偏移。");
}
