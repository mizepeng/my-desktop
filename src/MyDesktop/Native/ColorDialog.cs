using System.Runtime.InteropServices;
using System.Windows.Media;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 系统的「颜色」对话框（ChooseColor），分区背景颜色和图标高亮的「自定义颜色…」共用；
/// 对话框里的 16 个自定义颜色格子在本次运行期间保留。
/// </summary>
internal static class ColorDialog
{
	static readonly int[] CustomColors = new int[16];

	/// <summary>
	/// 弹出对话框，初始选中 initial；用户取消时返回 null。
	/// </summary>
	public static Color? Pick(IntPtr owner, Color initial)
	{
		var buffer = Marshal.AllocHGlobal(CustomColors.Length * sizeof(int));
		try
		{
			Marshal.Copy(CustomColors, 0, buffer, CustomColors.Length);
			var choose = new CHOOSECOLOR
			{
				lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
				hwndOwner = owner,
				rgbResult = initial.R | (initial.G << 8) | (initial.B << 16),
				lpCustColors = buffer,
				Flags = CC_RGBINIT | CC_FULLOPEN,
			};
			if (!ChooseColor(ref choose))
			{
				return null;
			}
			Marshal.Copy(buffer, CustomColors, 0, CustomColors.Length);
			return Color.FromRgb((byte)(choose.rgbResult & 0xFF), (byte)((choose.rgbResult >> 8) & 0xFF), (byte)((choose.rgbResult >> 16) & 0xFF));
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}
}
