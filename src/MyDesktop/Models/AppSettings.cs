using System.IO.Enumeration;
using System.Text.Json.Serialization;

namespace MyDesktop.Models;

public enum SortField
{
	Name,
	Type,
	Size,
	Modified,
	/// <summary>
	/// 用户拖动图标调整的顺序。
	/// </summary>
	Custom,
}

/// <summary>
/// 角标处理程序（ShellIconOverlayIdentifiers）的角标怎么取。
/// </summary>
public enum OverlayMode
{
	/// <summary>
	/// 和资源管理器一样，用系统加载的处理程序、按它们报告的优先级取角标。
	/// </summary>
	FollowSystem,
	/// <summary>
	/// 由本程序加载勾选的处理程序，和「系统角标」（Windows 内置的角标）排成一个列表，按列表顺序取第一个认领的。
	/// </summary>
	Custom,
}

public enum IconSizeMode
{
	Small,
	Medium,
	Large,
	ExtraLarge,
}

public enum FenceView
{
	Icons,
	List,
	/// <summary>
	/// 和资源管理器的「详细信息」一样一行一项，名称后面是修改日期、类型、大小。
	/// </summary>
	Details,
}

/// <summary>
/// 分区卷起后收向哪条边，标题栏就在这一侧。
/// </summary>
public enum RollEdge
{
	Top,
	Bottom,
	Left,
	Right,
}

/// <summary>
/// 隐藏/显示的对象。
/// </summary>
public enum HideTarget
{
	All,
	Icons,
	Fences,
}

/// <summary>
/// 程序启动时图标和分区显示还是隐藏。
/// </summary>
public enum StartupVisibility
{
	Show,
	/// <summary>
	/// 按 AppSettings.StartupHideTarget 隐藏。
	/// </summary>
	Hide,
	/// <summary>
	/// 和上次退出时一样（AppSettings.LastHidden）。
	/// </summary>
	Restore,
}

/// <summary>
/// 分区的背景效果。
/// </summary>
public enum BackgroundEffect
{
	/// <summary>
	/// 只有背景颜色。
	/// </summary>
	Translucent,
	/// <summary>
	/// 自己画的毛玻璃：背景颜色下面铺一层模糊后的壁纸（Services/WallpaperBlur）。
	/// </summary>
	Blur,
	/// <summary>
	/// 系统亚克力（实验性，Views/AcrylicBackdrop）：能透出动态壁纸，用的是系统未公开的接口。
	/// </summary>
	Acrylic,
}

/// <summary>
/// 标签不铺满标题栏时靠哪一边排。
/// </summary>
public enum TabAlignment
{
	Left,
	Right,
}

/// <summary>
/// 全局设置与全部分区布局，序列化为数据目录下的 settings.json。
/// </summary>
public sealed class AppSettings
{
	public bool DoubleClickToHide { get; set; } = true;

	/// <summary>
	/// 双击桌面空白处时隐藏/显示的对象，默认图标和分区一起。
	/// </summary>
	public HideTarget DoubleClickTarget { get; set; } = HideTarget.All;

	/// <summary>
	/// 启动时显示还是隐藏；隐藏后只能双击桌面恢复，所以关掉「双击桌面隐藏」时一律显示。
	/// </summary>
	public StartupVisibility StartupVisibility { get; set; } = StartupVisibility.Show;

	/// <summary>
	/// 启动时隐藏的对象（StartupVisibility 为 Hide 时）。
	/// </summary>
	public HideTarget StartupHideTarget { get; set; } = HideTarget.All;

	/// <summary>
	/// 最近一次隐藏着的对象，null 表示都显示着；每次隐藏或显示时记下，下次启动「和上次退出时一样」据此恢复。
	/// </summary>
	public HideTarget? LastHidden { get; set; }

	/// <summary>
	/// 合并成标签页后，标签平分标题栏的宽度、名称居中。
	/// </summary>
	public bool TabsFillWidth { get; set; } = true;

	/// <summary>
	/// 标签不平分宽度时靠哪一边排。
	/// </summary>
	public TabAlignment TabAlignment { get; set; } = TabAlignment.Left;

	/// <summary>
	/// 鼠标停在标签上一会儿就切换过去，不用点击。
	/// </summary>
	public bool SwitchTabOnHover { get; set; } = true;

	/// <summary>
	/// 鼠标停在标签上多久切换（毫秒）。
	/// </summary>
	public int TabHoverDelay { get; set; } = 200;

	public bool ExpandOnHover { get; set; } = true;

	/// <summary>
	/// 单击分区里的图标就打开，不用双击；按住 Ctrl、Shift 单击仍是多选。开着时不再「单击已选中图标的名称开始改名」，改名用 F2 或右键菜单。
	/// </summary>
	public bool FenceSingleClickOpen { get; set; }

	/// <summary>
	/// 单击桌面上散放的图标就打开，其余同 FenceSingleClickOpen。
	/// </summary>
	public bool DesktopSingleClickOpen { get; set; }

	/// <summary>
	/// 1.7.2 及以前分区和桌面共用的「单击打开图标」开关：只读不写，读到时两个开关都取它的值。
	/// </summary>
	[JsonPropertyName("SingleClickOpen")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacySingleClickOpen
	{
		get => null;
		set
		{
			if (value is bool open)
			{
				FenceSingleClickOpen = open;
				DesktopSingleClickOpen = open;
			}
		}
	}

	/// <summary>
	/// 鼠标移开后多久收起悬停展开的分区（毫秒）；收起前鼠标还在分区上时按这个间隔再看，所以不能为 0。
	/// </summary>
	public int CollapseDelay { get; set; } = 400;

	/// <summary>
	/// 卷起的分区连标题栏也藏起来，鼠标移到收起那一条所在的位置时再展开；分区单独设置 FenceSettings.AutoHide 时以分区为准。默认关闭。
	/// </summary>
	public bool AutoHide { get; set; }

	/// <summary>
	/// 自动隐藏的分区藏起来后，在收起那一条靠收向的一侧留一道细线，提示分区藏在这里。
	/// </summary>
	public bool AutoHideShowLine { get; set; }

	/// <summary>
	/// 自动隐藏后那道细线的颜色（#RRGGBB）；浅色桌面上可以改成深色。
	/// </summary>
	public string AutoHideLineColor { get; set; } = "#FFFFFF";

	/// <summary>
	/// 鼠标在自动隐藏的分区那一条上停多久才展开（毫秒），免得鼠标路过时误弹出；0 为立即展开。
	/// </summary>
	public int AutoHideRevealDelay { get; set; } = 50;

	public bool SnapToEdges { get; set; } = true;

	/// <summary>
	/// 吸附到相邻分区时两者之间的间距（DIP），0 表示紧贴。
	/// </summary>
	public double SnapGap { get; set; } = 0;

	/// <summary>
	/// 调整分区大小时按图标的整行、整列吸附。
	/// </summary>
	public bool SnapToGrid { get; set; } = true;

	/// <summary>
	/// 在桌面空白处用左键框选后，在选框旁弹出新建分区、新建映射分区的选项。
	/// </summary>
	public bool DrawToCreate { get; set; } = true;

	/// <summary>
	/// 在桌面右键菜单中显示 MyDesktop 子菜单。
	/// </summary>
	public bool DesktopContextMenu { get; set; } = true;

	/// <summary>
	/// 启动后和之后每天自动检查一次新版本。
	/// </summary>
	public bool AutoCheckUpdates { get; set; } = true;

	/// <summary>
	/// 上次检查新版本的时间（手动检查也算）；程序一直开着时，自动检查据此每天查一次。
	/// </summary>
	public DateTime? LastUpdateCheck { get; set; }

	/// <summary>
	/// 用户选择「跳过此版本」的发行版标签（如 v1.0.2），自动检查时不再提示它。
	/// </summary>
	public string? SkippedVersion { get; set; }

	/// <summary>
	/// 映射分区显示隐藏文件，desktop.ini 这类受保护的系统文件另外跟随资源管理器的「隐藏受保护的操作系统文件」；
	/// 桌面分区和散放图标跟随资源管理器的「隐藏的项目」设置。
	/// </summary>
	public bool ShowHiddenFiles { get; set; }

	/// <summary>
	/// 桌面上新出现的文件按整理规则自动归入分区。
	/// </summary>
	public bool AutoOrganize { get; set; }

	/// <summary>
	/// 弹出搜索框（搜索桌面图标）的全局快捷键，如「Alt+Space」；空字符串表示不用快捷键。
	/// </summary>
	public string SearchHotkey { get; set; } = "Alt+Space";

	/// <summary>
	/// 速览的全局快捷键：把所有分区和桌面图标临时提到当前窗口上面，可以直接打字搜索；空字符串表示不用。
	/// </summary>
	public string PeekHotkey { get; set; } = "Ctrl+Alt+Space";

	public bool TextShadow { get; set; } = true;

	/// <summary>
	/// 默认显示缩略图的扩展名：图片和视频。PDF、Office 文档等的首页预览缩小后像白纸、看不出类型，默认显示类型图标。
	/// </summary>
	public const string DefaultThumbnailExtensions = "jpg jpeg png gif bmp webp heic tif tiff ico mp4 mkv mov avi wmv flv webm m4v";

	/// <summary>
	/// 显示缩略图（内容预览）的扩展名，空格或逗号分隔、带不带点都行，按 OrganizeRule.ParseExtensions 解析；
	/// 其他文件显示类型图标，文件夹始终显示图标。为空表示所有文件都显示图标。
	/// </summary>
	public string ThumbnailExtensions { get; set; } = DefaultThumbnailExtensions;

	/// <summary>
	/// 快捷方式图标左下角叠加系统的小箭头角标，与资源管理器一致。
	/// </summary>
	public bool ShowShortcutArrows { get; set; } = true;

	public OverlayMode OverlayMode { get; set; } = OverlayMode.FollowSystem;

	/// <summary>
	/// 自定义顺序时「系统角标」里的共享角标（跟随系统时用系统的结果，不看这一项）。
	/// </summary>
	public bool ShowShareOverlay { get; set; } = true;

	/// <summary>
	/// 自定义顺序时「系统角标」里共享、快捷方式箭头以外的系统角标（如脱机文件；跟随系统时用系统的结果，不看这一项）。
	/// </summary>
	public bool ShowOtherSystemOverlays { get; set; } = true;

	/// <summary>
	/// 自定义顺序时的角标列表（「系统角标」和处理程序），顺序即优先级（越靠前越优先）。为空表示还没切到过自定义顺序，切过去时按系统的优先级生成。
	/// </summary>
	public List<OverlayHandlerSetting> CustomOverlayHandlers { get; set; } = [];

	/// <summary>
	/// 分区的图标视图里显示图标名称；关掉后只显示图标、格子变窄，鼠标停在图标上仍能看到名称。各分区可以单独设置（FenceSettings.ShowIconNames）。
	/// </summary>
	public bool ShowIconNames { get; set; } = true;

	/// <summary>
	/// 硬件加速：用显卡绘制界面。关掉后改用 CPU 绘制，不占显卡、少占内存，也不会在游戏占满显卡时跟着卡。
	/// </summary>
	public bool HardwareAcceleration { get; set; } = true;

	/// <summary>
	/// 图标飞入动画：开机启动、一键整理和自动整理时图标从桌面飞进分区，删除分区时飞回桌面。关掉后图标直接出现在新位置。
	/// </summary>
	public bool FlightAnimation { get; set; } = true;

	public string DefaultColor { get; set; } = "#1E1E1E";

	public double DefaultOpacity { get; set; } = 0.25;

	public double CornerRadius { get; set; } = 8;

	public IconSizeMode DefaultIconSize { get; set; } = IconSizeMode.Medium;

	public BackgroundEffect DefaultBackground { get; set; } = BackgroundEffect.Blur;

	/// <summary>
	/// 1.2.0 及以前的「毛玻璃背景」开关：只读不写，关着的改成半透明。
	/// </summary>
	[JsonPropertyName("DefaultBlur")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyDefaultBlur
	{
		get => null;
		set
		{
			if (value == false)
			{
				DefaultBackground = BackgroundEffect.Translucent;
			}
		}
	}

	/// <summary>
	/// 自己画的毛玻璃的模糊程度：屏幕上的模糊半径约为它的 8 倍（物理像素），默认 6 约 50 像素。系统亚克力的模糊程度是固定的。
	/// </summary>
	public double BlurRadius { get; set; } = 6;

	/// <summary>
	/// 分区默认显示边框；默认不显示。
	/// </summary>
	public bool DefaultBorder { get; set; }

	/// <summary>
	/// 分区文字的字体（字体族名称）；为空时用系统界面字体。
	/// </summary>
	public string? FenceFontFamily { get; set; }

	/// <summary>
	/// 分区里图标名称的字号（DIP）；分区标题、标签比它大 1。
	/// </summary>
	public double FenceFontSize { get; set; } = 12;

	/// <summary>
	/// 桌面上散放图标名称的字体（字体族名称）；为空时用系统界面字体。
	/// </summary>
	public string? DesktopFontFamily { get; set; }

	/// <summary>
	/// 桌面上散放图标名称的字号（DIP）。
	/// </summary>
	public double DesktopFontSize { get; set; } = 12;

	/// <summary>
	/// 高亮的图标：完整路径 → 颜色（#RRGGBB），分区和桌面上的图标都按它铺一层彩色底块。路径不区分大小写。
	/// </summary>
	public Dictionary<string, string> Highlights
	{
		get;
		set => field = new(value, StringComparer.OrdinalIgnoreCase);
	} = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 上次运行的程序版本；升级后（不论自动更新还是手动安装）第一次启动时据此提示已更新。
	/// </summary>
	public string? LastRunVersion { get; set; }

	/// <summary>
	/// 已提示过「以管理员身份运行时右键菜单会缺项」；只提示一次，之后仍以管理员身份运行也不再提示。
	/// </summary>
	public bool ElevatedNoticeShown { get; set; }

	/// <summary>
	/// 上次提示「.NET 运行时版本过旧」的日期；同一天最多提示一次，运行时升级后不再提示。
	/// </summary>
	public DateTime? RuntimeNoticeDate { get; set; }

	/// <summary>
	/// 各分区现在的位置与尺寸所属的显示器组合（见 FenceManager.CurrentDisplayKey）；为空时是旧版本配置，按当前组合看待。
	/// </summary>
	public string? DisplayKey { get; set; }

	public List<FenceSettings> Fences { get; set; } = [];

	/// <summary>
	/// 合并成标签页的分区组；不在任何组里的分区单独显示。
	/// </summary>
	public List<FenceGroup> Groups { get; set; } = [];

	public List<OrganizeRule> Rules { get; set; } = OrganizeRule.CreateDefaults();
}

/// <summary>
/// 合并成标签页的一组分区（至少两个），外框只管显示：
/// 各成员的图标、映射文件夹、外观、排序和整理规则都还是自己的，拆开后原样恢复；
/// 位置、大小、卷起和锁定由 FenceManager 在组内保持一致，同一时间只显示当前标签那一个分区。
/// </summary>
public sealed class FenceGroup
{
	/// <summary>
	/// 成员分区的 Id，按标签从左到右（竖放标题栏时从上到下）的顺序。
	/// </summary>
	public List<Guid> Members { get; set; } = [];

	/// <summary>
	/// 当前显示的标签。
	/// </summary>
	public Guid Active { get; set; }
}

/// <summary>
/// 分区在某个缩放比例下的位置与尺寸（屏幕物理像素）。
/// </summary>
public sealed record FenceBounds(int X, int Y, int Width, int Height);

/// <summary>
/// 分区在某个显示器组合下的布局：位置与尺寸（屏幕物理像素）、它们对应的 DPI，以及标题栏所在的一侧。
/// </summary>
public sealed record FenceLayout(int X, int Y, int Width, int Height, int Dpi, RollEdge RollEdge);

public sealed class FenceSettings
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public string Title { get; set; } = "新建分区";

	/// <summary>
	/// 映射分区所映射的文件夹；桌面分区为空（旧版本的托管分区在启动时转换）。
	/// </summary>
	public string FolderPath { get; set; } = string.Empty;

	/// <summary>
	/// 映射分区：直接展示任意已有文件夹；否则为桌面分区，展示归入它的桌面图标，文件始终留在桌面。
	/// </summary>
	public bool IsPortal { get; set; }

	/// <summary>
	/// 智能分区的筛选条件：不为空时是智能分区（IsPortal 也为 true，FolderPath 是来源文件夹），只显示来源文件夹里符合条件的项目。
	/// </summary>
	public SmartFilter? Smart { get; set; }

	/// <summary>
	/// 桌面分区的成员：桌面项目的完整解析名（文件为完整路径，此电脑等系统图标为 ::{CLSID}）。
	/// </summary>
	public List<string> Members { get; set; } = [];

	/// <summary>
	/// 位置与尺寸均为屏幕物理像素；Height 为展开状态下的高度。
	/// </summary>
	public int X { get; set; }

	public int Y { get; set; }

	public int Width { get; set; }

	public int Height { get; set; }

	/// <summary>
	/// 上面的位置与尺寸对应的显示器 DPI（缩放比例）；为 0 时是旧版本配置，按当前的 DPI 看待。
	/// </summary>
	public int LayoutDpi { get; set; }

	/// <summary>
	/// 在其他缩放比例下的位置与尺寸，切回那个缩放比例时原样恢复。
	/// </summary>
	public Dictionary<int, FenceBounds> BoundsByDpi { get; set; } = [];

	/// <summary>
	/// 在其他显示器组合下的布局，切回那个组合（如重新接上外接显示器）时原样恢复。
	/// </summary>
	public Dictionary<string, FenceLayout> LayoutByDisplay { get; set; } = [];

	public bool RolledUp { get; set; }

	/// <summary>
	/// 用户指定的卷起方向；为空时自动：贴着屏幕上边或下边时上下收（同时贴着左右边也按上下），只贴左右边时左右收，不贴边时向上。
	/// </summary>
	public RollEdge? RollDirection { get; set; }

	/// <summary>
	/// 标题栏所在、卷起时收向的边：按卷起方向和位置确定，移动分区后随之更新（收起状态下拖到屏幕下边或左右边时也更新）。
	/// </summary>
	public RollEdge RollEdge { get; set; }

	/// <summary>
	/// 卷起后是否连标题栏也藏起来；为空时跟随全局 AppSettings.AutoHide。
	/// </summary>
	public bool? AutoHide { get; set; }

	public bool Locked { get; set; }

	/// <summary>
	/// 以下外观项为空时跟随全局默认设置。
	/// </summary>
	public string? Color { get; set; }

	public double? Opacity { get; set; }

	public BackgroundEffect? Background { get; set; }

	/// <summary>
	/// 1.2.0 及以前单独设置的「毛玻璃背景」开关：只读不写，开着的改成毛玻璃、关着的改成半透明。
	/// </summary>
	[JsonPropertyName("Blur")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyBlur
	{
		get => null;
		set
		{
			if (value is bool blur)
			{
				Background = blur ? BackgroundEffect.Blur : BackgroundEffect.Translucent;
			}
		}
	}

	public bool? Border { get; set; }

	public IconSizeMode? IconSize { get; set; }

	/// <summary>
	/// 图标视图里显示图标名称；为空时跟随全局的 AppSettings.ShowIconNames。
	/// </summary>
	public bool? ShowIconNames { get; set; }

	public FenceView View { get; set; } = FenceView.Icons;

	public SortField SortBy { get; set; } = SortField.Name;

	public bool SortDescending { get; set; }

	/// <summary>
	/// 详细信息视图里修改日期、类型、大小三列的宽度（DIP）：在表头拖动分隔线调过的列才有值，为空时按当前字体自动计算。
	/// </summary>
	public double? DetailsDateWidth { get; set; }

	public double? DetailsTypeWidth { get; set; }

	public double? DetailsSizeWidth { get; set; }

	/// <summary>
	/// 自定义排序时的顺序：桌面分区记完整解析名，映射分区记文件名；不在列表中的新项目排在最后。
	/// </summary>
	public List<string> CustomOrder { get; set; } = [];
}

//// <summary>
/// 智能分区的筛选条件：分区显示来源文件夹（FenceSettings.FolderPath）里符合全部条件的项目，只是显示，文件不移动。
/// 留空的条件不参与；只看来源文件夹这一层，不进子文件夹。设了扩展名或大小条件时不显示文件夹（文件夹没有扩展名和大小）。
/// </summary>
public sealed class SmartFilter
{
	const double BytesPerMB = 1024 * 1024;

	/// <summary>
	/// 修改时间在最近这么多天以内；为空表示不限。
	/// </summary>
	public int? ModifiedWithinDays { get; set; }

	/// <summary>
	/// 扩展名，格式同 OrganizeRule.Extensions。
	/// </summary>
	public string Extensions { get; set; } = string.Empty;

	/// <summary>
	/// 名称条件，格式同 OrganizeRule.NamePatterns（带 * ? 的是通配符，不带的是包含）。
	/// </summary>
	public string NamePatterns { get; set; } = string.Empty;

	/// <summary>
	/// 文件大于（MB）；为空表示不限。
	/// </summary>
	public double? MinSizeMB { get; set; }

	/// <summary>
	/// 文件小于（MB）；为空表示不限。
	/// </summary>
	public double? MaxSizeMB { get; set; }

	public bool Matches(FileSystemInfo info, DateTime now)
	{
		if (ModifiedWithinDays is int days && now - info.LastWriteTime > TimeSpan.FromDays(days))
		{
			return false;
		}
		var extensions = OrganizeRule.ParseExtensions(Extensions);
		bool sized = MinSizeMB != null || MaxSizeMB != null;
		if (info is not FileInfo file)
		{
			return extensions.Count == 0 && !sized && NameMatches(info.FullName);
		}
		if (extensions.Count > 0 && !extensions.Contains(file.Extension))
		{
			return false;
		}
		if (MinSizeMB is double min && file.Length <= min * BytesPerMB)
		{
			return false;
		}
		if (MaxSizeMB is double max && file.Length >= max * BytesPerMB)
		{
			return false;
		}
		return NameMatches(file.FullName);
	}

	bool NameMatches(string path)
	{
		var patterns = OrganizeRule.ParsePatterns(NamePatterns);
		return patterns.Count == 0 || patterns.Any(p => OrganizeRule.NameMatches(p, path));
	}

	/// <summary>
	/// 条件的简短说明，显示在分区标题的提示里，如「最近 7 天修改 · 大于 1024 MB」。
	/// </summary>
	public string Describe()
	{
		var parts = new List<string>();
		if (ModifiedWithinDays is int days)
		{
			parts.Add($"最近 {days} 天修改");
		}
		if (OrganizeRule.ParseExtensions(Extensions) is { Count: > 0 } extensions)
		{
			parts.Add(string.Join(" ", extensions));
		}
		if (!string.IsNullOrWhiteSpace(NamePatterns))
		{
			parts.Add($"名称 {NamePatterns.Trim()}");
		}
		if (MinSizeMB is double min)
		{
			parts.Add($"大于 {min} MB");
		}
		if (MaxSizeMB is double max)
		{
			parts.Add($"小于 {max} MB");
		}
		return parts.Count > 0 ? string.Join(" · ", parts) : "全部项目";
	}

	public SmartFilter Clone() => (SmartFilter)MemberwiseClone();
}

/// <summary>
/// 整理规则：把匹配的桌面文件归入同名分区。
/// 条件分四类：类型（扩展名、文件夹）、名称、快捷方式目标、快捷方式类别。
/// 同一类里的多个值满足一个即可（或），填了的几类都要满足（且），留空的那一类不参与；
/// 一类条件都没填的规则不匹配任何项目（兜底规则除外）。旧版本只有类型这一类，升级后行为不变。
/// </summary>
public sealed class OrganizeRule
{
	static readonly char[] Separators = [';', ',', ' ', '；', '，'];

	/// <summary>
	/// 名称、快捷方式目标的分隔符：名称和程序名里常有空格、逗号，只按分号和换行分隔。
	/// </summary>
	static readonly char[] PatternSeparators = [';', '；', '\r', '\n'];

	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// 扩展名列表，分号、逗号或空格分隔，例如 ".jpg; .png"。
	/// </summary>
	public string Extensions { get; set; } = string.Empty;

	/// <summary>
	/// 类型条件里的「文件夹」：和扩展名同属一类，文件夹看这一项、文件看扩展名。
	/// </summary>
	public bool MatchFolders { get; set; }

	/// <summary>
	/// 名称条件，分号分隔：带 * 或 ? 的是通配符，和完整文件名或去掉扩展名的名称匹配；
	/// 不带的是包含，文件名里有这段文字就算。不区分大小写。
	/// </summary>
	public string NamePatterns { get; set; } = string.Empty;

	/// <summary>
	/// 快捷方式目标条件，分号分隔，只对快捷方式（.lnk 看目标路径，.url 看网址）起作用：
	/// 不含 \、/、: 的按目标的文件名匹配（如 photoshop.exe、*Code*.exe），不带通配符时要完全相同；
	/// 含有的按完整目标匹配（如 D:\Games\*、steam://*），不带通配符时包含即可。不区分大小写。
	/// </summary>
	public string TargetPatterns { get; set; } = string.Empty;

	/// <summary>
	/// 快捷方式类别条件：快捷方式分类表（数据目录的 shortcut-categories.json）里的类别名，
	/// 目标按该类别的程序列表匹配，规则同 TargetPatterns。
	/// </summary>
	public string ShortcutCategory { get; set; } = string.Empty;

	/// <summary>
	/// 兜底规则：收纳所有未被其他规则命中的项目。
	/// </summary>
	public bool IsFallback { get; set; }

	/// <summary>
	/// 关联的分区，首次整理时自动创建并记录。
	/// </summary>
	public Guid? FenceId { get; set; }

	/// <param name="context">取快捷方式目标和类别的程序列表；只在规则带这两类条件时才用到。</param>
	public bool Matches(string path, bool isDirectory, RuleContext context)
	{
		bool any = false;
		if (MatchFolders || !string.IsNullOrWhiteSpace(Extensions))
		{
			any = true;
			bool typeMatched = isDirectory
					? MatchFolders
					: Path.GetExtension(path) is { Length: > 0 } extension && ParseExtensions(Extensions).Contains(extension);
			if (!typeMatched)
			{
				return false;
			}
		}
		var names = ParsePatterns(NamePatterns);
		if (names.Count > 0)
		{
			any = true;
			if (!names.Any(p => NameMatches(p, path)))
			{
				return false;
			}
		}
		var targets = ParsePatterns(TargetPatterns);
		bool hasCategory = !string.IsNullOrWhiteSpace(ShortcutCategory);
		if (targets.Count > 0 || hasCategory)
		{
			any = true;
			var target = isDirectory ? null : context.ResolveTarget(path);
			if (string.IsNullOrEmpty(target))
			{
				return false;
			}
			if (targets.Count > 0 && !targets.Any(p => TargetMatches(p, target)))
			{
				return false;
			}
			if (hasCategory && !context.CategoryPatterns(ShortcutCategory.Trim()).Any(p => TargetMatches(p, target)))
			{
				return false;
			}
		}
		return any;
	}

	/// <summary>
	/// 按列表顺序取第一条命中的规则，都不命中时返回兜底规则（没有则为 null）。
	/// </summary>
	public static OrganizeRule? Match(IEnumerable<OrganizeRule> rules, string path, bool isDirectory, RuleContext context)
	{
		OrganizeRule? fallback = null;
		foreach (var rule in rules)
		{
			if (rule.IsFallback)
			{
				fallback ??= rule;
			}
			else if (rule.Matches(path, isDirectory, context))
			{
				return rule;
			}
		}
		return fallback;
	}

	static bool HasWildcard(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

	/// <summary>
	/// 文件名是否符合一个名称条件，规则见 NamePatterns。
	/// </summary>
	public static bool NameMatches(string pattern, string path)
	{
		var fileName = Path.GetFileName(path);
		if (HasWildcard(pattern))
		{
			return FileSystemName.MatchesSimpleExpression(pattern, fileName)
					|| FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileNameWithoutExtension(fileName));
		}
		return fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 快捷方式目标是否符合一个条件，规则见 TargetPatterns。
	/// </summary>
	public static bool TargetMatches(string pattern, string target)
	{
		bool wildcard = HasWildcard(pattern);
		if (pattern.IndexOfAny(['\\', '/', ':']) >= 0)
		{
			return wildcard
					? FileSystemName.MatchesSimpleExpression(pattern, target)
					: target.Contains(pattern, StringComparison.OrdinalIgnoreCase);
		}
		var fileName = Path.GetFileName(target.TrimEnd('\\', '/'));
		return wildcard
				? FileSystemName.MatchesSimpleExpression(pattern, fileName)
				: string.Equals(pattern, fileName, StringComparison.OrdinalIgnoreCase);
	}

	public static HashSet<string> ParseExtensions(string text)
	{
		return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
				.Select(e => e.StartsWith('.') ? e : "." + e)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 名称、快捷方式目标条件：按分号、换行分隔，去掉首尾空白。
	/// </summary>
	public static List<string> ParsePatterns(string text)
	{
		return text.Split(PatternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
	}

	/// <summary>
	/// 按快捷方式分类表归类的规则，要排在「快捷方式」规则前面；分类表里识别不到的快捷方式仍归入「快捷方式」。
	/// </summary>
	public static List<OrganizeRule> CreateCategoryRules(IEnumerable<string> categories)
	{
		return categories.Select(c => new OrganizeRule { Name = c, ShortcutCategory = c }).ToList();
	}

	public static List<OrganizeRule> CreateDefaults()
	{
		return
		[
			.. CreateCategoryRules(DefaultShortcutCategories.Keys),
			new() { Name = "快捷方式", Extensions = ".lnk; .url; .appref-ms; .website" },
			new() { Name = "文件夹", MatchFolders = true },
			new() { Name = "文档", Extensions = ".doc; .docx; .xls; .xlsx; .xlsm; .ppt; .pptx; .pdf; .txt; .md; .rtf; .wps; .et; .dps; .csv; .odt; .ods; .odp; .ofd; .xmind; .vsdx; .epub" },
			new() { Name = "图片", Extensions = ".jpg; .jpeg; .png; .gif; .bmp; .webp; .svg; .ico; .tif; .tiff; .heic; .heif; .psd; .ai; .raw" },
			new() { Name = "影音", Extensions = ".mp4; .mkv; .avi; .mov; .wmv; .flv; .webm; .m4v; .rmvb; .mp3; .wav; .flac; .aac; .m4a; .ogg; .wma; .ape" },
			new() { Name = "压缩包", Extensions = ".zip; .rar; .7z; .tar; .gz; .tgz; .bz2; .xz; .iso; .cab" },
			new() { Name = "程序", Extensions = ".exe; .msi; .bat; .cmd; .ps1; .vbs; .jar; .appx; .msix" },
			new() { Name = "其他", IsFallback = true },
		];
	}

	/// <summary>
	/// 内置的快捷方式分类表：类别 → 目标程序（规则同 TargetPatterns）。
	/// 第一次用到时写进数据目录的 shortcut-categories.json，之后以那个文件为准。
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string[]> DefaultShortcutCategories = new Dictionary<string, string[]>
	{
		["设计"] =
		[
			"Photoshop.exe", "Illustrator.exe", "InDesign.exe", "AfterFX.exe", "Adobe Premiere Pro.exe", "Lightroom.exe", "Adobe Lightroom Classic.exe",
			"XD.exe", "Animate.exe", "Dreamweaver.exe", "Figma.exe", "blender.exe", "SketchUp.exe", "CorelDRW.exe", "3dsmax.exe", "maya.exe",
			"Cinema 4D.exe", "ZBrush.exe", "krita.exe", "gimp*.exe", "inkscape.exe", "Pixso.exe", "MasterGo.exe", "acad.exe", "Rhino.exe",
			"CapCut.exe", "JianyingPro.exe", "Resolve.exe", "Affinity*.exe",
		],
		["社交"] =
		[
			"WeChat.exe", "Weixin.exe", "WXWork.exe", "QQ.exe", "QQNT.exe", "TIM.exe", "DingTalk.exe", "Feishu.exe", "Lark.exe", "Telegram.exe",
			"Discord.exe", "Slack.exe", "Teams.exe", "ms-teams.exe", "Skype.exe", "WhatsApp.exe", "LINE.exe", "KOOK.exe", "YY.exe", "Zoom.exe",
			"wemeetapp.exe",
		],
		["下载"] =
		[
			"Thunder.exe", "IDMan.exe", "Motrix.exe", "qbittorrent.exe", "BitComet.exe", "uTorrent.exe", "fdm.exe", "BaiduNetdisk.exe",
			"aDrive.exe", "115*.exe", "NDM.exe", "FlashGet*.exe",
		],
		["多媒体"] =
		[
			"PotPlayerMini64.exe", "PotPlayerMini.exe", "PotPlayer*.exe", "vlc.exe", "mpc-hc64.exe", "mpc-be64.exe", "cloudmusic.exe", "QQMusic.exe",
			"KuGou.exe", "KwMusic.exe", "Spotify.exe", "foobar2000.exe", "AIMP.exe", "哔哩哔哩.exe", "QyClient.exe", "QQLive.exe", "YoukuDesktop.exe",
			"obs64.exe", "Kodi.exe", "mpv.exe", "Honeyview.exe", "ImageGlass.exe", "Huya.exe", "DouyuLive*.exe", "FormatFactory.exe",
		],
		["开发"] =
		[
			"Code.exe", "Code - Insiders.exe", "Cursor.exe", "Windsurf.exe", "devenv.exe", "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe",
			"goland64.exe", "rider64.exe", "datagrip64.exe", "phpstorm64.exe", "studio64.exe", "sublime_text.exe", "notepad++.exe", "HBuilderX.exe",
			"wechatdevtools.exe", "git-bash.exe", "WindowsTerminal.exe", "wt.exe", "Postman.exe", "navicat.exe", "dbeaver.exe", "Docker Desktop.exe",
			"Xshell.exe", "MobaXterm*.exe", "Termius.exe", "putty.exe", "WinSCP.exe", "GitHubDesktop.exe", "Fork.exe", "SourceTree.exe", "Apifox.exe",
		],
		["游戏"] =
		[
			"steam.exe", "steam://*", "EpicGamesLauncher.exe", "com.epicgames.launcher://*", "WeGame.exe", "Battle.net.exe", "Battle.net Launcher.exe",
			"EADesktop.exe", "Origin.exe", "UbisoftConnect.exe", "upc.exe", "GalaxyClient.exe", "MinecraftLauncher.exe", "HYP.exe", "YuanShen.exe",
			"GenshinImpact.exe", "StarRail.exe", "LeagueClient.exe", "NetEase*Launcher*.exe", "XboxPcApp.exe", "PlayniteDesktopApp.exe",
		],
	};
}

/// <summary>
/// 规则匹配时要从外部取的信息。
/// </summary>
/// <param name="ResolveTarget">快捷方式的目标（.lnk 为目标路径，.url 为网址），不是快捷方式或取不到时为 null。</param>
/// <param name="CategoryPatterns">快捷方式分类表里某个类别的程序列表，没有这个类别时为空。</param>
public sealed record RuleContext(Func<string, string?> ResolveTarget, Func<string, IReadOnlyList<string>> CategoryPatterns);

// <summary>
/// 自定义顺序里的一个角标处理程序。
/// </summary>
public sealed class OverlayHandlerSetting
{
	/// <summary>
	/// 处理程序为 ShellIconOverlayIdentifiers 子键默认值里的 CLSID，「系统角标」为 system。
	/// </summary>
	public string Id { get; set; } = "";

	/// <summary>
	/// 是否启用；「系统角标」不看这一项，由 ShowShortcutArrows、ShowShareOverlay、ShowOtherSystemOverlays 三项决定。
	/// </summary>
	public bool Enabled { get; set; }
}
