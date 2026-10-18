using MyDesktop.Services;

namespace MyDesktop.WinUI.Verification;

/// <summary>
/// 隔离 UI 测试不读写用户剪贴板，格式的原生验证另在独立 Window Station 中运行。
/// </summary>
internal sealed class MemoryFileClipboard : IFileClipboard
{
	ClipboardFiles? _files;
	uint _sequence;
	public ClipboardFiles? Read(nint owner) => _files;
	public void Write(nint owner, IReadOnlyList<string> paths, bool cut) => _files = new ClipboardFiles(paths.ToArray(), cut, ++_sequence);
	public void Clear(nint owner, uint expectedSequence)
	{
		if (_files?.Sequence == expectedSequence)
		{
			_files = null;
			_sequence++;
		}
	}
}
