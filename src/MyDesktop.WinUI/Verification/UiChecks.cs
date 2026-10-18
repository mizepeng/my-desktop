using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Services;
using MyDesktop.Verification;
using MyDesktop.WinUI.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class UiChecks
{
	static StreamWriter? _report;
	internal static void Write(string message)
	{
		if (_report == null)
		{
			throw new InvalidOperationException("隔离验证报告尚未打开。");
		}
		_report.WriteLine(message);
	}

	public static async Task RunAsync(DesktopSession session, string reportPath)
	{
		using var report = new StreamWriter(reportPath, false) { AutoFlush = true };
		_report = report;
		try
		{
			var floor = FindWindowEx(0, 0, DesktopFixtureProtocol.FloorClass, null);
			Require(floor != 0, "只能在独立测试桌面运行");
			GetWindowThreadProcessId(floor, out uint floorProcess);
			Require(floorProcess != Environment.ProcessId, "底层窗口来自另一个进程");
			Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "PresentationFramework"), "新入口没有加载 WPF 框架");
			int monitorCount = 0;
			EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref RECT bounds, nint data) => { monitorCount++; return true; }, 0);
			Require(session.Surfaces.Count == monitorCount && session.Surfaces.Sum(surface => surface.Fences.Count) == 2, "每屏一个宿主，三个真实配置分区投影成两个视图");
			try
			{
				await session.Takeover!.FirstActive.WaitAsync(TimeSpan.FromSeconds(10));
			}
			catch (TimeoutException)
			{
				throw new InvalidOperationException("隔离桌面接管未就绪：" + string.Join("；", session.Surfaces.Select(surface => surface.Notice)));
			}
			var surface = session.Surfaces.First(surface => surface.Fences.Count > 0);
			var hwnd = surface.Handle;
			var foreground = GetForegroundWindow();
			var frame = surface.Fences[0];
			await frame.ContentReady.WaitAsync(TimeSpan.FromSeconds(10));
			Require(frame.DisplayedPaths.Count == 1 && frame.ContentStatus == "", "桌面分区已读取真实文件与 Shell 图像");
			frame.UpdateLayout();
			Require(Descendants(frame.ItemList).OfType<Image>().Any(image => image.Source is WriteableBitmap), "真实 Shell 图像已绑定到 XAML 图像控件");
			long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
			Require((style & (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_APPWINDOW)) == 0, "无 CPU 分层窗口、整窗穿透或常驻置顶");
			Require((style & WS_EX_NOACTIVATE) != 0 && (style & WS_EX_TOOLWINDOW) != 0, "宿主不激活且不显示任务栏图标");
			var origin = new POINT(0, 0);
			ClientToScreen(hwnd, ref origin);
			var windowBounds = GetWindowRect(hwnd);
			Require(origin.X == windowBounds.Left && origin.Y == windowBounds.Top, "客户区原点与窗口一致");
			Require(RootAt(origin.X + 20, origin.Y + 20) == floor, "分区之外的空白保持穿透");
			var bounds = surface.BoundsOf(frame);
			Require(RootAt(origin.X + bounds.Left + bounds.Width / 2, origin.Y + bounds.Top + 20) == hwnd, "分区标题命中宿主");
			var visual = frame.PanelVisual;
			var second = frame.Frame.Members[1];
			frame.ActivateTab(second.Id);
			await frame.ContentReady.WaitAsync(TimeSpan.FromSeconds(10));
			Require(frame.Frame.Active.Id == second.Id && frame.DisplayedPaths.Count == 2, "切换标签后读取真实映射文件夹");
			Require(ReferenceEquals(visual, frame.PanelVisual) && surface.Handle == hwnd && surface.Fences.Count == 2, "切换标签复用父视觉与宿主，不创建隐藏分区窗口");
			frame.SetRolledUp(true);
			Require(RootAt(origin.X + bounds.Left + bounds.Width / 2, origin.Y + bounds.Top + bounds.Height - 20) == floor, "收起后释放内容区输入");
			frame.SetRolledUp(false);
			frame.TranslateTo(24, 20);
			frame.CommitPosition();
			await visual.Compositor.RequestCommitAsync();
			await Task.Delay(400);
			var saved = SettingsStore.ReadFrom(AppPaths.SettingsFile);
			double scale = GetDpiForWindow(hwnd) / 96.0;
			Require(saved.Groups[0].Active == second.Id, "活动标签已通过共享存储保存");
			Require(saved.Fences[0].X == (int)Math.Round(windowBounds.Left + bounds.Left + 24 * scale), "拖动位置以物理像素保存");
			Require(saved.Fences[0].X == saved.Fences[1].X && saved.Fences[0].Y == saved.Fences[1].Y, "组内位置同步保存");
			Require(RootAt(origin.X + bounds.Left + 5, origin.Y + bounds.Top + 100) == floor, "移动后释放旧位置");
			Require(GetForegroundWindow() == foreground, "加载、切换与保存未改变前台窗口");
			int visible = 0;
			EnumWindows((window, _) =>
			{
				GetWindowThreadProcessId(window, out uint process);
				if (process == Environment.ProcessId && IsWindowVisible(window))
				{
					string name = GetClassName(window);
					var rectangle = GetWindowRect(window);
					Write($"可见顶层窗口：{name}，范围 {rectangle.Left},{rectangle.Top},{rectangle.Width},{rectangle.Height}，样式 0x{GetWindowLongPtr(window, GWL_EXSTYLE):X}。");
					if (name is not ("UAC_InputIndicatorOverlayWnd" or "UAC Input Indicator" or "Shell_InputSwitchTopLevelWindow"))
					{
						visible++;
					}
				}
				return true;
			}, 0);
			Require(visible == session.Surfaces.Count, "除输入指示器外每屏只有一个可见顶层窗口");
			await DesktopPlacementChecks.RunAsync(hwnd);
			await RenameChecks.RunAsync(session);
			await DesktopPlacementChecks.RunAsync(hwnd);
			await TakeoverChecks.RunAsync(session);
			await FileChangeChecks.RunAsync(session);
			Write("通过：分区、标签、改名、文件监视、散放图标、接管恢复与层级回归。未操作实际桌面或注入输入。");
		}
		finally
		{
			_report = null;
		}
	}

	static nint RootAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT(x, y)), 2);

	internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
	{
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
		{
			var child = VisualTreeHelper.GetChild(parent, index);
			yield return child;
			foreach (var descendant in Descendants(child))
			{
				yield return descendant;
			}
		}
	}

	static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
		Write("通过：" + message);
	}

	[DllImport("user32.dll")]
	static extern bool ClientToScreen(nint hwnd, ref POINT point);
}
