using System.ComponentModel;
using System.Diagnostics;
using MyDesktop.Verification;
using MyDesktop.WinUI.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Verification;

internal static class DesktopPlacementChecks
{
	public static async Task RunAsync(nint hwnd)
	{
		var controller = FindWindowEx(0, 0, DesktopFixtureProtocol.ControlClass, null);
		if (controller == 0)
		{
			throw new InvalidOperationException("桌面层级验证必须在 独立测试桌面中运行。");
		}
		var foreground = GetForegroundWindow();
		var desktop = FindWindowEx(0, 0, DesktopFixtureProtocol.FloorClass, null);
		var app = Command(controller, DesktopProbeCommand.OpenApplication);
		UiChecks.Write($"层级场景：宿主=0x{hwnd:X}，桌面=0x{desktop:X}，普通应用=0x{app:X}。");
		await WaitAsync(() => Above(app, hwnd) && Above(hwnd, desktop) && !Topmost(hwnd), "普通应用在宿主之上，宿主在桌面之上");
		Command(controller, DesktopProbeCommand.RaiseDesktop);
		await WaitAsync(() => Above(hwnd, desktop) && Above(desktop, app), "桌面提起后宿主仍在桌面之上");
		ForceTopmost(hwnd);
		Command(controller, DesktopProbeCommand.LowerDesktop);
		await WaitAsync(() => Above(app, hwnd) && Above(hwnd, desktop) && !Topmost(hwnd), "桌面放回后宿主回到应用下方并撤销置顶");
		var popup = CreateWindowEx((uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE), "Static", "隔离宿主弹出窗口", (uint)WS_POPUP,
			0, 0, 40, 40, hwnd, 0, GetModuleHandle(null), 0);
		if (popup == 0)
		{
			throw new Win32Exception();
		}
		try
		{
			WindowApi.ShowWindow(popup, SW_SHOWNA);
			for (int attempt = 0; attempt < 5; attempt++)
			{
				if (!SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
				{
					throw new Win32Exception();
				}
			}
			await WaitAsync(() => Above(popup, hwnd) && Above(hwnd, desktop) && Above(app, hwnd), "宿主弹出窗口不会被误选为层级参照");
		}
		finally
		{
			DestroyWindow(popup);
		}
		for (int cycle = 0; cycle < 20; cycle++)
		{
			Command(controller, DesktopProbeCommand.RaiseDesktop);
			await WaitAsync(() => Above(hwnd, desktop), "循环提起", false);
			ForceTopmost(hwnd);
			Command(controller, DesktopProbeCommand.LowerDesktop);
			await WaitAsync(() => Above(app, hwnd) && Above(hwnd, desktop) && !Topmost(hwnd), "循环放回", false);
		}
		UiChecks.Write("通过：20 次桌面提起与放回，没有残留置顶状态。");
		Command(controller, DesktopProbeCommand.RemoveDesktop);
		await WaitAsync(() => !IsWindowVisible(hwnd), "桌面不存在期间隐藏宿主");
		var replacement = Command(controller, DesktopProbeCommand.RecreateDesktop);
		if (replacement == desktop)
		{
			throw new InvalidOperationException("桌面重建未生成新的窗口句柄。");
		}
		await WaitAsync(() => IsWindowVisible(hwnd) && Above(app, hwnd) && Above(hwnd, replacement), "桌面重建后自动恢复宿主及层级");
		Command(controller, DesktopProbeCommand.CloseApplication);
		await WaitAsync(() => Above(hwnd, replacement) && !Topmost(hwnd), "关闭测试应用后宿主仍在桌面上方");
		if (GetWindow(hwnd, GW_OWNER) != 0 || GetParent(hwnd) != 0 || GetForegroundWindow() != foreground)
		{
			throw new InvalidOperationException("宿主建立了窗口从属关系或改变了前台窗口。");
		}
		UiChecks.Write("PASS：单宿主跨进程层级、临时置顶恢复、桌面销毁重建，不建立 owner/parent，不改变前台窗口。");
	}

	static nint Command(nint controller, DesktopProbeCommand command)
	{
		if (SendMessageTimeout(controller, DesktopFixtureProtocol.DesktopProbeMessage, (nint)command, 0, 2, 1000, out var result) == 0 || result == 0)
		{
			throw new Win32Exception($"隔离桌面命令失败：{command}");
		}
		return result;
	}

	static bool Topmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

	static void ForceTopmost(nint hwnd)
	{
		// 仅在隔离测试中绕过约束，模拟宿主已经进入置顶层，检查下一次事件能否撤回。
		if (!SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | 0x0400) || !Topmost(hwnd))
		{
			throw new InvalidOperationException("隔离测试未能把宿主放入置顶层。");
		}
	}

	static bool Above(nint upper, nint lower)
	{
		var order = new List<nint>();
		EnumWindows((window, _) =>
		{
			order.Add(window);
			return true;
		}, 0);
		int upperIndex = order.IndexOf(upper), lowerIndex = order.IndexOf(lower);
		return upperIndex >= 0 && lowerIndex >= 0 && upperIndex < lowerIndex;
	}

	static async Task WaitAsync(Func<bool> condition, string description, bool log = true)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			if (watch.ElapsedMilliseconds > 2000)
			{
				EnumWindows((window, _) =>
				{
					if (IsWindowVisible(window))
					{
						UiChecks.Write($"层级快照：0x{window:X} {GetClassName(window)}，置顶={Topmost(window)}，owner=0x{GetWindow(window, GW_OWNER):X}");
					}
					return true;
				}, 0);
				throw new InvalidOperationException($"桌面层级验证超时：{description}");
			}
			await Task.Delay(10);
		}
		if (log)
		{
			UiChecks.Write("通过：" + description);
		}
	}
}
