using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MyDesktop.Core;

/// <summary>
/// 开机自启：通过当前用户的 Run 项在登录 Windows 后启动，不要求提前于资源管理器启动。
/// 旧版本的登录计划任务在程序启动时迁回 Run 项，保留启动参数和 Windows 中的禁用状态。
/// </summary>
internal static class AutoStart
{
	public const string Argument = "--autostart";
	const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
	const string ValueName = "MyDesktop";
	const string TaskPrefix = "MyDesktop 开机启动";
	const int TaskNotFound = unchecked((int)0x80070002);

	// 迁移失败时旧任务仍负责自启，设置界面和关闭操作也要识别它。
	static bool _legacyTaskEnabled;
	static bool _migrationAttempted;

	static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("取不到当前程序的路径");

	static string TaskName => $"{TaskPrefix}（{Environment.UserName}）";

	public static bool IsEnabled()
	{
		MigrateTask();
		return _legacyTaskEnabled || RunValueExists() && IsRunApproved();
	}

	public static void SetEnabled(bool enabled)
	{
		MigrateTask();
		if (enabled)
		{
			// 引号前的末尾反斜杠要成对写入，否则盘符根目录会把结束引号变成参数内容。
			var dataDir = Regex.Replace(AppPaths.DataDir, @"\\+$", "$0$0");
			var arguments = Argument + (AppPaths.IsCustomDataDir ? $" --data \"{dataDir}\"" : string.Empty);
			WriteRunValue(arguments);
			// 用户明确开启时解除 Windows 的禁用；自动迁移不改这个状态。
			DeleteApprovalValue();
		}
		if (_legacyTaskEnabled)
		{
			UseTaskFolder(folder =>
			{
				folder.DeleteTask(TaskName, 0);
				return true;
			});
			_legacyTaskEnabled = false;
		}
		if (!enabled)
		{
			using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
			key?.DeleteValue(ValueName, false);
			DeleteApprovalValue();
		}
	}

	/// <summary>
	/// 先写入 Run 项，再删除本用户的旧登录任务；迁移失败时保留旧任务，下次启动再试。
	/// 已禁用的任务不创建 Run 项，已有的 Run 项和 Windows 禁用记录保持原样。
	/// </summary>
	public static void MigrateTask()
	{
		if (_migrationAttempted)
		{
			return;
		}
		_migrationAttempted = true;
		try
		{
			UseTaskFolder(folder =>
			{
				dynamic task;
				try
				{
					task = folder.GetTask(TaskName);
				}
				catch (Exception ex) when (ex.HResult == TaskNotFound)
				{
					return false;
				}
				try
				{
					var xml = XDocument.Parse((string)task.Xml);
					XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
					var action = xml.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
					if (!string.Equals((string?)action?.Element(ns + "Command"), ExePath, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					_legacyTaskEnabled = (bool)task.Enabled;
					if (_legacyTaskEnabled && !RunValueExists())
					{
						// 任务的 Arguments 可省略；原参数中的自定义数据目录也原样迁移。
						WriteRunValue((string?)action?.Element(ns + "Arguments") ?? string.Empty);
					}
					folder.DeleteTask(TaskName, 0);
					_legacyTaskEnabled = false;
					Log.Info("开机自启已从登录计划任务迁移到当前用户的 Run 项");
					return true;
				}
				finally
				{
					Marshal.ReleaseComObject(task);
				}
			});
		}
		catch (Exception ex)
		{
			Log.Warn("开机自启迁移到 Run 项失败，保留旧计划任务，下次启动再试", ex);
		}
	}

	static bool RunValueExists()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKey);
		return key?.GetValue(ValueName) is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
	}

	static bool IsRunApproved()
	{
		using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey);
		var value = key?.GetValue(ValueName);
		if (value == null)
		{
			return true;
		}
		if (value is not byte[] { Length: >= 4 } data)
		{
			throw new InvalidDataException("Windows 开机自启状态的格式不正确");
		}
		// StartupApproved 的状态最低位为 1 表示禁用（3、7），为 0 表示启用（2、6）。
		return (BitConverter.ToUInt32(data, 0) & 1) == 0;
	}

	static void WriteRunValue(string arguments)
	{
		var command = $"\"{ExePath}\" {arguments}".TrimEnd();
		if (command.Length > 260)
		{
			throw new InvalidOperationException("开机自启命令超过 Windows Run 项的 260 字符限制，请缩短程序路径或数据目录");
		}
		using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
		key.SetValue(ValueName, command, RegistryValueKind.String);
	}

	static void DeleteApprovalValue()
	{
		using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey, true);
		key?.DeleteValue(ValueName, false);
	}

	/// <summary>
	/// 任务计划程序的 COM 接口仅用于迁移、清理旧版本留下的任务。
	/// </summary>
	static T UseTaskFolder<T>(Func<dynamic, T> action)
	{
		var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("系统中没有任务计划程序");
		dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法连接任务计划程序");
		try
		{
			service.Connect();
			dynamic folder = service.GetFolder(@"\");
			try
			{
				return action(folder);
			}
			finally
			{
				Marshal.ReleaseComObject(folder);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(service);
		}
	}
}
