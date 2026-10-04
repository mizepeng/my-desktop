using System.Runtime.InteropServices;
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
	/// 主程序托盘窗口的标题，用来判断 MyDesktop 是否在运行。
	/// </summary>
	public const string MainWindowTitle = "MyDesktopTrayHost";

	// 启动后至少保留这么久，供触发本次启动的那个菜单使用
	static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);
	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

	public static void Run()
	{
		// 在 MTA 线程注册类对象，Explorer 的调用由 COM 线程池处理，不依赖消息循环
		var thread = new Thread(Serve) { Name = "DesktopMenuServer" };
		thread.Start();
		thread.Join();
	}

	public static bool IsMainInstanceRunning() => FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, MainWindowTitle) != IntPtr.Zero;

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
		while (IsMainInstanceRunning())
		{
			Thread.Sleep(PollInterval);
		}
		CoRevokeClassObject(cookie);
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
	/// 桌面右键菜单里的「MyDesktop」子菜单，只在桌面空白处右键、且 MyDesktop 正在运行时显示。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class RootCommand : IExplorerCommand
	{
		const int E_NOTIMPL = unchecked((int)0x80004001);
		const uint ECS_ENABLED = 0;
		const uint ECS_HIDDEN = 2;
		const uint ECF_HASSUBCOMMANDS = 0x1;
		const uint SIGDN_FILESYSPATH = 0x80058000;

		public int GetTitle(IntPtr items, out IntPtr name)
		{
			name = Marshal.StringToCoTaskMemUni("MyDesktop");
			return 0;
		}

		public int GetIcon(IntPtr items, out IntPtr icon)
		{
			icon = Marshal.StringToCoTaskMemUni($"{Environment.ProcessPath},0");
			return 0;
		}

		public int GetToolTip(IntPtr items, out IntPtr infoTip)
		{
			infoTip = IntPtr.Zero;
			return E_NOTIMPL;
		}

		public int GetCanonicalName(out Guid name)
		{
			name = Clsid;
			return 0;
		}

		public int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			// 扩展注册在「目录背景」上，文件夹空白处右键也会询问，这里只放行桌面
			state = IsDesktop(items) && IsMainInstanceRunning() ? ECS_ENABLED : ECS_HIDDEN;
			return 0;
		}

		public int Invoke(IntPtr items, IntPtr bindContext) => 0;

		public int GetFlags(out uint flags)
		{
			flags = ECF_HASSUBCOMMANDS;
			return 0;
		}

		public int EnumSubCommands(out IntPtr enumerator)
		{
			IExplorerCommand[] commands =
			[
				new SubCommand("新建分区", AppCommand.NewFence, false),
				new SubCommand("一键整理桌面…", AppCommand.Organize, false),
				new SubCommand("隐藏/显示桌面图标和分区", AppCommand.ToggleHidden, false),
				new SubCommand("设置…", AppCommand.ShowSettings, true),
			];
			enumerator = Marshal.GetComInterfaceForObject(new CommandEnumerator(commands), typeof(IEnumExplorerCommand));
			return 0;
		}

		static bool IsDesktop(IntPtr items)
		{
			if (items == IntPtr.Zero)
			{
				return false;
			}
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
	}

	/// <summary>
	/// 子菜单中的一项，点击后把命令广播给正在运行的 MyDesktop。
	/// </summary>
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class SubCommand(string title, AppCommand command, bool separatorBefore) : IExplorerCommand
	{
		const int E_NOTIMPL = unchecked((int)0x80004001);
		const uint ECS_ENABLED = 0;
		const uint ECF_DEFAULT = 0;
		const uint ECF_SEPARATORBEFORE = 0x20;

		public int GetTitle(IntPtr items, out IntPtr name)
		{
			name = Marshal.StringToCoTaskMemUni(title);
			return 0;
		}

		public int GetIcon(IntPtr items, out IntPtr icon)
		{
			icon = IntPtr.Zero;
			return E_NOTIMPL;
		}

		public int GetToolTip(IntPtr items, out IntPtr infoTip)
		{
			infoTip = IntPtr.Zero;
			return E_NOTIMPL;
		}

		public int GetCanonicalName(out Guid name)
		{
			name = Guid.Empty;
			return E_NOTIMPL;
		}

		/// <summary>
		/// 子项拿不到右键位置（参数为空），显示与否已由父菜单决定。
		/// </summary>
		public int GetState(IntPtr items, bool okToBeSlow, out uint state)
		{
			state = ECS_ENABLED;
			return 0;
		}

		public int Invoke(IntPtr items, IntPtr bindContext)
		{
			AppCommands.Broadcast(command);
			return 0;
		}

		public int GetFlags(out uint flags)
		{
			flags = separatorBefore ? ECF_SEPARATORBEFORE : ECF_DEFAULT;
			return 0;
		}

		public int EnumSubCommands(out IntPtr enumerator)
		{
			enumerator = IntPtr.Zero;
			return E_NOTIMPL;
		}
	}

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	sealed class CommandEnumerator(IExplorerCommand[] commands) : IEnumExplorerCommand
	{
		const int S_FALSE = 1;
		const int E_NOTIMPL = unchecked((int)0x80004001);
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
