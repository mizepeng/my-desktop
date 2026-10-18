using System.Runtime.InteropServices;
using MyDesktop.Core;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Native;

/// <summary>
/// 从 Shell 回调取得真正完成的路径，重名、跳过或取消后不猜测目标文件名。
/// </summary>
internal static class ShellTransfer
{
	static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
	const uint Undo = 0x20000000, Recycle = 0x00080000, WarnPermanent = 0x4000;
	const uint Quiet = 0x0004 | 0x0010 | 0x0400 | 0x00100000;

	public static Task<FileOperationResult> RunAsync(nint owner, FileAction action, IReadOnlyList<string> paths, string? folder,
		bool quiet = false, bool recycle = true)
	{
		var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try { completion.SetResult(Run(owner, action, paths, folder, quiet, recycle)); }
			catch (Exception ex) { completion.SetException(ex); }
		}) { IsBackground = true, Name = "WinUI-FileOperation" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task;
	}

	static FileOperationResult Run(nint owner, FileAction action, IReadOnlyList<string> paths, string? folder, bool quiet, bool recycle)
	{
		var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass)!)!;
		var items = new List<IShellItem>();
		var sinks = new List<Progress>();
		IShellItem? destination = null;
		bool aborted = false;
		string? error = null;
		try
		{
			uint flags = quiet ? Quiet | 0x0008 | 0x00200000 : Undo;
			if (action == FileAction.Delete && recycle)
			{
				flags |= Recycle | WarnPermanent;
			}
			operation.SetOperationFlags(flags);
			operation.SetOwnerWindow(owner);
			if (action is FileAction.Copy or FileAction.Move)
			{
				if (folder == null || !Directory.Exists(folder))
				{
					throw new DirectoryNotFoundException("粘贴目标目录不存在。");
				}
				destination = Parse(folder);
			}
			foreach (string path in paths)
			{
				var item = Parse(path);
				items.Add(item);
				var sink = new Progress(path, folder, Directory.Exists(path));
				sinks.Add(sink);
				switch (action)
				{
					case FileAction.Copy: operation.CopyItem(item, destination!, null, sink); break;
					case FileAction.Move: operation.MoveItem(item, destination!, null, sink); break;
					case FileAction.Delete: operation.DeleteItem(item, sink); break;
				}
			}
			try
			{
				operation.PerformOperations();
			}
			catch (COMException ex)
			{
				// COM 边界外保留已完成的项目，调用方只同步这些结果；取消后的未完成项绝不补成功路径。
				aborted = true;
				error = ex.HResult == unchecked((int)0x800704C7) ? null : ex.Message;
			}
			operation.GetAnyOperationsAborted(out bool stopped);
			aborted |= stopped;
			var completed = sinks.Select(sink => sink.Completed).OfType<CompletedFile>().ToArray();
			error ??= sinks.Select(sink => sink.Error).FirstOrDefault(message => message != null);
			return new FileOperationResult(completed, aborted || completed.Length != paths.Count, error);
		}
		finally
		{
			foreach (var item in items) { Marshal.ReleaseComObject(item); }
			if (destination != null) { Marshal.ReleaseComObject(destination); }
			Marshal.ReleaseComObject(operation);
		}
	}

	static IShellItem Parse(string path)
	{
		var iid = typeof(IShellItem).GUID;
		Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var pointer));
		try { return (IShellItem)Marshal.GetObjectForIUnknown(pointer); }
		finally { Marshal.Release(pointer); }
	}

	[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
	sealed class Progress(string source, string? folder, bool directory) : IFileOperationProgressSink
	{
		public CompletedFile? Completed { get; private set; }
		public string? Error { get; private set; }

		int Record(IShellItem item, IShellItem? created, int result, bool deleting)
		{
			try
			{
				// 同卷移动可返回 COPYENGINE_S_DONT_PROCESS_CHILDREN，表示整项已由重命名完成。
				if (result is not (0 or 0x00270008))
				{
					if (result < 0 && result != unchecked((int)0x800704C7))
					{
						Error = Marshal.GetExceptionForHR(result)?.Message ?? $"Shell 文件操作失败：0x{result:X8}";
					}
					return 0;
				}
				item.GetDisplayName(0x80058000, out string original);
				if (!PathUtil.AreEqual(original, source)) { return 0; }
				string? target = null;
				if (!deleting)
				{
					if (created == null) { throw new InvalidOperationException("Shell 没有返回已完成文件的目标路径。"); }
					created.GetDisplayName(0x80058000, out target);
					if (!Path.IsPathFullyQualified(target) || !PathUtil.AreEqual(Path.GetDirectoryName(target), folder))
					{
						throw new InvalidOperationException("Shell 返回的文件不在粘贴目标目录。");
					}
				}
				Completed = new CompletedFile(source, target, directory);
				return 0;
			}
			catch (Exception ex)
			{
				// 原生回调中不能抛出托管异常；记录原因并返回失败码，由操作结果明确报告。
				Error = ex.Message;
				return ex.HResult;
			}
		}

		public int StartOperations() => 0;
		public int FinishOperations(int result) => 0;
		public int PreRenameItem(uint flags, IShellItem item, string? name) => 0;
		public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
		public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
		public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Record(item, created, result, false);
		public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
		public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Record(item, created, result, false);
		public int PreDeleteItem(uint flags, IShellItem item) => 0;
		public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => Record(item, created, result, true);
		public int PreNewItem(uint flags, IShellItem destination, string? name) => 0;
		public int PostNewItem(uint flags, IShellItem destination, string? name, string? template, uint attributes, int result, IShellItem? created) => 0;
		public int UpdateProgress(uint total, uint completed) => 0;
		public int ResetTimer() => 0;
		public int PauseTimer() => 0;
		public int ResumeTimer() => 0;
	}
}
