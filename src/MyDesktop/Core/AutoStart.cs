using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace MyDesktop.Core;

/// <summary>
/// 开机自启：登录 Windows 时由任务计划程序启动，只针对当前用户，不需要管理员权限。
/// 注册表 Run 项要等资源管理器按顺序逐个拉起（实测比资源管理器晚约 30 秒），计划任务在登录时立即启动、按普通优先级运行。
/// 旧版本写的 Run 项、安装包勾选「开机自动启动」时写的 Run 项，都在程序启动时换成计划任务。
/// </summary>
internal static class AutoStart
{
	public const string Argument = "--autostart";
	const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string ValueName = "MyDesktop";

	/// <summary>
	/// 计划任务名的前缀，每个用户一个任务；卸载程序按这个前缀删除全部用户的任务。
	/// </summary>
	const string TaskPrefix = "MyDesktop 开机启动";

	// ITaskFolder.RegisterTask 的 TASK_CREATE_OR_UPDATE 与 TASK_LOGON_INTERACTIVE_TOKEN
	const int CreateOrUpdate = 6;
	const int InteractiveToken = 3;

	// 任务不存在时任务计划程序返回 HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)，经动态调用抛出的是 FileNotFoundException，按 HResult 判断
	const int TaskNotFound = unchecked((int)0x80070002);

	static string ExePath => Environment.ProcessPath ?? string.Empty;

	static string TaskName => $"{TaskPrefix}（{Environment.UserName}）";

	public static bool IsEnabled() => TaskExists() || RunValueExists();

	public static void SetEnabled(bool enabled)
	{
		if (enabled)
		{
			RegisterTask();
		}
		else
		{
			DeleteTask();
		}
		// 开启时计划任务接替 Run 项，关闭时两者都不留
		DeleteRunValue();
	}

	/// <summary>
	/// 把旧版本或安装包写的 Run 项换成计划任务；换不成时保留 Run 项照旧开机启动，下次启动再试。
	/// </summary>
	public static void MigrateRunValue()
	{
		if (!RunValueExists())
		{
			return;
		}
		try
		{
			RegisterTask();
			DeleteRunValue();
			Log.Info("开机自启已从注册表 Run 项改为登录时运行的计划任务");
		}
		catch (Exception ex)
		{
			Log.Warn("开机自启改为计划任务失败，仍使用注册表 Run 项", ex);
		}
	}

	static bool RunValueExists()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKey);
		return key?.GetValue(ValueName) is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
	}

	static void DeleteRunValue()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
		key?.DeleteValue(ValueName, false);
	}

	/// <summary>
	/// 本用户的任务存在、并且启动的是当前这个程序（换过安装位置的旧任务不算）。
	/// </summary>
	static bool TaskExists()
	{
		try
		{
			return UseTaskFolder(folder =>
			{
				string xml = folder.GetTask(TaskName).Xml;
				return xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase);
			});
		}
		catch (Exception ex) when (ex.HResult == TaskNotFound)
		{
			return false;
		}
	}

	/// <summary>
	/// 登录时启动：普通优先级（任务默认是低于正常），不限运行时长、不因使用电池而不启动或被停止，已在运行时不再启动第二个。
	/// </summary>
	static void RegisterTask()
	{
		var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("取不到当前用户的 SID");
		var arguments = Argument + (AppPaths.IsCustomDataDir ? $" --data \"{AppPaths.DataDir}\"" : string.Empty);
		var xml = $"""
				<?xml version="1.0" encoding="UTF-16"?>
				<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
				  <RegistrationInfo>
				    <Description>登录 Windows 时启动 MyDesktop 桌面分区</Description>
				  </RegistrationInfo>
				  <Triggers>
				    <LogonTrigger>
				      <Enabled>true</Enabled>
				      <UserId>{user}</UserId>
				    </LogonTrigger>
				  </Triggers>
				  <Principals>
				    <Principal id="Author">
				      <UserId>{user}</UserId>
				      <LogonType>InteractiveToken</LogonType>
				      <RunLevel>LeastPrivilege</RunLevel>
				    </Principal>
				  </Principals>
				  <Settings>
				    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
				    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
				    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
				    <AllowHardTerminate>false</AllowHardTerminate>
				    <StartWhenAvailable>false</StartWhenAvailable>
				    <IdleSettings>
				      <StopOnIdleEnd>false</StopOnIdleEnd>
				      <RestartOnIdle>false</RestartOnIdle>
				    </IdleSettings>
				    <AllowStartOnDemand>true</AllowStartOnDemand>
				    <Enabled>true</Enabled>
				    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
				    <Priority>4</Priority>
				  </Settings>
				  <Actions Context="Author">
				    <Exec>
				      <Command>{SecurityElement.Escape(ExePath)}</Command>
				      <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
				    </Exec>
				  </Actions>
				</Task>
				""";
		UseTaskFolder(folder => folder.RegisterTask(TaskName, xml, CreateOrUpdate, null, null, InteractiveToken, null));
	}

	static void DeleteTask()
	{
		try
		{
			UseTaskFolder(folder => folder.DeleteTask(TaskName, 0));
		}
		catch (Exception ex) when (ex.HResult == TaskNotFound)
		{
			// 本来就没有
		}
	}

	/// <summary>
	/// 通过任务计划程序的 COM 接口操作根文件夹。
	/// </summary>
	static T UseTaskFolder<T>(Func<dynamic, T> action)
	{
		var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("系统中没有任务计划程序");
		dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法连接任务计划程序");
		try
		{
			service.Connect();
			return action(service.GetFolder(@"\"));
		}
		finally
		{
			Marshal.FinalReleaseComObject(service);
		}
	}
}
