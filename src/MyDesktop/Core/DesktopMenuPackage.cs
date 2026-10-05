using System.Diagnostics;
using System.Text;

namespace MyDesktop.Core;

/// <summary>
/// 桌面右键菜单扩展包（与 MyDesktop.exe 同目录的 MyDesktop.DesktopMenu.msix）的注册与注销。
/// 包以「外部位置」方式指向 exe 所在目录，注册后 Windows 11 的新式菜单和「显示更多选项」中都会出现 MyDesktop 子菜单。
/// 注册、注销调用 PowerShell 的 Add-AppxPackage / Remove-AppxPackage，耗时一两秒，应在后台线程调用。
/// </summary>
internal static class DesktopMenuPackage
{
	const string PackageName = "MyDesktop.DesktopMenu";

	static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

	static string PackagePath => Path.Combine(AppDirectory, PackageName + ".msix");

	/// <summary>
	/// 记录已注册的「exe 目录 | 包文件时间」，两者都没变时不重复注册。包按用户注册，记录放在与数据目录无关的位置。
	/// </summary>
	static string MarkerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyDesktop", "desktop-menu-package.txt");

	/// <returns>扩展包已注册时返回 true；没有扩展包文件或注册失败（如签名证书未受信任）时返回 false。</returns>
	public static bool Register()
	{
		if (!File.Exists(PackagePath))
		{
			return false;
		}
		var marker = $"{AppDirectory}|{File.GetLastWriteTimeUtc(PackagePath).Ticks}";
		var previous = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null;
		if (previous == marker)
		{
			return true;
		}
		// 一律装到系统盘：「新应用的保存位置」设成其他盘时，包的数据目录会放在那个盘上、原位置只留目录联接，
		// 之后更新或注销时系统拒绝穿过这些联接（0x800701C0 不受信任的装入点），包就再也更新不了。
		// 升级时原地更新，不先注销；只有 exe 换了目录（同版本的包换了外部位置时 Add-AppxPackage 不会更新），
		// 或者已注册的包的数据在其他盘上时才先注销，后者要等重启后才能注册成功，期间由注册表里的菜单顶替
		bool moved = previous != null && !previous.StartsWith(AppDirectory + "|", StringComparison.OrdinalIgnoreCase);
		var script = "$system = @{}; $volume = Get-AppxVolume | Where-Object IsSystemVolume | Select-Object -First 1; if ($volume) { $system.Volume = $volume }; "
				+ $"$package = Get-AppxPackage -Name {PackageName}; "
				+ "$state = if ($package) { Join-Path $env:LOCALAPPDATA \"Packages\\$($package.PackageFamilyName)\\LocalState\" }; "
				+ "$elsewhere = $state -and (Test-Path $state) -and ((Get-Item $state -Force).Attributes -band [IO.FileAttributes]::ReparsePoint); "
				+ $"if ($package -and ({(moved ? "$true" : "$false")} -or $elsewhere)) {{ $package | Remove-AppxPackage }}; "
				+ $"Add-AppxPackage -Path {Quote(PackagePath)} -ExternalLocation {Quote(AppDirectory)} @system -ForceUpdateFromAnyVersion -ForceApplicationShutdown";
		if (!RunPowerShell(script, out var error))
		{
			Log.Warn($"注册桌面右键菜单扩展包失败：{error}");
			return false;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
		File.WriteAllText(MarkerPath, marker);
		Log.Info("已注册桌面右键菜单扩展包");
		return true;
	}

	/// <summary>
	/// 只注销本程序注册过的扩展包。
	/// </summary>
	public static void Unregister()
	{
		if (!File.Exists(MarkerPath))
		{
			return;
		}
		if (!RunPowerShell($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage", out var error))
		{
			Log.Warn($"注销桌面右键菜单扩展包失败：{error}");
			return;
		}
		File.Delete(MarkerPath);
		Log.Info("已注销桌面右键菜单扩展包");
	}

	static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

	static bool RunPowerShell(string script, out string error)
	{
		// 出错信息改用 UTF-8 输出到标准输出，避免中文系统代码页导致乱码
		var wrapped = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ErrorActionPreference = 'Stop'; "
				+ $"try {{ {script}; exit 0 }} catch {{ [Console]::Out.Write($_.Exception.Message); exit 1 }}";
		var info = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", wrapped })
		{
			info.ArgumentList.Add(argument);
		}
		using var process = Process.Start(info)!;
		error = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		return process.ExitCode == 0;
	}
}
