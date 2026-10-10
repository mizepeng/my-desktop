using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyDesktop.Views;

/// <summary>
/// 分区、桌面图标文字的字体和字号（设置 → 外观，两者分开设置）。
/// </summary>
internal static class TextFont
{
	public const double MinSize = 9;

	public const double MaxSize = 16;

	/// <summary>
	/// 给窗口指定字体和字号；没选字体时清掉，用系统界面字体（WPF 默认）。返回实际的字体和字号。
	/// </summary>
	public static (FontFamily Family, double Size) Apply(Window window, string? family, double size)
	{
		if (string.IsNullOrEmpty(family))
		{
			window.ClearValue(Control.FontFamilyProperty);
		}
		else
		{
			window.FontFamily = new FontFamily(family);
		}
		size = Math.Clamp(size, MinSize, MaxSize);
		window.FontSize = size;
		return (window.FontFamily, size);
	}
}
