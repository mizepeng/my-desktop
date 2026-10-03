using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyDesktop.Core;
using MyDesktop.Models;

namespace MyDesktop.Services;

internal static class SettingsStore
{
	static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		// 中文按原样写出，便于直接查看配置文件
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new JsonStringEnumConverter() },
	};

	public static AppSettings Load(out bool isFirstRun)
	{
		var file = AppPaths.SettingsFile;
		isFirstRun = !File.Exists(file);
		if (isFirstRun)
		{
			return new AppSettings();
		}
		try
		{
			return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? new AppSettings();
		}
		catch (Exception ex)
		{
			Log.Error("配置文件读取失败，已备份原文件并使用默认设置", ex);
			try
			{
				File.Copy(file, $"{file}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
			}
			catch (IOException)
			{
				// 备份失败不影响启动
			}
			return new AppSettings();
		}
	}

	/// <summary>
	/// 先写临时文件再替换，避免写到一半断电导致配置损坏。
	/// </summary>
	public static void Save(AppSettings settings)
	{
		Directory.CreateDirectory(AppPaths.DataDir);
		var file = AppPaths.SettingsFile;
		var temp = file + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
		File.Move(temp, file, true);
	}
}
