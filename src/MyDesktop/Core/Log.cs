using System.Diagnostics;

namespace MyDesktop.Core;

/// <summary>
/// 简单文件日志：写入数据目录下的 app.log，启动时超过 1MB 则轮转。
/// </summary>
internal static class Log
{
	const long MaxSize = 1024 * 1024;
	static readonly object Gate = new();
	static string? _file;

	public static void Init(string directory)
	{
		try
		{
			Directory.CreateDirectory(directory);
			_file = Path.Combine(directory, "app.log");
			var info = new FileInfo(_file);
			if (info.Exists && info.Length > MaxSize)
			{
				File.Move(_file, _file + ".1", true);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"日志初始化失败：{ex}");
			_file = null;
		}
	}

	public static void Info(string message) => Write("INFO", message, null);

	public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

	public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

	static void Write(string level, string message, Exception? exception)
	{
		var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
		if (exception != null)
		{
			line += Environment.NewLine + exception;
		}
		Debug.WriteLine(line);
		var file = _file;
		if (file == null)
		{
			return;
		}
		lock (Gate)
		{
			try
			{
				File.AppendAllText(file, line + Environment.NewLine);
			}
			catch (Exception)
			{
				// 日志写失败（被占用、无权限等）不影响主流程，也不能让原生回调里抛出异常
			}
		}
	}
}
