namespace MyDesktop.WinUI.Services;

internal sealed record StartupOptions(string? DataDirectory, string? PreviewFile, string? ReportFile, bool IsProbe, bool IsCrashProbe)
{
	public static StartupOptions Parse(string[] args)
	{
		string? data = null, preview = null, report = null;
		bool probe = false, crash = false;
		for (int index = 0; index < args.Length; index++)
		{
			if (args[index] is "--probe" or "--probe-crash")
			{
				probe = true;
				crash = args[index] == "--probe-crash";
				continue;
			}
			string name = args[index];
			if (name is not ("--data" or "--preview" or "--report") || ++index == args.Length)
			{
				throw new ArgumentException($"命令行参数无效或缺少值：{name}");
			}
			string path = Path.GetFullPath(args[index]);
			switch (name)
			{
				case "--data": data = path; break;
				case "--preview": preview = path; break;
				case "--report": report = path; break;
			}
		}
		if (probe && (data == null || report == null || preview != null))
		{
			throw new ArgumentException("隔离验证必须明确指定 --data 和 --report，不能使用正式配置或预览模式。");
		}
		return new StartupOptions(data, preview, report, probe, crash);
	}
}
