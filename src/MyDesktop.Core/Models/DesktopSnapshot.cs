using System.Drawing;
using System.Text;

namespace MyDesktop.Core;

/// <summary>
/// 资源管理器桌面上的一个项目。Key 为完整解析名：文件是完整路径，此电脑等系统图标是 ::{CLSID}。
/// Position 是它在资源管理器图标列表里的位置（物理像素，相对列表左上角）。
/// IconKey 是系统图标当前使用的图标位置（回收站空、满时不同），用来判断图标是否需要重新加载。
/// </summary>
internal sealed record DesktopEntry(string Key, string DisplayName, bool IsFileSystem, bool IsFolder, Point Position, string? IconKey = null,
		bool CanRename = true, bool CanDelete = true);

/// <summary>
/// 一次读取到的资源管理器桌面视图：排列方式、间距、图标大小、列表在屏幕上的范围和全部项目。
/// </summary>
internal sealed class DesktopSnapshot
{
	public DesktopSnapshot(bool autoArrange, Point spacing, int iconSize, Rectangle listBounds, IReadOnlyList<DesktopEntry> items)
	{
		AutoArrange = autoArrange;
		Spacing = spacing;
		IconSize = iconSize;
		ListBounds = listBounds;
		Items = items;
		var byKey = new Dictionary<string, DesktopEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items)
		{
			byKey.TryAdd(item.Key, item);
		}
		ByKey = byKey;
		var sb = new StringBuilder();
		sb.Append(autoArrange).Append('|').Append(spacing.X).Append('x').Append(spacing.Y).Append('|').Append(iconSize)
				.Append('|').Append(listBounds.Left).Append(',').Append(listBounds.Top).Append(',').Append(listBounds.Right).Append(',').Append(listBounds.Bottom);
		foreach (var item in items)
		{
			sb.Append('\n').Append(item.Key).Append('|').Append(item.DisplayName).Append('|').Append(item.Position.X).Append(',').Append(item.Position.Y)
					.Append('|').Append(item.IconKey);
		}
		Signature = sb.ToString();
	}

	public bool AutoArrange { get; }

	/// <summary>
	/// 图标格子的间距（物理像素）。
	/// </summary>
	public Point Spacing { get; }

	/// <summary>
	/// 资源管理器设置的图标大小（与缩放无关的逻辑像素，中等图标为 48）。
	/// </summary>
	public int IconSize { get; }

	/// <summary>
	/// 资源管理器图标列表在屏幕上的范围（物理像素），项目位置以它的左上角为原点。
	/// </summary>
	public Rectangle ListBounds { get; }

	public IReadOnlyList<DesktopEntry> Items { get; }

	public IReadOnlyDictionary<string, DesktopEntry> ByKey { get; }

	/// <summary>
	/// 用于判断两次读取之间有没有变化。
	/// </summary>
	public string Signature { get; }
}

