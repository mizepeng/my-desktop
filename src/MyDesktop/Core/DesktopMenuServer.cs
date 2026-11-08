using System.Runtime.InteropServices;
using MyDesktop.Models;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 桌面右键菜单扩展的 COM 服务进程（MyDesktop.exe --shell-extension）。
/// 由 Windows 按扩展包里的注册以包身份按需启动，向 Explorer 提供 MyDesktop 子菜单，点击后把命令转交给正在运行的 MyDesktop。
/// 不加载界面；MyDesktop 在运行时一直保留，之后的右键菜单可以立即弹出。
/// </summary>
internal static class DesktopMenuServer
{
	public const string Argument = "--shell-extension";

	/// <summary>
	/// 与扩展包清单（ShellExtension\AppxManifest.xml）中的 com:Class 和 desktop5:Verb 保持一致。
	/// </summary>
	public static readonly Guid Clsid = new("54873389-EF3D-41F1-8659-877D58CE2CBD");

	/// <summary>
	/// 主程序托盘窗口的标题，用来判断 MyDesktop 是否在运行、向它查询状态。
	/// </summary>
	public const string MainWindowTitle = "MyDesktopTrayHost";

	// 启动后至少保留这么久，供触发本次启动的那个菜单使用
	static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);
	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
	const uint StateQueryTimeoutMs = 300;

	const int E_NOTIMPL = unchecked((int)0x80004001);
	const uint ECS_ENABLED = 0x0;
	const uint ECS_DISABLED = 0x1;
	const uint ECS_HIDDEN = 0x2;
	const uint ECS_CHECKED = 0x8;
	const uint ECS_RADIOCHECK = 0x10;
	const uint ECF_DEFAULT = 0x0;
	const uint ECF_HASSUBCOMMANDS = 0x1;
	const uint ECF_ISSEPARATOR = 0x8;
	const uint ECF_SEPARATORBEFORE = 0x20;

	public static void Run()
	{
		// 在 MTA 线程注册类对象，Explorer 的调用由 COM 线程池处理，不依赖消息循环
		var thread = new Thread(Serve) { Name = "DesktopMenuServer" };
		thread.Start();
		thread.Join();
	}

	static IntPtr FindMainWindow() => FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, MainWindowTitle);

	static void Serve()
	{
		var clsid = Clsid;
		int hr = CoRegisterClassObject(ref clsid, new ClassFactory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);
		if (hr != 0)
		{
			Log.Warn($"注册桌面右键菜单服务失败：0x{hr:X8}");
			return;
		}
		Thread.Sleep(GracePeriod);
		while (FindMainWindow() != IntPtr.Zero)
		{
			Thread.Sleep(PollInterval);
		}
		CoRevokeClassObject(cookie);
	}

	/// <summary>
	/// 向主程序查询「双击桌面隐藏」的状态、是否在桌面右键菜单中显示；主程序没在运行或没及时响应时返回 false。
	/// </summary>
	static bool TryQueryState(out HideTarget target, out bool doubleClickEnabled, out bool desktopMenu)
	{
		target = HideTarget.All;
		doubleClickEnabled = false;
		desktopMenu = false;
		var window = FindMainWindow();
		return window != IntPtr.Zero
				&& SendMessageTimeout(window, RegisterWindowMessage(AppCommands.StateMessageName), IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG,
						StateQueryTimeoutMs, out var result) != IntPtr.Zero
				&& AppCommands.TryDecodeState((int)result, out target, out doubleClickEnabled, out desktopMenu);
	}

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class ClassFactory : IClassFactory
	{
		const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

		public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
		{
			result = IntPtr.Zero;
			if (outer != IntPtr.Zero)
			{
				return CLASS_E_NOAGGREGATION;
			}
			var unknown = Marshal.GetIUnknownForObject(new RootCommand());
			try
			{
				return Marshal.QueryInterface(unknown, in iid, out result);
			}
			finally
			{
				Marshal.Release(unknown);
			}
		}

		public int LockServer(bool doLock) => 0;
	}

	/// <summary>
	/// 菜单项的公共实现：标题固定，默认无图标、可用、无子菜单，点击不做事。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	abstract class MenuCommand(string title) : IExplorerCommand
	{
		public int GetTitle(IntPtr items, out IntPtr name)
		{
			name = Marshal.StringToCoTaskMemUni(title);
			return 0;
		}

		public virtual int GetIcon(IntPtr items, out IntPtr icon)
		{
			icon = IntPtr.Zero;
			return E_NOTIMPL;
		}

		public int GetToolTip(IntPtr items, out IntPtr infoTip)
		{
			infoTip = IntPtr.Zero;
			return E_NOTIMPL;
		}

		public virtual int GetCanonicalName(out Guid name)
		{
			name = Guid.Empty;
			return E_NOTIMPL;
		}

		public virtual int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			state = ECS_ENABLED;
			return 0;
		}

		public virtual int Invoke(IntPtr items, IntPtr bindContext) => 0;

		public virtual int GetFlags(out uint flags)
		{
			flags = ECF_DEFAULT;
			return 0;
		}

		public virtual int EnumSubCommands(out IntPtr enumerator)
		{
			enumerator = IntPtr.Zero;
			return E_NOTIMPL;
		}

		protected static int Enumerate(IExplorerCommand[] commands, out IntPtr enumerator)
		{
			enumerator = Marshal.GetComInterfaceForObject(new CommandEnumerator(commands), typeof(IEnumExplorerCommand));
			return 0;
		}
	}

	/// <summary>
	/// 桌面右键菜单里的「MyDesktop」子菜单，只在桌面空白处右键、且 MyDesktop 正在运行时显示。
	/// 实现 IObjectWithSite：新版 Windows 11 询问状态时不传选中项，只能从站点取文件夹视图判断是不是桌面。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class RootCommand() : MenuCommand("MyDesktop"), IObjectWithSite
	{
		const uint SIGDN_FILESYSPATH = 0x80058000;
		const int E_FAIL = unchecked((int)0x80004005);

		// 站点判断失败时只记一次日志，避免每次右键都写
		static int _siteFailureLogged;

		object? _site;

		// 新版右键菜单（询问状态时不传选中项）不显示扩展的第二级子菜单、也不认 ECF_SEPARATORBEFORE，
		// 「双击桌面隐藏」的选项直接展开在本菜单里，分隔线改用单独的分隔项
		bool _flatten;

		public int SetSite(object? site)
		{
			_site = site;
			return 0;
		}

		public int GetSite(ref Guid iid, out IntPtr site)
		{
			site = IntPtr.Zero;
			if (_site == null)
			{
				return E_FAIL;
			}
			var unknown = Marshal.GetIUnknownForObject(_site);
			try
			{
				return Marshal.QueryInterface(unknown, in iid, out site);
			}
			finally
			{
				Marshal.Release(unknown);
			}
		}

		public override int GetIcon(IntPtr items, out IntPtr icon)
		{
			icon = Marshal.StringToCoTaskMemUni($"{Environment.ProcessPath},0");
			return 0;
		}

		public override int GetCanonicalName(out Guid name)
		{
			name = Clsid;
			return 0;
		}

		public override int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			// 扩展注册在「目录背景」上，文件夹空白处右键也会询问，这里只放行桌面。
			// 设置里关掉桌面右键菜单后立即隐藏：后台注销扩展包要好几秒，已经启动的本服务进程也可能还在被资源管理器调用；
			// 主程序一时没应答时照常显示
			_flatten = items == IntPtr.Zero;
			bool shown = (items == IntPtr.Zero ? IsDesktopSite() : IsDesktop(items)) && FindMainWindow() != IntPtr.Zero && !(TryQueryState(out _, out _, out bool desktopMenu) && !desktopMenu);
			state = shown ? ECS_ENABLED : ECS_HIDDEN;
			return 0;
		}

		public override int GetFlags(out uint flags)
		{
			flags = ECF_HASSUBCOMMANDS;
			return 0;
		}

		public override int EnumSubCommands(out IntPtr enumerator)
		{
			// 子菜单生成时取一次主程序状态，用来勾选「双击桌面隐藏」的当前选项
			bool known = TryQueryState(out var target, out bool doubleClickEnabled, out _);
			HideTarget? current = known ? target : null;
			IExplorerCommand[] actions =
			[
				new ActionCommand("新建分区", AppCommand.NewFence, false),
				new ActionCommand("新建文件夹映射分区…", AppCommand.NewPortalFence, false),
				new ActionCommand("一键整理桌面…", AppCommand.Organize, false),
			];
			if (!_flatten)
			{
				return Enumerate(
				[
					.. actions,
					new DoubleClickMenu(current, doubleClickEnabled),
					new ActionCommand("设置…", AppCommand.ShowSettings, true),
				], out enumerator);
			}
			// 新版右键菜单也不认 ECF_SEPARATORBEFORE，改用单独的分隔项
			var choices = Enum.GetValues<HideTarget>()
					.Select(t => (IExplorerCommand)new ChoiceCommand(t, t == current, $"双击桌面隐藏：{t.DisplayName()}", doubleClickEnabled));
			return Enumerate(
			[
				.. actions,
				new SeparatorCommand(),
				.. choices,
				new SeparatorCommand(),
				new ActionCommand("设置…", AppCommand.ShowSettings, false),
			], out enumerator);
		}

		static bool IsDesktop(IntPtr items)
		{
			var array = (IShellItemArray)Marshal.GetObjectForIUnknown(items);
			try
			{
				array.GetCount(out uint count);
				if (count != 1)
				{
					return false;
				}
				array.GetItemAt(0, out var item);
				try
				{
					item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
					return PathUtil.AreEqual(path, AppPaths.Desktop);
				}
				finally
				{
					Marshal.ReleaseComObject(item);
				}
			}
			catch (COMException)
			{
				// 没有文件系统路径的虚拟文件夹（此电脑、库等）
				return false;
			}
			finally
			{
				Marshal.ReleaseComObject(array);
			}
		}

		/// <summary>
		/// Windows 11 26300 起，询问状态时不再传选中项（items 为 null），桌面和文件夹空白处都一样；
		/// 改从站点按 SID_SFolderView 取文件夹视图，看当前文件夹是不是桌面。
		/// </summary>
		bool IsDesktopSite()
		{
			string? path = null;
			try
			{
				path = GetSiteFolderPath();
			}
			catch (COMException ex)
			{
				LogSiteFailure($"0x{ex.HResult:X8}");
				return false;
			}
			if (path == null)
			{
				LogSiteFailure(_site == null ? "没有站点" : "站点不提供文件夹视图");
				return false;
			}
			return PathUtil.AreEqual(path, AppPaths.Desktop);
		}

		string? GetSiteFolderPath()
		{
			if (_site is not IOleServiceProvider provider)
			{
				return null;
			}
			var service = typeof(IFolderView).GUID;
			var iid = typeof(IFolderView).GUID;
			if (provider.QueryService(ref service, ref iid, out var viewPtr) != 0 || viewPtr == IntPtr.Zero)
			{
				return null;
			}
			var view = (IFolderView)Marshal.GetObjectForIUnknown(viewPtr);
			Marshal.Release(viewPtr);
			try
			{
				var itemIid = typeof(IShellItem).GUID;
				int hr = view.GetFolder(ref itemIid, out var itemPtr);
				if (hr != 0)
				{
					Marshal.ThrowExceptionForHR(hr);
				}
				var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
				Marshal.Release(itemPtr);
				try
				{
					item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
					return path;
				}
				catch (COMException)
				{
					// 没有文件系统路径的虚拟文件夹（此电脑、库等），不是桌面
					return string.Empty;
				}
				finally
				{
					Marshal.ReleaseComObject(item);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(view);
			}
		}

		static void LogSiteFailure(string reason)
		{
			if (Interlocked.Exchange(ref _siteFailureLogged, 1) == 0)
			{
				Log.Warn($"桌面右键菜单：没有传入选中项，也无法从站点取得当前文件夹（{reason}），菜单不显示");
			}
		}
	}

	/// <summary>
	/// 点击后把命令广播给正在运行的 MyDesktop。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class ActionCommand(string title, AppCommand command, bool separatorBefore) : MenuCommand(title)
	{
		public override int Invoke(IntPtr items, IntPtr bindContext)
		{
			AppCommands.Broadcast(command);
			return 0;
		}

		public override int GetFlags(out uint flags)
		{
			flags = separatorBefore ? ECF_SEPARATORBEFORE : ECF_DEFAULT;
			return 0;
		}
	}

	/// <summary>
	/// 单独的分隔项，供不认 ECF_SEPARATORBEFORE 的新版右键菜单使用。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class SeparatorCommand() : MenuCommand(string.Empty)
	{
		public override int GetFlags(out uint flags)
		{
			flags = ECF_ISSEPARATOR;
			return 0;
		}
	}

	/// <summary>
	/// 「双击桌面隐藏」三选一子菜单；没有启用双击隐藏（或查询不到状态）时置灰。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class DoubleClickMenu(HideTarget? current, bool enabled) : MenuCommand("双击桌面隐藏")
	{
		public override int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			state = enabled ? ECS_ENABLED : ECS_DISABLED;
			return 0;
		}

		public override int GetFlags(out uint flags)
		{
			flags = ECF_HASSUBCOMMANDS | ECF_SEPARATORBEFORE;
			return 0;
		}

		public override int EnumSubCommands(out IntPtr enumerator)
		{
			var choices = Enum.GetValues<HideTarget>().Select(t => (IExplorerCommand)new ChoiceCommand(t, t == current, t.DisplayName(), true)).ToArray();
			return Enumerate(choices, out enumerator);
		}
	}

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class ChoiceCommand(HideTarget target, bool selected, string title, bool enabled) : MenuCommand(title)
	{
		/// <summary>
		/// Explorer 把带单选标记的项一律画成选中，所以只给当前选项加标记。
		/// 展开在 MyDesktop 菜单里时，没有启用双击隐藏就置灰。
		/// </summary>
		public override int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			state = (selected ? ECS_RADIOCHECK | ECS_CHECKED : ECS_ENABLED) | (enabled ? ECS_ENABLED : ECS_DISABLED);
			return 0;
		}

		public override int Invoke(IntPtr items, IntPtr bindContext)
		{
			AppCommands.Broadcast(AppCommands.ForDoubleClickTarget(target));
			return 0;
		}
	}

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class CommandEnumerator(IExplorerCommand[] commands) : IEnumExplorerCommand
	{
		const int S_FALSE = 1;
		int _index;

		public int Next(uint count, IntPtr output, IntPtr fetched)
		{
			int written = 0;
			while (written < count && _index < commands.Length)
			{
				Marshal.WriteIntPtr(output, written * IntPtr.Size, Marshal.GetComInterfaceForObject(commands[_index], typeof(IExplorerCommand)));
				written++;
				_index++;
			}
			if (fetched != IntPtr.Zero)
			{
				Marshal.WriteInt32(fetched, written);
			}
			return written == count ? 0 : S_FALSE;
		}

		public int Skip(uint count)
		{
			_index = (int)Math.Min(commands.Length, _index + count);
			return _index < commands.Length ? 0 : S_FALSE;
		}

		public int Reset()
		{
			_index = 0;
			return 0;
		}

		public int Clone(out IntPtr enumerator)
		{
			enumerator = IntPtr.Zero;
			return E_NOTIMPL;
		}
	}
}
