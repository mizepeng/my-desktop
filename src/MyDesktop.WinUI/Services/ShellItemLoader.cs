using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using MyDesktop.Native;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.WinUI.Services;

internal sealed record ShellItemImage(string Name, int Width, int Height, byte[] Pixels)
{
	public WriteableBitmap ToBitmap()
	{
		var bitmap = new WriteableBitmap(Width, Height);
		using (var stream = bitmap.PixelBuffer.AsStream())
		{
			stream.Write(Pixels);
		}
		bitmap.Invalidate();
		return bitmap;
	}
}

/// <summary>
/// 在独立 STA 线程读取 Shell 显示名与图像，只把像素交回界面，不创建 WPF 对象。
/// </summary>
internal sealed class ShellItemLoader : IDisposable
{
	readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<ShellItemImage> Completion)> _queue = new();

	public ShellItemLoader()
	{
		var thread = new Thread(() =>
		{
			foreach (var request in _queue.GetConsumingEnumerable())
			{
				try
				{
					request.Completion.SetResult(Read(request.Path, request.Size));
				}
				catch (Exception ex)
				{
					request.Completion.SetException(ex);
				}
			}
		}) { IsBackground = true, Name = "WinUI-ShellImages" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	public Task<ShellItemImage> LoadAsync(string path, int size)
	{
		var completion = new TaskCompletionSource<ShellItemImage>(TaskCreationOptions.RunContinuationsAsynchronously);
		_queue.Add((path, size, completion));
		return completion.Task;
	}

	static ShellItemImage Read(string path, int size)
	{
		var iid = typeof(IShellItemImageFactory).GUID;
		Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var pointer));
		var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(pointer);
		Marshal.Release(pointer);
		try
		{
			((IShellItem)factory).GetDisplayName(0, out string name);
			// 与现有实现一致，文件夹只取图标，避免内容缩略图覆盖用户的图标主题。
			uint flags = Directory.Exists(path) ? 4u : 0u;
			Marshal.ThrowExceptionForHR(factory.GetImage(new SIZE(size, size), flags, out var bitmap));
			try
			{
				if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), out var section) == 0)
				{
					throw new InvalidOperationException($"无法读取 Shell 图像：{path}");
				}
				int width = section.dsBm.bmWidth, height = Math.Abs(section.dsBm.bmHeight);
				var pixels = new byte[checked(width * height * 4)];
				var info = new BITMAPINFO
				{
					bmiHeader = new BITMAPINFOHEADER
					{
						biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
					},
				};
				var dc = CreateCompatibleDC(0);
				try
				{
					if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
					{
						throw new InvalidOperationException($"无法转换 Shell 图像像素：{path}");
					}
				}
				finally
				{
					DeleteDC(dc);
				}
				bool hasAlpha = false, straightAlpha = false;
				for (int index = 0; index < pixels.Length; index += 4)
				{
					byte alpha = pixels[index + 3];
					hasAlpha |= alpha != 0;
					straightAlpha |= pixels[index] > alpha || pixels[index + 1] > alpha || pixels[index + 2] > alpha;
				}
				for (int index = 0; index < pixels.Length; index += 4)
				{
					if (!hasAlpha)
					{
						pixels[index + 3] = 255;
					}
					else if (straightAlpha)
					{
						for (int channel = 0; channel < 3; channel++)
						{
							pixels[index + channel] = (byte)((pixels[index + channel] * pixels[index + 3] + 127) / 255);
						}
					}
				}
				return new ShellItemImage(name, width, height, pixels);
			}
			finally
			{
				DeleteObject(bitmap);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(factory);
		}
	}

	public void Dispose() => _queue.CompleteAdding();
}
