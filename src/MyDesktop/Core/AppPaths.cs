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

	public static void UseDataDir(string directory)
	{
		DataDir = Path.GetFullPath(directory);
	}
}
