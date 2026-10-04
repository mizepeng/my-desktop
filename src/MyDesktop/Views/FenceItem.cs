using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using MyDesktop.Core;
using MyDesktop.Native;

namespace MyDesktop.Views;

/// <summary>
/// 分区或桌面上的一个项目。来自桌面时 FullPath 是完整解析名：文件为完整路径，此电脑等系统图标为 ::{CLSID}。
/// </summary>
public sealed class FenceItem : INotifyPropertyChanged
{
	ImageSource? _icon;
	bool _isRenaming;
	string _renameText = string.Empty;
	string _displayName = string.Empty;
	double _x;
	double _y;
	bool _isCut;
	bool _isDropTarget;

	FenceItem(string fullPath)
	{
		FullPath = fullPath;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string FullPath { get; }

	public string FileName => Path.GetFileName(FullPath);

	public bool IsFolder { get; private set; }

	/// <summary>
	/// 此电脑、回收站这类不对应文件的系统图标：不能复制，改名、删除走它们自己的 Shell 命令。
	/// </summary>
	public bool IsVirtual { get; private set; }

	/// <summary>
	/// 系统图标能否改名、删除（删除即从桌面移除图标）；文件总是可以。
	/// </summary>
	public bool CanRename { get; private set; } = true;

	public bool CanDelete { get; private set; } = true;

	/// <summary>
	/// 系统图标当前的图标位置（回收站空、满时不同），变化时重新加载图标。
	/// </summary>
	internal string? IconKey { get; private set; }

	/// <summary>
	/// 已被剪切到剪贴板，与资源管理器一样半透明显示。
	/// </summary>
	public bool IsCut
	{
		get => _isCut;
		set => Set(ref _isCut, value);
	}

	/// <summary>
	/// 拖动的内容正悬停在它上面、松手会交给它处理（放进文件夹、用程序打开、删除到回收站）。
	/// </summary>
	public bool IsDropTarget
	{
		get => _isDropTarget;
		set => Set(ref _isDropTarget, value);
	}

	/// <summary>
	/// Shell 显示名：快捷方式不带 .lnk，并遵循系统「隐藏已知文件扩展名」设置。
	/// </summary>
	public string DisplayName
	{
		get => _displayName;
		private set
		{
			Set(ref _displayName, value);
			OnPropertyChanged(nameof(ToolTipText));
		}
	}

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

	/// <summary>
	/// 在散放图标层上的位置（DIP，相对图标层左上角）。
	/// </summary>
	public double X
	{
		get => _x;
		set => Set(ref _x, value);
	}

	public double Y
	{
		get => _y;
		set => Set(ref _y, value);
	}

	public string ToolTipText
	{
		get
		{
			if (IsVirtual)
			{
				return DisplayName;
			}
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
	/// 由资源管理器桌面视图中的项目创建，显示名与桌面上的一致。
	/// </summary>
	internal static FenceItem Create(DesktopEntry entry)
	{
		var item = new FenceItem(entry.Key)
		{
			IsFolder = entry.IsFolder,
			IsVirtual = !entry.IsFileSystem,
		};
		if (!item.IsVirtual)
		{
			item.TypeName = ReadTypeName(entry.Key);
		}
		item.Update(entry);
		return item;
	}

	/// <summary>
	/// 用桌面视图的最新信息更新（显示名会随「隐藏已知文件扩展名」设置变化），返回内容是否发生变化（变化时缩略图需要重新加载）。
	/// </summary>
	internal bool Update(DesktopEntry entry)
	{
		DisplayName = entry.DisplayName;
		CanRename = entry.CanRename;
		CanDelete = entry.CanDelete;
		if (IsVirtual)
		{
			bool iconChanged = IconKey != entry.IconKey;
			IconKey = entry.IconKey;
			return iconChanged;
		}
		FileSystemInfo info = IsFolder ? new DirectoryInfo(FullPath) : new FileInfo(FullPath);
		return info.Exists && Refresh(info);
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

	static string ReadTypeName(string path)
	{
		var shellInfo = new SHFILEINFO();
		return NativeMethods.SHGetFileInfo(path, 0, ref shellInfo, (uint)Marshal.SizeOf<SHFILEINFO>(), NativeMethods.SHGFI_TYPENAME) != IntPtr.Zero
				? shellInfo.szTypeName
				: string.Empty;
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
