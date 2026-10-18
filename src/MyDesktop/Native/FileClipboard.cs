using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MyDesktop.Services;

namespace MyDesktop.Native;

/// <summary>
/// 使用 Explorer 的文件列表与 Preferred DropEffect 格式，不依赖界面框架。
/// </summary>
internal sealed class FileClipboard : IFileClipboard
{
	const uint FileDrop = 15;
	static uint EffectFormat => RegisterClipboardFormat("Preferred DropEffect") is > 0 and var format ? format : throw new Win32Exception();

	public ClipboardFiles? Read(nint owner)
	{
		Open(owner);
		try
		{
			if (!IsClipboardFormatAvailable(FileDrop))
			{
				return null;
			}
			var drop = GetClipboardData(FileDrop);
			if (drop == 0)
			{
				throw new Win32Exception();
			}
			uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
			var paths = new string[checked((int)count)];
			for (uint index = 0; index < count; index++)
			{
				uint length = DragQueryFile(drop, index, null, 0);
				var text = new StringBuilder(checked((int)length + 1));
				if (length == 0 || DragQueryFile(drop, index, text, (uint)text.Capacity) != length)
				{
					throw new InvalidDataException("剪贴板中的文件路径无效。");
				}
				paths[index] = text.ToString();
			}
			bool cut = false;
			if (IsClipboardFormatAvailable(EffectFormat))
			{
				var effect = GetClipboardData(EffectFormat);
				if (effect == 0 || GlobalSize(effect).ToUInt64() < 4)
				{
					throw new InvalidDataException("剪贴板中的文件传输类型无效。");
				}
				var pointer = Lock(effect);
				try
				{
					cut = (Marshal.ReadInt32(pointer) & 2) != 0;
				}
				finally
				{
					GlobalUnlock(effect);
				}
			}
			// 外部程序未指定 Preferred DropEffect 时，文件列表按复制处理。
			return paths.Length == 0 ? null : new ClipboardFiles(paths, cut, GetClipboardSequenceNumber());
		}
		finally
		{
			CloseClipboard();
		}
	}

	public void Write(nint owner, IReadOnlyList<string> paths, bool cut)
	{
		if (owner == 0 || paths.Count == 0 || paths.Any(path => !Path.IsPathFullyQualified(path) || path.Contains('\0')))
		{
			throw new ArgumentException("文件剪贴板缺少窗口或有效的完整路径。");
		}
		var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
		var bytes = new byte[20 + names.Length];
		BitConverter.GetBytes(20).CopyTo(bytes, 0);
		BitConverter.GetBytes(1).CopyTo(bytes, 16);
		names.CopyTo(bytes, 20);
		nint drop = 0, effect = 0;
		try
		{
			drop = Allocate(bytes);
			effect = Allocate(BitConverter.GetBytes(cut ? 2 : 5));
			Open(owner);
			try
			{
				if (!EmptyClipboard() || SetClipboardData(FileDrop, drop) == 0)
				{
					throw new Win32Exception();
				}
				drop = 0;
				if (SetClipboardData(EffectFormat, effect) == 0)
				{
					throw new Win32Exception();
				}
				effect = 0;
			}
			finally
			{
				CloseClipboard();
			}
		}
		finally
		{
			if (drop != 0) { GlobalFree(drop); }
			if (effect != 0) { GlobalFree(effect); }
		}
	}

	public void Clear(nint owner, uint expectedSequence)
	{
		Open(owner);
		try
		{
			// 文件操作期间用户可能已复制其他内容，不能清掉那一份新的剪贴板。
			if (GetClipboardSequenceNumber() == expectedSequence && !EmptyClipboard())
			{
				throw new Win32Exception();
			}
		}
		finally
		{
			CloseClipboard();
		}
	}

	static void Open(nint owner)
	{
		if (!OpenClipboard(owner)) { throw new Win32Exception(); }
	}

	static nint Lock(nint handle) => GlobalLock(handle) is var pointer && pointer != 0 ? pointer : throw new Win32Exception();

	static nint Allocate(byte[] bytes)
	{
		var handle = GlobalAlloc(0x42, (nuint)bytes.Length);
		if (handle == 0) { throw new Win32Exception(); }
		try
		{
			var pointer = Lock(handle);
			try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
			finally { GlobalUnlock(handle); }
			return handle;
		}
		catch
		{
			GlobalFree(handle);
			throw;
		}
	}

	[DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
	[DllImport("user32.dll")] static extern bool CloseClipboard();
	[DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
	[DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
	[DllImport("user32.dll", SetLastError = true)] static extern nint GetClipboardData(uint format);
	[DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint memory);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint RegisterClipboardFormat(string format);
	[DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
	[DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalAlloc(uint flags, nuint bytes);
	[DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalLock(nint handle);
	[DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint handle);
	[DllImport("kernel32.dll")] static extern nint GlobalFree(nint handle);
	[DllImport("kernel32.dll")] static extern nuint GlobalSize(nint handle);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint capacity);
}
