using System.Runtime.InteropServices;
using MyDesktop.Native;
using MyDesktop.Verification;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Native;

internal sealed record DesktopIconTarget(nint Root, nint List, uint ProcessId, bool Fixture)
{
	public static DesktopIconTarget? Find(bool fixture)
	{
		var root = ExplorerDesktopWindow.FindRoot();
		if (root == 0)
		{
			return null;
		}
		string name = GetClassName(root);
		if (fixture ? name != DesktopFixtureProtocol.FloorClass || DesktopInput.IsInputDesktop() : name is not ("Progman" or "WorkerW"))
		{
			throw new InvalidOperationException("桌面目标与运行模式不符，拒绝隐藏任何系统窗口。");
		}
		var view = FindWindowEx(root, 0, "SHELLDLL_DefView", null);
		var list = FindWindowEx(view, 0, "SysListView32", null);
		if (list == 0)
		{
			return null;
		}
		GetWindowThreadProcessId(root, out uint process);
		return new DesktopIconTarget(root, list, process, fixture);
	}

	public bool IsCurrent
	{
		get
		{
			GetWindowThreadProcessId(List, out uint process);
			return process == ProcessId && GetClassName(List) == "SysListView32" && GetClassName(GetParent(List)) == "SHELLDLL_DefView"
				&& GetAncestor(List, 2) == Root && (Fixture ? GetClassName(Root) == DesktopFixtureProtocol.FloorClass : GetClassName(Root) is "Progman" or "WorkerW");
		}
	}

	public bool HiddenBySystem => Fixture ? GetProp(Root, DesktopFixtureProtocol.HiddenIconsProperty) != 0 : ExplorerDesktopWindow.IconsHiddenBySystem();
	public bool Visible => IsCurrent && IsWindowVisible(List);

	public bool Show(bool visible) => IsCurrent && ShowWindowAsync(List, visible ? SW_SHOWNA : SW_HIDE);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	static extern nint GetProp(nint hwnd, string name);
}
