using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MyDesktop.Core;
using MyDesktop.Models;
using MyDesktop.Services;
using MyDesktop.Views;

namespace MyDesktop;

public partial class App : Application
{
	/// <summary>
	/// 恢复配置后重新启动时带上：等原来的进程退出后再启动，不当作重复运行。
	/// </summary>
	const string RestartArgument = "--restart";

	Mutex? _mutex;
	bool _ownsMutex;

	public static new App Current => (App)Application.Current;

	internal FenceManager? Manager { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		var command = ParseArguments(e.Args);
		Log.Init(AppPaths.DataDir);
		if (command == AppCommand.Exit)
		{
			ExitRunningInstance();
			Shutdown();
			return;
		}
		bool restart = e.Args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);
		if (!AcquireSingleInstance(restart ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
		{
			// 已有实例在运行：把命令（默认打开设置）转交给它；开机自启重复拉起时（如旧的 Run 项还没迁移）什么也不做
			if (!e.Args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase))
			{
				AppCommands.Broadcast(command ?? AppCommand.ShowSettings);
			}
			Shutdown();
			return;
		}
		RegisterExceptionHandlers();
		if (DesktopHost.IsWindows10)
		{
			int failureReported = 0;
			ShellDesktopIcons.Failed += ex =>
			{
				if (Interlocked.Exchange(ref failureReported, 1) != 0)
				{
					return;
				}
				Dispatcher.BeginInvoke(() =>
				{
					MessageDialog.Show("Win10 黑屏测试版", $"无法使用系统图标隐藏方式，测试已中止。请恢复使用正式版，并把 app.log 发给我们。\n\n{ex.Message}", "确定");
					ExitApp();
				});
			};
		}
		SystemTheme.ApplyToMenus();
		Log.Info($"启动 {typeof(App).Assembly.GetName().Version}，数据目录：{AppPaths.DataDir}，管理员权限：{(Environment.IsPrivilegedProcess ? "是" : "否")}，"
				+ $".NET 运行时：{Environment.Version}，Windows：{WindowsVersion()}");
		Log.Info("运行 Win10 黑屏测试版 1：仅 Win10 改用 Shell 图标隐藏；黑屏是否改善仍待用户验证。");
		var settings = SettingsStore.Load(out bool firstRun);
		FenceManager.ApplyRenderMode(settings.HardwareAcceleration);
		Manager = new FenceManager(settings);
		StartWhenDesktopReady(Manager, firstRun, command);
	}

	public void ExitApp()
	{
		Manager?.Shutdown();
		Shutdown();
	}

	/// <summary>
	/// 按当前配置重新启动（设置里有些改动要重启才完全生效）。
	/// </summary>
	internal void Restart() => RestartWithSettings(Manager!.Settings);

	/// <summary>
	/// 换成另一份配置并重新启动：先正常退出（保存当前配置、恢复桌面图标），再写入新配置，由新进程等本进程退出后接着启动。
	/// </summary>
	internal void RestartWithSettings(AppSettings settings)
	{
		Manager?.Shutdown();
		try
		{
			SettingsStore.Save(settings);
			var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
			start.ArgumentList.Add(RestartArgument);
			if (AppPaths.IsCustomDataDir)
			{
				start.ArgumentList.Add("--data");
				start.ArgumentList.Add(AppPaths.DataDir);
			}
			Process.Start(start)?.Dispose();
		}
		catch (Exception ex)
		{
			Log.Error("重新启动失败", ex);
		}
		Shutdown();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		Manager?.Shutdown();
		if (_ownsMutex)
		{
			_mutex?.ReleaseMutex();
		}
		_mutex?.Dispose();
		base.OnExit(e);
	}

	protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
	{
		// 注销/关机时 WPF 会逐个关闭窗口，先有序关闭，避免分区窗口被当作意外关闭而重建
		Manager?.Shutdown();
		base.OnSessionEnding(e);
	}

	/// <summary>
	/// 开机自启时 Explorer 可能还没创建好桌面窗口，轮询等待（最多约一分钟）。
	/// </summary>
	static void StartWhenDesktopReady(FenceManager manager, bool firstRun, AppCommand? command)
	{
		void Start()
		{
			manager.Start(firstRun);
			if (command is AppCommand value)
			{
				manager.ExecuteCommand(value);
			}
		}
		if (DesktopHost.FindFolderView() != IntPtr.Zero)
		{
			Start();
			return;
		}
		int attempts = 0;
		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) =>
		{
			if (DesktopHost.FindFolderView() == IntPtr.Zero && ++attempts < 60)
			{
				return;
			}
			timer.Stop();
			Start();
		};
		timer.Start();
	}

	/// <summary>
	/// 解析 --data（数据目录）与 --command（桌面右键菜单发来的命令）。
	/// </summary>
	static AppCommand? ParseArguments(string[] args)
	{
		AppCommand? command = null;
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (string.Equals(args[i], "--data", StringComparison.OrdinalIgnoreCase))
			{
				AppPaths.UseDataDir(args[i + 1]);
			}
			else if (string.Equals(args[i], "--command", StringComparison.OrdinalIgnoreCase))
			{
				command = AppCommands.Parse(args[i + 1]);
			}
		}
		return command;
	}

	/// <summary>
	/// 按数据目录区分实例，便于用 --data 另起一个互不干扰的测试实例；已有实例在运行时最多等它 wait 这么久。
	/// </summary>
	bool AcquireSingleInstance(TimeSpan wait)
	{
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToUpperInvariant())))[..16];
		_mutex = new Mutex(true, $"Local\\MyDesktop-{hash}", out _ownsMutex);
		if (!_ownsMutex && wait > TimeSpan.Zero)
		{
			try
			{
				_ownsMutex = _mutex.WaitOne(wait);
			}
			catch (AbandonedMutexException)
			{
				// 对方没释放互斥体就结束了（如被强制结束），同样算已退出，此时本进程已取得互斥体
				_ownsMutex = true;
			}
		}
		return _ownsMutex;
	}

	/// <summary>
	/// 安装程序升级、卸载前调用（--command exit）：让正在运行的实例正常退出并恢复桌面图标，
	/// 等它释放单实例互斥体（最多 20 秒），再等本会话里其余的 MyDesktop 进程结束；不启动程序。
	/// </summary>
	void ExitRunningInstance()
	{
		// 广播给所有实例，用 --data 启动的实例也一并正常退出，安装程序才能替换文件
		AppCommands.Broadcast(AppCommand.Exit);
		AcquireSingleInstance(TimeSpan.FromSeconds(20));
		WaitForOtherProcesses();
	}

	/// <summary>
	/// 等本会话里其余的 MyDesktop 进程结束：主程序释放互斥体后还要收尾，守护进程随它结束；
	/// 右键菜单服务进程要在主程序消失几秒后才退出，到时还在的直接结束，免得安装程序报告文件被占用。
	/// </summary>
	static void WaitForOtherProcesses()
	{
		using var self = Process.GetCurrentProcess();
		var deadline = DateTime.UtcNow.AddSeconds(3);
		foreach (var process in Process.GetProcessesByName(self.ProcessName))
		{
			using (process)
			{
				try
				{
					if (process.Id == self.Id || process.SessionId != self.SessionId)
					{
						continue;
					}
					var remaining = deadline - DateTime.UtcNow;
					if (remaining <= TimeSpan.Zero || !process.WaitForExit(remaining))
					{
						process.Kill();
						process.WaitForExit(TimeSpan.FromSeconds(2));
					}
				}
				catch (Exception)
				{
					// 进程已经退出，或没有权限访问
				}
			}
		}
	}

	/// <summary>
	/// 带内部版本号的 Windows 版本，如 10.0.19045.6093：Environment.OSVersion 没有最后一段（UBR），要从注册表读。
	/// 读不到 UBR 时照实写明，不编一个数字。
	/// </summary>
	static string WindowsVersion()
	{
		var os = Environment.OSVersion.Version;
		using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
		return key?.GetValue("UBR") is int ubr
				? $"{os.Major}.{os.Minor}.{os.Build}.{ubr}"
				: $"{os.Major}.{os.Minor}.{os.Build}（读不到 UBR）";
	}

	void RegisterExceptionHandlers()
	{
		DispatcherUnhandledException += (_, e) =>
		{
			Log.Error("界面线程未处理的异常", e.Exception);
			e.Handled = true;
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			var exception = e.ExceptionObject as Exception;
			// 进程退出时 C++/CLI 混合模块（多半是加载进本进程的第三方 Shell 扩展）反初始化找不到 VC 运行库：本程序没有 C++/CLI，
			// 这时已在正常退出，不影响使用，记成警告，免得排查时当成崩溃（用户日志：升级时旧进程退出报 __std_type_info_destroy_list）
			if (exception is DllNotFoundException && exception.StackTrace?.Contains("ModuleUninitializer") == true)
			{
				Log.Warn("进程退出时第三方 C++/CLI 模块反初始化失败，不影响使用", exception);
			}
			else
			{
				Log.Error("未处理的异常，程序即将退出", exception);
			}
			// 进程即将崩溃，尽量把被隐藏的桌面图标恢复出来
			DesktopHost.RestoreIcons();
		};
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Log.Warn("后台任务未处理的异常", e.Exception);
			e.SetObserved();
		};
	}
}
