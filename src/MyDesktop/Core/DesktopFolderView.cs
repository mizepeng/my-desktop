using System.Runtime.InteropServices;
using MyDesktop.Native;

namespace MyDesktop.Core;

/// <summary>
/// 获取 Explorer 当前的桌面视图；调用方必须在自己的 STA 线程上使用并释放返回的 COM 对象。
/// </summary>
internal static class DesktopFolderView
{
	public static void Release(IFolderView2 view) => Marshal.ReleaseComObject(view);

	public static IFolderView2? Open()
	{
		var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
		try
		{
			object location = 0;
			object root = null!;
			Marshal.ThrowExceptionForHR(windows.FindWindowSW(ref location, ref root, 8, out _, 1, out var dispatch));
			if (dispatch == null)
			{
				return null;
			}
			try
			{
				var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
				var iid = typeof(IShellBrowser).GUID;
				Marshal.ThrowExceptionForHR(((IComServiceProvider)dispatch).QueryService(ref service, ref iid, out var pointer));
				if (pointer == IntPtr.Zero)
				{
					throw new InvalidOperationException("Explorer 没有返回桌面浏览器接口。");
				}
				var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pointer);
				Marshal.Release(pointer);
				try
				{
					Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var view));
					if (view is IFolderView2 folderView)
					{
						return folderView;
					}
					if (view != null)
					{
						Marshal.ReleaseComObject(view);
					}
					throw new InvalidOperationException("Explorer 桌面视图不支持 IFolderView2。");
				}
				finally
				{
					Marshal.ReleaseComObject(browser);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(dispatch);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(windows);
		}
	}
}
