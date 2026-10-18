using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using MyDesktop.Core;
using MyDesktop.Services;
using MyDesktop.WinUI.Services;

namespace MyDesktop.WinUI;

public partial class App : Application
{
	Mutex? _mutex;
	bool _ownsMutex;
	DesktopSession? _session;

	public App()
	{
		InitializeComponent();
		UnhandledException += (_, args) => Log.Error("WinUI 界面发生未处理异常", args.Exception);
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		StartupOptions? options = null;
		try
		{
			options = StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
			if (options.DataDirectory != null)
			{
				AppPaths.UseDataDir(options.DataDirectory);
			}
			if (options.PreviewFile == null)
			{
				var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToUpperInvariant())))[..16];
				// 与现有入口共用互斥体，同一份配置不能被两个界面实例同时保存。
				_mutex = new Mutex(true, $"Local\\MyDesktop-{hash}", out _ownsMutex);
				if (!_ownsMutex)
				{
					throw new InvalidOperationException("这份配置已有 MyDesktop 实例在运行。请先退出该实例，或使用独立的 --data 数据目录。");
				}
				Log.Init(AppPaths.DataDir);
			}
			var settings = options.PreviewFile == null ? SettingsStore.Load(out _) : SettingsStore.ReadFrom(options.PreviewFile);
			_session = new DesktopSession(settings, options, Shutdown);
			_session.Start();
		}
		catch (Exception ex)
		{
			Log.Error("WinUI 启动失败", ex);
			if (options?.IsProbe == true)
			{
				File.AppendAllText(options.ReportFile!, ex + Environment.NewLine);
			}
			else
			{
				Native.WindowApi.ShowError(ex.Message);
			}
			Environment.ExitCode = 1;
			Shutdown();
		}
	}

	void Shutdown()
	{
		_session?.Dispose();
		_session = null;
		if (_ownsMutex)
		{
			_mutex!.ReleaseMutex();
			_ownsMutex = false;
		}
		_mutex?.Dispose();
		_mutex = null;
		Exit();
	}
}
