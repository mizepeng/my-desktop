<#
.SYNOPSIS
	生成并签名桌面右键菜单扩展包 MyDesktop.DesktopMenu.msix，由 MyDesktop.csproj 在生成后调用。
.DESCRIPTION
	签名证书取当前用户证书存储中主题为 CN=MyDesktop、带私钥且未过期的证书；找不到时跳过并提示，不影响编译。
	打包与签名调用 Windows 自带的 AppxPackaging 与 SignerSignEx2，不依赖 Windows SDK（makeappx / signtool）。
#>
param(
	[Parameter(Mandatory)] [string]$OutFile,
	[Parameter(Mandatory)] [string]$Version
)
$ErrorActionPreference = 'Stop'
# MSBuild 传入的输出路径可能是相对项目目录的
$OutFile = [IO.Path]::GetFullPath($OutFile)

$publisher = 'CN=MyDesktop'
$cert = Get-ChildItem Cert:\CurrentUser\My |
		Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
		Sort-Object NotAfter -Descending |
		Select-Object -First 1
if (-not $cert) {
	Write-Warning "未找到 $publisher 代码签名证书，跳过生成桌面右键菜单扩展包（创建方法见 README）"
	exit 0
}

$source = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;

[StructLayout(LayoutKind.Sequential)]
public struct APPX_PACKAGE_SETTINGS { public int forceZip32; public IntPtr hashMethod; }

[ComImport, Guid("BEB94909-E451-438B-B5A7-D79E767B75D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAppxFactory
{
	IAppxPackageWriter CreatePackageWriter(IStream outputStream, ref APPX_PACKAGE_SETTINGS settings);
}

[ComImport, Guid("9099E33B-246F-41E4-881A-008EB613F858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAppxPackageWriter
{
	void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string contentType, int compression, IStream input);
	void Close(IStream manifest);
}

[ComImport, Guid("5842A140-FF9F-4166-8F5C-62F5B7B0C781")]
public class AppxFactory { }

public static class DesktopMenuPacker
{
	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_FILE_INFO { public uint cbSize; public IntPtr pwszFileName; public IntPtr hFile; }

	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_SUBJECT_INFO { public uint cbSize; public IntPtr pdwIndex; public uint dwSubjectChoice; public IntPtr pSignerFileInfo; }

	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_CERT_STORE_INFO { public uint cbSize; public IntPtr pSigningCert; public uint dwCertPolicy; public IntPtr hCertStore; }

	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_CERT { public uint cbSize; public uint dwCertChoice; public IntPtr pCertStoreInfo; public IntPtr hwnd; }

	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_SIGNATURE_INFO
	{
		public uint cbSize;
		public uint algidHash;
		public uint dwAttrChoice;
		public IntPtr pAttrAuthcode;
		public IntPtr psAuthenticated;
		public IntPtr psUnauthenticated;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct SIGNER_SIGN_EX2_PARAMS
	{
		public uint dwFlags;
		public IntPtr pSubjectInfo;
		public IntPtr pSigningCert;
		public IntPtr pSignatureInfo;
		public IntPtr pProviderInfo;
		public uint dwTimestampFlags;
		public IntPtr pszAlgorithmOid;
		public IntPtr pwszTimestampURL;
		public IntPtr pCryptAttrs;
		public IntPtr pSipData;
		public IntPtr pSignerContext;
		public IntPtr pCryptoPolicy;
		public IntPtr pReserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct APPX_SIP_CLIENT_DATA { public IntPtr pSignerParams; public IntPtr pAppxSipState; }

	const uint STGM_READ = 0;
	const uint STGM_CREATE_WRITE = 0x1001;
	const uint SIGNER_SUBJECT_FILE = 1;
	const uint SIGNER_CERT_STORE = 2;
	const uint SIGNER_CERT_POLICY_CHAIN = 2;
	const uint CALG_SHA_256 = 0x800C;

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
	static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream template, out IStream stream);

	[DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
	static extern int CreateUri(string uri, uint flags, UIntPtr reserved, out IntPtr result);

	[DllImport("mssign32.dll", CharSet = CharSet.Unicode)]
	static extern int SignerSignEx2(uint flags, IntPtr subject, IntPtr cert, IntPtr signature, IntPtr provider, uint timestampFlags, IntPtr timestampOid,
			IntPtr timestampUrl, IntPtr cryptAttrs, IntPtr sipData, IntPtr signerContext, IntPtr cryptoPolicy, IntPtr reserved);

	static IStream OpenStream(string path, bool write)
	{
		IStream stream;
		Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, write ? STGM_CREATE_WRITE : STGM_READ, 0x80, write, null, out stream));
		return stream;
	}

	/// <summary>
	/// 把目录打成包：AppxManifest.xml 作为清单，其余文件作为内容（块映射等由系统接口生成）。
	/// </summary>
	public static void Pack(string directory, string outFile)
	{
		IntPtr hashMethod;
		Marshal.ThrowExceptionForHR(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, UIntPtr.Zero, out hashMethod));
		var output = OpenStream(outFile, true);
		try
		{
			var settings = new APPX_PACKAGE_SETTINGS { forceZip32 = 1, hashMethod = hashMethod };
			var writer = ((IAppxFactory)new AppxFactory()).CreatePackageWriter(output, ref settings);
			foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
			{
				var name = file.Substring(directory.Length).TrimStart('\\');
				if (string.Equals(name, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				var input = OpenStream(file, false);
				var contentType = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/octet-stream";
				writer.AddPayloadFile(name, contentType, 1, input);
				Marshal.ReleaseComObject(input);
			}
			var manifest = OpenStream(Path.Combine(directory, "AppxManifest.xml"), false);
			writer.Close(manifest);
			Marshal.ReleaseComObject(manifest);
			Marshal.ReleaseComObject(writer);
		}
		finally
		{
			Marshal.ReleaseComObject(output);
			Marshal.Release(hashMethod);
		}
	}

	/// <summary>
	/// 按微软「以编程方式为应用包签名」的做法调用 SignerSignEx2，APPX_SIP_CLIENT_DATA 是包签名必需的。
	/// </summary>
	public static int Sign(string file, X509Certificate2 cert)
	{
		var allocations = new List<IntPtr>();
		try
		{
			var fileName = Marshal.StringToHGlobalUni(file);
			allocations.Add(fileName);
			var index = Alloc(0, allocations);
			var fileInfo = Alloc(new SIGNER_FILE_INFO { cbSize = Size<SIGNER_FILE_INFO>(), pwszFileName = fileName }, allocations);
			var subject = Alloc(new SIGNER_SUBJECT_INFO
			{
				cbSize = Size<SIGNER_SUBJECT_INFO>(),
				pdwIndex = index,
				dwSubjectChoice = SIGNER_SUBJECT_FILE,
				pSignerFileInfo = fileInfo,
			}, allocations);
			var storeInfo = Alloc(new SIGNER_CERT_STORE_INFO
			{
				cbSize = Size<SIGNER_CERT_STORE_INFO>(),
				pSigningCert = cert.Handle,
				dwCertPolicy = SIGNER_CERT_POLICY_CHAIN,
			}, allocations);
			var signerCert = Alloc(new SIGNER_CERT { cbSize = Size<SIGNER_CERT>(), dwCertChoice = SIGNER_CERT_STORE, pCertStoreInfo = storeInfo }, allocations);
			var signature = Alloc(new SIGNER_SIGNATURE_INFO { cbSize = Size<SIGNER_SIGNATURE_INFO>(), algidHash = CALG_SHA_256 }, allocations);
			var parameters = Alloc(new SIGNER_SIGN_EX2_PARAMS { pSubjectInfo = subject, pSigningCert = signerCert, pSignatureInfo = signature }, allocations);
			var sipData = Alloc(new APPX_SIP_CLIENT_DATA { pSignerParams = parameters }, allocations);
			int hr = SignerSignEx2(0, subject, signerCert, signature, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sipData, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			var sipState = Marshal.ReadIntPtr(sipData, IntPtr.Size);
			if (sipState != IntPtr.Zero)
			{
				Marshal.Release(sipState);
			}
			return hr;
		}
		finally
		{
			foreach (var pointer in allocations)
			{
				Marshal.FreeHGlobal(pointer);
			}
		}
	}

	static uint Size<T>() where T : struct
	{
		return (uint)Marshal.SizeOf(typeof(T));
	}

	static IntPtr Alloc<T>(T value, List<IntPtr> allocations) where T : struct
	{
		var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)));
		allocations.Add(pointer);
		Marshal.StructureToPtr(value, pointer, false);
		return pointer;
	}
}
'@
Add-Type -TypeDefinition $source

# AppX 版本号必须是四段数字
$parts = @($Version.Split('-')[0].Split('.')) + @('0', '0', '0', '0')
$packageVersion = ($parts[0..3]) -join '.'

$name = 'MyDesktop.DesktopMenu.' + [guid]::NewGuid().ToString('N')
$staging = Join-Path ([IO.Path]::GetTempPath()) $name
# 签名接口按扩展名识别包格式，临时文件也必须是 .msix，且不能放在待打包的目录里
$temporary = Join-Path ([IO.Path]::GetTempPath()) "$name.msix"
New-Item -ItemType Directory -Path (Join-Path $staging 'Assets') | Out-Null
try {
	$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml')).Replace('__VERSION__', $packageVersion)
	[IO.File]::WriteAllText((Join-Path $staging 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))
	Copy-Item (Join-Path $PSScriptRoot '..\Assets\app.png') (Join-Path $staging 'Assets\logo.png')
	[DesktopMenuPacker]::Pack($staging, $temporary)
	$hr = [DesktopMenuPacker]::Sign($temporary, $cert)
	if ($hr -ne 0) {
		throw ('签名桌面右键菜单扩展包失败：0x{0:X8}' -f $hr)
	}
	Move-Item $temporary $OutFile -Force
	Write-Host "已生成桌面右键菜单扩展包：$OutFile（版本 $packageVersion，证书 $($cert.Thumbprint)）"
}
finally {
	Remove-Item $staging -Recurse -Force
	if (Test-Path $temporary) {
		Remove-Item $temporary -Force
	}
}
