using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyDesktop.WinUI.Native;

namespace MyDesktop.WinUI;

internal static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args is ["--command", "exit"] or ["--command", "exit", "--data", _])
		{
			try
			{
				InstanceCommands.ExitRunning(args.Length == 4 ? args[3] : null);
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
				return 1;
			}
		}
		if (args.Contains("--guard", StringComparer.Ordinal))
		{
			try
			{
				return IconRecovery.Run(args);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
				return 1;
			}
		}
		StartApplication();
		return Environment.ExitCode;
	}

	static void StartApplication()
	{
		WinRT.ComWrappersSupport.InitializeComWrappers();
		Application.Start(parameters =>
		{
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			_ = new App();
		});
	}
}
