using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 一批 Shell 变更通知合并后的结果：All 为真时图标全部重新加载，否则只重新加载 Items 里的项目和 Folders 里各目录下的项目。
/// </summary>
internal sealed record IconChanges(bool All, IReadOnlySet<string> Items, IReadOnlySet<string> Folders);

/// <summary>
/// 在隐藏的消息窗口上接收 Shell 变更通知（SHChangeNotifyRegister），资源管理器就是靠它刷新角标的：
/// 角标软件状态变了（git 提交、网盘同步完成）时调 SHChangeNotify 发 SHCNE_UPDATEITEM / SHCNE_UPDATEDIR，文件本身不变。
/// 通知常一次来一大批（git checkout、批量同步），合并后统一触发 Changed。
/// </summary>
internal sealed class ShellChangeWatcher : IDisposable
{
	const int WM_SHELLCHANGE = WM_APP + 1;
	const int Events = SHCNE_UPDATEITEM | SHCNE_UPDATEDIR | SHCNE_UPDATEIMAGE | SHCNE_ASSOCCHANGED;

	readonly HwndSource _window;
	readonly DispatcherTimer _timer;
	readonly uint _registration;
	bool _all;
	HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);
	HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

	public event Action<IconChanges>? Changed;

	public ShellChangeWatcher()
	{
		var parameters = new HwndSourceParameters("MyDesktopShellChange")
		{
			Width = 0,
			Height = 0,
			WindowStyle = 0,
			// 只收消息的窗口
			ParentWindow = new IntPtr(-3),
		};
		_window = new HwndSource(parameters);
		_window.AddHook(WndProc);
		// 以管理员身份运行时，放行资源管理器（普通权限）投递的通知
		ChangeWindowMessageFilterEx(_window.Handle, WM_SHELLCHANGE, MSGFLT_ALLOW, IntPtr.Zero);
		// 首条通知后等一会儿再统一处理，不顺延：通知持续不断时也能定时跟上
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_timer.Tick += (_, _) => Flush();

		if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out var desktop) != 0)
		{
			throw new InvalidOperationException("取不到桌面的 PIDL，无法注册 Shell 变更通知");
		}
		try
		{
			// 以命名空间的根（桌面）递归注册，收到所有位置的通知
			var entry = new SHChangeNotifyEntry { pidl = desktop, fRecursive = true };
			_registration = SHChangeNotifyRegister(_window.Handle, SHCNRF_ShellLevel | SHCNRF_NewDelivery, Events, WM_SHELLCHANGE, 1, ref entry);
		}
		finally
		{
			ILFree(desktop);
		}
		if (_registration == 0)
		{
			_window.Dispose();
			throw new InvalidOperationException("注册 Shell 变更通知失败");
		}
	}

	IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg != WM_SHELLCHANGE)
		{
			return IntPtr.Zero;
		}
		handled = true;
		var lockHandle = SHChangeNotification_Lock(wParam, (uint)lParam.ToInt64(), out var pidls, out int eventId);
		if (lockHandle == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		try
		{
			if ((eventId & (SHCNE_UPDATEIMAGE | SHCNE_ASSOCCHANGED)) != 0)
			{
				_all = true;
			}
			else if ((eventId & (SHCNE_UPDATEITEM | SHCNE_UPDATEDIR)) != 0 && PathOf(Marshal.ReadIntPtr(pidls)) is string path)
			{
				((eventId & SHCNE_UPDATEDIR) != 0 ? _folders : _items).Add(path);
			}
			else
			{
				return IntPtr.Zero;
			}
		}
		finally
		{
			SHChangeNotification_Unlock(lockHandle);
		}
		if (!_timer.IsEnabled)
		{
			_timer.Start();
		}
		return IntPtr.Zero;
	}

	/// <summary>
	/// 文件系统项目的路径；不在文件系统里的（控制面板等）返回 null。
	/// </summary>
	static string? PathOf(IntPtr pidl)
	{
		if (pidl == IntPtr.Zero)
		{
			return null;
		}
		var path = new StringBuilder(260);
		return SHGetPathFromIDList(pidl, path) && path.Length > 0 ? path.ToString().TrimEnd('\\') : null;
	}

	void Flush()
	{
		_timer.Stop();
		var changes = new IconChanges(_all, _items, _folders);
		_all = false;
		_items = new(StringComparer.OrdinalIgnoreCase);
		_folders = new(StringComparer.OrdinalIgnoreCase);
		Changed?.Invoke(changes);
	}

	public void Dispose()
	{
		_timer.Stop();
		SHChangeNotifyDeregister(_registration);
		_window.Dispose();
	}
}
