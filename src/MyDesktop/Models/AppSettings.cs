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
/// 全局设置与全部分区布局，序列化为数据目录下的 settings.json。
/// </summary>
public sealed class AppSettings
{
	public bool DoubleClickToHide { get; set; } = true;

	/// <summary>
	/// 双击桌面空白处时隐藏/显示的对象，默认图标和分区一起。
	/// </summary>
	public HideTarget DoubleClickTarget { get; set; } = HideTarget.All;

	public bool ExpandOnHover { get; set; } = true;

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
	/// 在桌面空白处按住右键拖动画框新建分区。
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
	/// 上次检查新版本的时间（手动检查也算），自动检查据此每天只查一次（跨重启）。
	/// </summary>
	public DateTime? LastUpdateCheck { get; set; }

	/// <summary>
	/// 用户选择「跳过此版本」的发行版标签（如 v1.0.2），自动检查时不再提示它。
	/// </summary>
	public string? SkippedVersion { get; set; }

	/// <summary>
	/// 映射分区显示隐藏文件；桌面分区和散放图标跟随资源管理器的「隐藏的项目」设置。
	/// </summary>
	public bool ShowHiddenFiles { get; set; }

	/// <summary>
	/// 桌面上新出现的文件按整理规则自动归入分区。
	/// </summary>
	public bool AutoOrganize { get; set; }

	public bool TextShadow { get; set; } = true;

	/// <summary>
	/// 快捷方式图标左下角叠加系统的小箭头角标，与资源管理器一致。
	/// </summary>
	public bool ShowShortcutArrows { get; set; } = true;

	public string DefaultColor { get; set; } = "#1E1E1E";

	public double DefaultOpacity { get; set; } = 0.45;

	public double CornerRadius { get; set; } = 8;

	public IconSizeMode DefaultIconSize { get; set; } = IconSizeMode.Medium;

	/// <summary>
	/// 各分区现在的位置与尺寸所属的显示器组合（见 FenceManager.CurrentDisplayKey）；为空时是旧版本配置，按当前组合看待。
	/// </summary>
	public string? DisplayKey { get; set; }

	public List<FenceSettings> Fences { get; set; } = [];

	public List<OrganizeRule> Rules { get; set; } = OrganizeRule.CreateDefaults();
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

	public bool Locked { get; set; }

	/// <summary>
	/// 以下外观项为空时跟随全局默认设置。
	/// </summary>
	public string? Color { get; set; }

	public double? Opacity { get; set; }

	public IconSizeMode? IconSize { get; set; }

	public FenceView View { get; set; } = FenceView.Icons;

	public SortField SortBy { get; set; } = SortField.Name;

	public bool SortDescending { get; set; }

	/// <summary>
	/// 自定义排序时的顺序：桌面分区记完整解析名，映射分区记文件名；不在列表中的新项目排在最后。
	/// </summary>
	public List<string> CustomOrder { get; set; } = [];
}

/// <summary>
/// 整理规则：把匹配的桌面文件归入同名分区。
/// </summary>
public sealed class OrganizeRule
{
	static readonly char[] Separators = [';', ',', ' ', '；', '，'];

	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// 扩展名列表，分号、逗号或空格分隔，例如 ".jpg; .png"。
	/// </summary>
	public string Extensions { get; set; } = string.Empty;

	public bool MatchFolders { get; set; }

	/// <summary>
	/// 兜底规则：收纳所有未被其他规则命中的项目。
	/// </summary>
	public bool IsFallback { get; set; }

	/// <summary>
	/// 关联的分区，首次整理时自动创建并记录。
	/// </summary>
	public Guid? FenceId { get; set; }

	public bool Matches(string path, bool isDirectory)
	{
		if (isDirectory)
		{
			return MatchFolders;
		}
		var extension = Path.GetExtension(path);
		return extension.Length > 0 && ParseExtensions(Extensions).Contains(extension);
	}

	/// <summary>
	/// 按列表顺序取第一条命中的规则，都不命中时返回兜底规则（没有则为 null）。
	/// </summary>
	public static OrganizeRule? Match(IEnumerable<OrganizeRule> rules, string path, bool isDirectory)
	{
		OrganizeRule? fallback = null;
		foreach (var rule in rules)
		{
			if (rule.IsFallback)
			{
				fallback ??= rule;
			}
			else if (rule.Matches(path, isDirectory))
			{
				return rule;
			}
		}
		return fallback;
	}

	public static HashSet<string> ParseExtensions(string text)
	{
		return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
				.Select(e => e.StartsWith('.') ? e : "." + e)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	public static List<OrganizeRule> CreateDefaults()
	{
		return
		[
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
}
