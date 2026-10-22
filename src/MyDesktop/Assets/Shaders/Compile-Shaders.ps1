<#
.SYNOPSIS
	把本目录的 .fx 像素着色器编译成 WPF ShaderEffect 用的 ps_2_0 字节码（同名 .ps），编译产物随仓库提交，平时构建不需要运行。
.DESCRIPTION
	用 Windows 自带的 d3dcompiler_47.dll，不依赖 Windows SDK 的 fxc。修改 .fx 后在仓库根目录运行：
	powershell -ExecutionPolicy Bypass -File src\MyDesktop\Assets\Shaders\Compile-Shaders.ps1
	ps_2_0 是 WPF 软件渲染（关掉硬件加速时）也能运行的着色器版本。
#>
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ShaderCompiler
{
	[DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
	static extern int D3DCompile(byte[] source, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2,
			out IntPtr code, out IntPtr errors);

	[ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface ID3DBlob
	{
		[PreserveSig] IntPtr GetBufferPointer();
		[PreserveSig] UIntPtr GetBufferSize();
	}

	public static byte[] Compile(byte[] source, string name)
	{
		IntPtr code, errors;
		// D3DCOMPILE_OPTIMIZATION_LEVEL3
		int hr = D3DCompile(source, (UIntPtr)source.Length, name, IntPtr.Zero, IntPtr.Zero, "main", "ps_2_0", 1u << 15, 0, out code, out errors);
		if (hr < 0)
		{
			string message = errors == IntPtr.Zero ? "" : System.Text.Encoding.ASCII.GetString(Read(errors));
			throw new InvalidOperationException(string.Format("编译 {0} 失败（0x{1:X8}）：{2}", name, hr, message));
		}
		return Read(code);
	}

	static byte[] Read(IntPtr blobPointer)
	{
		var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPointer);
		var bytes = new byte[(int)blob.GetBufferSize()];
		Marshal.Copy(blob.GetBufferPointer(), bytes, 0, bytes.Length);
		Marshal.ReleaseComObject(blob);
		Marshal.Release(blobPointer);
		return bytes;
	}
}
'@
foreach ($source in Get-ChildItem -Path $PSScriptRoot -Filter *.fx)
{
	$output = [IO.Path]::ChangeExtension($source.FullName, '.ps')
	$bytes = [ShaderCompiler]::Compile([IO.File]::ReadAllBytes($source.FullName), $source.Name)
	[IO.File]::WriteAllBytes($output, $bytes)
	Write-Host "已生成 $output（$($bytes.Length) 字节）"
}
