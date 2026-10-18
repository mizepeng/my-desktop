using System.ComponentModel;
using System.Runtime.InteropServices;
using MyDesktop.Native;

internal static class ClipboardChecks
{
	// 剪贴板属于窗口站，单独创建桌面仍会共用用户剪贴板。此检查只在新辅助进程的私有窗口站运行。
	public static void Run(string report)
	{
		nint previousStation = GetProcessWindowStation();
		nint previousDesktop = GetThreadDesktop(GetCurrentThreadId());
		nint station = CreateWindowStation("MyDesktopClipboard-" + Guid.NewGuid().ToString("N"), 1, 0x37F, 0);
		// 普通用户只能使用系统生成的站名；CREATE_ONLY 保证不会打开已有窗口站。
		if (station == 0 && Marshal.GetLastWin32Error() == 5) { station = CreateWindowStation(null, 1, 0x37F, 0); }
		if (station == 0)
		{
			int error = Marshal.GetLastWin32Error();
			if (error is not (5 or 183)) { throw new Win32Exception(error); }
			File.AppendAllText(report, $"未执行：原生剪贴板检查无法新建私有窗口站（错误 {error}），保留用户剪贴板，命令流程另用内存剪贴板验证。" + Environment.NewLine);
			return;
		}
		nint desktop = 0, owner = 0;
		try
		{
			if (!SetProcessWindowStation(station) || GetProcessWindowStation() != station) { throw new Win32Exception(); }
			desktop = CreateDesktop("Clipboard", 0, 0, 0, 0x1FF, 0);
			if (desktop == 0 || !SetThreadDesktop(desktop)) { throw new Win32Exception(); }
			owner = CreateWindowEx(0, "Static", "MyDesktopClipboard", 0, 0, 0, 0, 0, (nint)(-3), 0, 0, 0);
			if (owner == 0) { throw new Win32Exception(); }
			var clipboard = new FileClipboard();
			string[] paths = [@"F:\测试 文件\图片😀.png", @"F:\测试 文件\资料.txt"];
			clipboard.Write(owner, paths, false);
			var copied = clipboard.Read(owner) ?? throw new InvalidOperationException("没有读到复制文件。");
			Require(!copied.Cut && copied.Paths.SequenceEqual(paths), "Unicode CF_HDROP 复制路径不一致。");
			clipboard.Write(owner, paths, true);
			var cut = clipboard.Read(owner) ?? throw new InvalidOperationException("没有读到剪切文件。");
			Require(cut.Cut && cut.Paths.SequenceEqual(paths), "Preferred DropEffect 未保留剪切语义。");
			clipboard.Clear(owner, copied.Sequence);
			Require(clipboard.Read(owner)?.Sequence == cut.Sequence, "旧操作错误清除了新剪贴板。");
			clipboard.Clear(owner, cut.Sequence);
			Require(clipboard.Read(owner) == null, "已完成的剪切没有清空剪贴板。");
			File.AppendAllText(report, "通过：私有窗口站验证 Unicode 文件剪贴板、剪切标记及按序号清理，未访问用户剪贴板。" + Environment.NewLine);
		}
		finally
		{
			if (owner != 0) { DestroyWindow(owner); }
			SetProcessWindowStation(previousStation);
			SetThreadDesktop(previousDesktop);
			if (desktop != 0) { CloseDesktop(desktop); }
			CloseWindowStation(station);
		}
	}

	static void Require(bool condition, string message)
	{
		if (!condition) { throw new InvalidOperationException(message); }
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern nint CreateWindowStation(string? name, uint flags, uint access, nint security);
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool SetProcessWindowStation(nint station);
	[DllImport("user32.dll")]
	static extern nint GetProcessWindowStation();
	[DllImport("user32.dll")]
	static extern bool CloseWindowStation(nint station);
	[DllImport("kernel32.dll")]
	static extern uint GetCurrentThreadId();
	[DllImport("user32.dll")]
	static extern nint GetThreadDesktop(uint thread);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool SetThreadDesktop(nint desktop);
	[DllImport("user32.dll")]
	static extern bool CloseDesktop(nint desktop);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height,
		nint parent, nint menu, nint instance, nint parameter);
	[DllImport("user32.dll")]
	static extern bool DestroyWindow(nint window);
}
