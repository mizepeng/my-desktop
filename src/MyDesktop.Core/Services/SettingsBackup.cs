using System.Globalization;
using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 备份的起因。
/// </summary>
public enum BackupReason
{
	Manual,
	Organize,
	DeleteFence,
	Restore,
}

/// <summary>
/// 一份备份：数据目录 backups 下的一个配置文件，文件名记着备份时间和起因。
/// </summary>
public sealed record BackupEntry(string File, DateTime Time, BackupReason Reason)
{
	public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");

	public string ReasonText => Reason switch
	{
		BackupReason.Manual => "手动备份",
		BackupReason.Organize => "一键整理前自动备份",
		BackupReason.DeleteFence => "删除分区前自动备份",
		BackupReason.Restore => "恢复配置前自动备份",
	};
}

/// <summary>
/// 配置备份：整份配置（全部分区与设置）。一键整理、删除分区、恢复配置之前自动备份，也可以手动备份；只保留最近 30 份。
/// </summary>
internal static class SettingsBackup
{
	const int MaxCount = 30;
	const string TimeFormat = "yyyyMMdd-HHmmss-fff";

	static string Folder => Path.Combine(AppPaths.DataDir, "backups");

	/// <summary>
	/// 备份失败只记日志，不影响接下来的操作。
	/// </summary>
	public static void Create(AppSettings settings, BackupReason reason)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			SettingsStore.SaveTo(settings, Path.Combine(Folder, $"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{reason}.json"));
			foreach (var old in List().Skip(MaxCount))
			{
				File.Delete(old.File);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("备份配置失败", ex);
		}
	}

	/// <summary>
	/// 全部备份，新的在前。
	/// </summary>
	public static List<BackupEntry> List()
	{
		if (!Directory.Exists(Folder))
		{
			return [];
		}
		var entries = new List<BackupEntry>();
		foreach (var file in Directory.GetFiles(Folder, "*.json"))
		{
			var name = Path.GetFileNameWithoutExtension(file);
			if (name.Length > TimeFormat.Length + 1
					&& DateTime.TryParseExact(name[..TimeFormat.Length], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
					&& Enum.TryParse<BackupReason>(name[(TimeFormat.Length + 1)..], out var reason)
					&& Enum.IsDefined(reason))
			{
				entries.Add(new BackupEntry(file, time, reason));
			}
		}
		return entries.OrderByDescending(e => e.Time).ToList();
	}

	public static void Delete(BackupEntry entry)
	{
		try
		{
			File.Delete(entry.File);
		}
		catch (Exception ex)
		{
			Log.Warn($"删除备份失败：{entry.File}", ex);
		}
	}
}
