using System.Windows.Media;

namespace MyDesktop.Models;

/// <summary>
/// 分区外观相关的预设值与换算。
/// </summary>
public static class Appearance
{
	public static readonly (string Name, string Hex)[] ColorPresets =
	[
		("石墨黑", "#1E1E1E"),
		("深空灰", "#3A3F4B"),
		("海军蓝", "#1E3A8A"),
		("天空蓝", "#2563EB"),
		("湖水青", "#0F766E"),
		("森林绿", "#166534"),
		("暮光紫", "#6D28D9"),
		("玫瑰红", "#BE123C"),
		("琥珀橙", "#C2410C"),
		("云朵白", "#F5F5F5"),
	];

	public static readonly int[] OpacityPresets = [10, 25, 40, 55, 70, 85, 100];

	/// <summary>
	/// 列表视图下的图标尺寸（DIP）。
	/// </summary>
	public const double ListIconDip = 20;

	public static double ToDip(this IconSizeMode size)
	{
		return size switch
		{
			IconSizeMode.Small => 32,
			IconSizeMode.Medium => 48,
			IconSizeMode.Large => 64,
			IconSizeMode.ExtraLarge => 96,
		};
	}

	public static string DisplayName(this IconSizeMode size)
	{
		return size switch
		{
			IconSizeMode.Small => "小图标",
			IconSizeMode.Medium => "中等图标",
			IconSizeMode.Large => "大图标",
			IconSizeMode.ExtraLarge => "超大图标",
		};
	}

	public static string DisplayName(this SortField field)
	{
		return field switch
		{
			SortField.Name => "名称",
			SortField.Type => "类型",
			SortField.Size => "大小",
			SortField.Modified => "修改日期",
		};
	}

	public static Color ParseColor(string? text, Color fallback)
	{
		return TryParseColor(text, out var color) ? color : fallback;
	}

	public static bool TryParseColor(string? text, out Color color)
	{
		color = default;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		try
		{
			color = (Color)ColorConverter.ConvertFromString(text);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

	/// <summary>
	/// 相对亮度（0~1），用于决定文字用深色还是浅色。
	/// </summary>
	public static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
}
