using System.Runtime.InteropServices;
using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 新建智能分区的模板：名称、来源文件夹和筛选条件。
/// </summary>
internal sealed record SmartTemplate(string Title, Func<string> Folder, Func<SmartFilter> Filter);

internal static class SmartTemplates
{
	static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

	/// <summary>
	/// 「下载」文件夹的实际位置（用户可能把它挪到了别的盘），取不到时抛出异常。
	/// </summary>
	public static string Downloads
	{
		get
		{
			var id = DownloadsId;
			int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
			try
			{
				Marshal.ThrowExceptionForHR(result);
				return Marshal.PtrToStringUni(pointer) ?? throw new InvalidOperationException("取不到「下载」文件夹的位置");
			}
			finally
			{
				Marshal.FreeCoTaskMem(pointer);
			}
		}
	}

	public static readonly IReadOnlyList<SmartTemplate> All =
	[
		new("最近 7 天的下载", () => Downloads, () => new SmartFilter { ModifiedWithinDays = 7 }),
		new("下载里大于 1 GB 的文件", () => Downloads, () => new SmartFilter { MinSizeMB = 1024 }),
		new("下载的安装包", () => Downloads, () => new SmartFilter { Extensions = ".exe; .msi; .msix; .appx; .msixbundle" }),
		new("下载的压缩包", () => Downloads, () => new SmartFilter { Extensions = ".zip; .rar; .7z; .tar; .gz; .xz; .iso" }),
		new("最近 30 天的文档", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				() => new SmartFilter { ModifiedWithinDays = 30, Extensions = ".doc; .docx; .xls; .xlsx; .ppt; .pptx; .pdf; .txt; .md; .wps; .et; .dps" }),
	];
}
