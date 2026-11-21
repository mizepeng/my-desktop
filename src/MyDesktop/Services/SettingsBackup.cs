using System.Globalization;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Views;

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
	ApplyAppearance,
	ResetAll,
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
		BackupReason.ApplyAppearance => "外观应用到所有分区前自动备份",
		BackupReason.ResetAll => "恢复全部默认设置前自动备份",
	};
}

/// <summary>
/// 配置备份：整份配置（全部分区与设置）。一键整理、删除分区、恢复配置、外观应用到所有分区、恢复全部默认设置之前自动备份，也可以手动备份；只保留最近 30 份。
/// </summary>
internal static class SettingsBackup
{
	const int MaxCount = 30;
	const string TimeFormat = "yyyyMMdd-HHmmss-fff";

	static string Folder => Path.Combine(AppPaths.DataDir, "backups");

	/// <summary>
	/// 写一份备份；写不进去时弹窗说明原因，返回 false。
	/// </summary>
	public static bool Create(AppSettings settings, BackupReason reason)
	{
		if (Write(settings, reason) is not Exception ex)
		{
			return true;
		}
		MessageDialog.Show("备份配置", $"备份当前配置失败：{ex.Message}\n\n请检查数据目录 {Folder} 是否可写。", "确定");
		return false;
	}

	/// <summary>
	/// 会改动配置的操作之前的自动备份，返回是否继续这次操作。
	/// 备份写不进去时弹窗说明：canContinue 为 true 时（操作可以撤销或影响小）让用户选择不备份继续，
	/// 否则直接停止，免得操作后没有恢复点。
	/// </summary>
	public static bool CreateBefore(AppSettings settings, BackupReason reason, string title, bool canContinue)
	{
		if (Write(settings, reason) is not Exception ex)
		{
			return true;
		}
		if (canContinue)
		{
			return MessageDialog.Show(title, $"自动备份当前配置失败：{ex.Message}\n\n继续的话，这次操作之前没有备份可以恢复。", "不备份，继续", "取消") == 0;
		}
		MessageDialog.Show(title, $"自动备份当前配置失败，为了安全这次没有继续：{ex.Message}\n\n"
				+ $"可以检查数据目录 {Folder} 是否可写，或先在设置 →「备份」里用「导出当前配置」另存一份。", "确定");
		return false;
	}

	/// <summary>
	/// 写入新备份并清理超出份数的旧备份；返回写入新备份时的异常，成功为 null。
	/// 清理旧备份失败只记日志、不算失败：新备份已经写好了。
	/// </summary>
	static Exception? Write(AppSettings settings, BackupReason reason)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			SettingsStore.SaveTo(settings, Path.Combine(Folder, $"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{reason}.json"));
		}
		catch (Exception ex)
		{
			Log.Warn("备份配置失败", ex);
			return ex;
		}
		try
		{
			foreach (var old in List().Skip(MaxCount))
			{
				File.Delete(old.File);
			}
		}
		catch (Exception ex)
		{
			Log.Warn("清理旧备份失败", ex);
		}
		return null;
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
