using System.Runtime.InteropServices;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Verification;

internal static class DesktopFixtureProtocol
{
	public const string FloorClass = "MyDesktopVerificationFloor";
	public const string ControlClass = "MyDesktopVerificationControl";
	public const int DesktopProbeMessage = 0x8050;
	public static (RECT WorkArea, double Scale) PrimaryArea() => GetMonitorWorkArea(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY));

	[DllImport("user32.dll")]
	public static extern bool ShowWindow(nint hwnd, int command);
	[DllImport("user32.dll")]
	public static extern int GetMessage(out Message message, nint hwnd, uint minimum, uint maximum);
	[DllImport("user32.dll")]
	public static extern bool TranslateMessage(in Message message);
	[DllImport("user32.dll")]
	public static extern nint DispatchMessage(in Message message);

	[StructLayout(LayoutKind.Sequential)]
	public struct Message
	{
		public nint Hwnd;
		public uint Id;
		public nuint WParam;
		public nint LParam;
		public uint Time;
		public POINT Point;
	}
}

internal enum DesktopProbeCommand
{
	OpenApplication = 1,
	RaiseDesktop,
	LowerDesktop,
	RemoveDesktop,
	RecreateDesktop,
	CloseApplication,
}
