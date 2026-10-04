using MyDesktop.Core;

namespace MyDesktop;

internal static class Program
{
	[STAThread]
	static void Main(string[] args)
	{
		// 系统为桌面右键菜单按需启动的服务进程：不加载 WPF，尽快响应 Explorer
		if (args.Contains(DesktopMenuServer.Argument, StringComparer.OrdinalIgnoreCase))
		{
			Log.Init(AppPaths.DataDir);
			DesktopMenuServer.Run();
			return;
		}
		// 接管桌面图标期间的守护进程：主程序被强制结束时恢复资源管理器的桌面图标
		if (args.Contains(DesktopGuard.Argument, StringComparer.OrdinalIgnoreCase))
		{
			DesktopGuard.Run(args);
			return;
		}
		var app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
