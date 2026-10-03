using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using MyDesktop.Native;

namespace MyDesktop.Views;

/// <summary>
/// 分区中的一个文件或文件夹。
/// </summary>
public sealed class FenceItem : INotifyPropertyChanged
{
	ImageSource? _icon;
	bool _isRenaming;
	string _renameText = string.Empty;

	FenceItem(string fullPath)
	{
		FullPath = fullPath;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string FullPath { get; }

	public string FileName => Path.GetFileName(FullPath);

	public bool IsFolder { get; private set; }

	/// <summary>
	/// Shell 显示名：快捷方式不带 .lnk，并遵循系统「隐藏已知文件扩展名」设置。
	/// </summary>
	public string DisplayName { get; private set; } = string.Empty;

	public string TypeName { get; private set; } = string.Empty;

	public long Size { get; private set; }

	public DateTime Modified { get; private set; }

	public ImageSource? Icon
	{
		get => _icon;
		set => Set(ref _icon, value);
	}

	public bool IsRenaming
	{
		get => _isRenaming;
		set => Set(ref _isRenaming, value);
	}

	public string RenameText
	{
		get => _renameText;
		set => Set(ref _renameText, value);
	}

	public string ToolTipText
	{
		get
		{
			var lines = new List<string> { DisplayName };
			if (!string.IsNullOrEmpty(TypeName))
			{
				lines.Add($"类型：{TypeName}");
			}
			if (!IsFolder)
			{
				lines.Add($"大小：{FormatSize(Size)}");
			}
			lines.Add($"修改日期：{Modified:yyyy/M/d HH:mm}");
			return string.Join("\n", lines);
		}
	}

	/// <summary>
	/// 已请求的图标像素尺寸，0 表示需要重新加载。
	/// </summary>
	internal int IconPixelSize { get; set; }

	public static FenceItem Create(FileSystemInfo info)
	{
		var item = new FenceItem(info.FullName)
		{
			IsFolder = info is DirectoryInfo,
		};
		item.ReadShellInfo(info);
		item.Refresh(info);
		return item;
	}

	/// <summary>
	/// 用最新的文件信息更新，返回内容是否发生变化（变化时缩略图需要重新加载）。
	/// </summary>
	public bool Refresh(FileSystemInfo info)
	{
		var modified = info.LastWriteTime;
		long size = info is FileInfo file ? file.Length : 0;
		if (modified == Modified && size == Size)
		{
			return false;
		}
		Modified = modified;
		Size = size;
		OnPropertyChanged(nameof(ToolTipText));
		return true;
	}

	void ReadShellInfo(FileSystemInfo info)
	{
		var shellInfo = new SHFILEINFO();
		var flags = NativeMethods.SHGFI_DISPLAYNAME | NativeMethods.SHGFI_TYPENAME;
		if (NativeMethods.SHGetFileInfo(FullPath, 0, ref shellInfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero)
		{
			DisplayName = shellInfo.szDisplayName;
			TypeName = shellInfo.szTypeName;
		}
		if (string.IsNullOrEmpty(DisplayName))
		{
			DisplayName = info.Name;
		}
	}

	static string FormatSize(long bytes)
	{
		return bytes switch
		{
			< 1024 => $"{bytes} 字节",
			< 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
			< 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
			_ => $"{bytes / 1073741824.0:0.##} GB",
		};
	}

	void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return;
		}
		field = value;
		OnPropertyChanged(name);
	}

	void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
