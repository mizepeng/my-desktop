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
/// 全局设置与全部分区布局，序列化为数据目录下的 settings.json。
/// </summary>
public sealed class AppSettings
{
	/// <summary>
	/// 托管分区的文件存放根目录；为空时使用默认位置（与桌面同级的「桌面分区」目录）。
	/// </summary>
	public string? StorageRoot { get; set; }

	public bool DoubleClickToHide { get; set; } = true;

	public bool ExpandOnHover { get; set; } = true;

	public bool SnapToEdges { get; set; } = true;

	/// <summary>
	/// 吸附到相邻分区时两者之间的间距（DIP），0 表示紧贴。
	/// </summary>
	public double SnapGap { get; set; } = 6;

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

	public bool ShowHiddenFiles { get; set; }

	/// <summary>
	/// 桌面上新出现的文件按整理规则自动移入分区。
	/// </summary>
	public bool AutoOrganize { get; set; }

	public bool TextShadow { get; set; } = true;

	public string DefaultColor { get; set; } = "#1E1E1E";

	public double DefaultOpacity { get; set; } = 0.45;

	public double CornerRadius { get; set; } = 8;

	public IconSizeMode DefaultIconSize { get; set; } = IconSizeMode.Medium;

	public List<FenceSettings> Fences { get; set; } = [];

	public List<OrganizeRule> Rules { get; set; } = OrganizeRule.CreateDefaults();
}

public sealed class FenceSettings
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public string Title { get; set; } = "新建分区";

	public string FolderPath { get; set; } = string.Empty;

	/// <summary>
	/// 映射分区：直接展示任意已有文件夹；否则为托管分区，文件夹由本程序在存储目录下创建。
	/// </summary>
	public bool IsPortal { get; set; }

	/// <summary>
	/// 位置与尺寸均为屏幕物理像素；Height 为展开状态下的高度。
	/// </summary>
	public int X { get; set; }

	public int Y { get; set; }

	public int Width { get; set; }

	public int Height { get; set; }

	public bool RolledUp { get; set; }

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
	/// 自定义排序时的文件名顺序；不在列表中的新文件按名称排在最后。
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
