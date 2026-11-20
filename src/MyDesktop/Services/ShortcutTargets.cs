using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MyDesktop.Core;
using MyDesktop.Native;

namespace MyDesktop.Services;

/// <summary>
/// 读快捷方式的目标，供整理规则按目标程序归类：.lnk 取目标路径，.url 取网址。
/// 按「路径 + 修改时间」缓存；要在 STA 线程（界面线程）上调用。
/// </summary>
internal static class ShortcutTargets
{
	const int MaxPath = 32768;

	static readonly Dictionary<string, (DateTime Modified, string? Target)> Cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 快捷方式的目标，不是快捷方式或读不出来时为 null；MSI 发布的快捷方式（如部分 Office）、指向系统对象的快捷方式没有文件路径，也是 null。
	/// </summary>
	public static string? Resolve(string path)
	{
		var extension = Path.GetExtension(path);
		bool isLink = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
		bool isUrl = extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
		if (!isLink && !isUrl)
		{
			return null;
		}
		DateTime modified;
		try
		{
			modified = File.GetLastWriteTimeUtc(path);
		}
		catch (Exception)
		{
			return null;
		}
		if (Cache.TryGetValue(path, out var cached) && cached.Modified == modified)
		{
			return cached.Target;
		}
		string? target;
		try
		{
			target = isLink ? ReadLink(path) : ReadUrl(path);
		}
		catch (Exception ex)
		{
			Log.Warn($"读取快捷方式目标失败：{path}", ex);
			target = null;
		}
		Cache[path] = (modified, target);
		return target;
	}

	static string? ReadLink(string path)
	{
		var link = (IShellLinkW)new ShellLink();
		try
		{
			((IPersistFile)link).Load(path, 0);
			var buffer = new StringBuilder(MaxPath);
			link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
			var target = buffer.ToString();
			return target.Length > 0 ? target : null;
		}
		finally
		{
			Marshal.ReleaseComObject(link);
		}
	}

	/// <summary>
	/// .url 是 INI 格式，[InternetShortcut] 节下的 URL= 就是网址。
	/// </summary>
	static string? ReadUrl(string path)
	{
		bool inSection = false;
		foreach (var raw in File.ReadLines(path))
		{
			var line = raw.Trim();
			if (line.StartsWith('['))
			{
				inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
			}
			else if (inSection && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
			{
				var url = line[4..].Trim();
				return url.Length > 0 ? url : null;
			}
		}
		return null;
	}
}
