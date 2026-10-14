using System.Runtime.InteropServices;
using MyDesktop.Native;

namespace MyDesktop.Core;

/// <summary>
/// 由资源管理器执行打开操作，让外部程序不继承 MyDesktop 的 Job 限制和提权状态。
/// 每次重新获取桌面对象，资源管理器重启后不会继续使用失效的 COM 代理。
/// </summary>
internal static class ExplorerShell
{
	static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

	public static void Open(string path, string workingDirectory)
	{
		var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;
		object? desktop = null;
		object? document = null;
		object? application = null;
		try
		{
			object location = 0;
			object root = null!;
			int result = windows.FindWindowSW(ref location, ref root, 8, out _, 1, out var dispatch);
			desktop = dispatch;
			Marshal.ThrowExceptionForHR(result);
			if (result != 0 || desktop == null)
			{
				throw new InvalidOperationException("资源管理器的桌面尚未就绪，请稍后重试");
			}
			document = ((dynamic)desktop).Document;
			application = ((dynamic)document).Application;
			((dynamic)application).ShellExecute(path, Type.Missing, workingDirectory, Type.Missing, 1);
		}
		finally
		{
			foreach (var value in new[] { application, document, desktop, windows })
			{
				if (value != null)
				{
					Marshal.ReleaseComObject(value);
				}
			}
		}
	}
}
