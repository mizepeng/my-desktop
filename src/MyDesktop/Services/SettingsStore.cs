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

	/// <summary>
	/// 把配置写到指定文件（备份、导出用）。
	/// </summary>
	public static void SaveTo(AppSettings settings, string file)
	{
		File.WriteAllText(file, JsonSerializer.Serialize(settings, Options));
	}

	/// <summary>
	/// 读取备份或导出的配置文件；不是 MyDesktop 的配置文件时抛出异常，免得误选的文件把配置清空。
	/// </summary>
	public static AppSettings ReadFrom(string file)
	{
		var text = File.ReadAllText(file);
		try
		{
			using (var document = JsonDocument.Parse(text))
			{
				if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(nameof(AppSettings.Fences), out _))
				{
					throw new InvalidDataException("这不是 MyDesktop 的配置文件");
				}
			}
			return JsonSerializer.Deserialize<AppSettings>(text, Options) ?? throw new InvalidDataException("这不是 MyDesktop 的配置文件");
		}
		catch (JsonException ex)
		{
			throw new InvalidDataException("这不是 MyDesktop 的配置文件，或者文件已损坏", ex);
		}
	}
}
