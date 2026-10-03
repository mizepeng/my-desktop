namespace MyDesktop.Core;

internal static class AppPaths
{
	static readonly string DefaultDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyDesktop");

	/// <summary>
	/// 配置与日志所在目录，可通过命令行 --data 指定（便于测试或便携使用）。
	/// </summary>
	public static string DataDir { get; private set; } = DefaultDataDir;

	public static bool IsCustomDataDir => !PathUtil.AreEqual(DataDir, DefaultDataDir);

	public static string SettingsFile => Path.Combine(DataDir, "settings.json");

	public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

	public static string CommonDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

	/// <summary>
	/// 托管分区的默认存储目录：与桌面文件夹同级（桌面在 OneDrive 中时也随之位于 OneDrive），
	/// 保证与桌面同盘，拖入分区时的移动是瞬时完成的。
	/// </summary>
	public static string DefaultStorageRoot
	{
		get
		{
			var desktop = PathUtil.Normalize(Desktop);
			return Path.Combine(Path.GetDirectoryName(desktop) ?? desktop, "桌面分区");
		}
	}

	public static void UseDataDir(string directory)
	{
		DataDir = Path.GetFullPath(directory);
	}
}
