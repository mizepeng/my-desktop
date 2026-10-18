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

	/// <summary>
	/// 每次保存时替换下来的上一版配置，配置文件读不出来时用它恢复。
	/// </summary>
	static string PreviousFile => AppPaths.SettingsFile + ".bak";

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
			return Read(file);
		}
		catch (Exception ex)
		{
			Log.Error("配置文件读取失败，已另存原文件", ex);
			try
			{
				File.Copy(file, $"{file}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
			}
			catch (IOException)
			{
				// 另存失败不影响启动
			}
		}
		// 用上次保存时留下的上一版恢复，并写回配置文件，免得下次保存把坏文件换成上一版；上一版也读不出来才用默认设置
		if (File.Exists(PreviousFile))
		{
			try
			{
				var settings = Read(PreviousFile);
				File.Copy(PreviousFile, file, true);
				Log.Warn("已从上一版配置（settings.json.bak）恢复");
				return settings;
			}
			catch (Exception ex)
			{
				Log.Error("上一版配置也读取失败，使用默认设置", ex);
			}
		}
		return new AppSettings();
	}

	static AppSettings Read(string file)
	{
		return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? throw new InvalidDataException($"配置文件内容为空：{file}");
	}

	/// <summary>
	/// 先写临时文件并确保写进磁盘，再替换配置文件、把替换下来的上一版留作 .bak：写到一半断电不会留下坏掉的配置，
	/// 万一配置文件还是坏了，启动时用上一版恢复。
	/// </summary>
	public static void Save(AppSettings settings)
	{
		Directory.CreateDirectory(AppPaths.DataDir);
		var file = AppPaths.SettingsFile;
		var temp = file + ".tmp";
		using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			JsonSerializer.Serialize(stream, settings, Options);
			stream.Flush(true);
		}
		if (File.Exists(file))
		{
			File.Replace(temp, file, PreviousFile);
		}
		else
		{
			File.Move(temp, file);
		}
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
