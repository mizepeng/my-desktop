<#
.SYNOPSIS
	打发行版安装包：发布程序、编译安装包、用 MyDesktop 证书给安装包签名，publish 目录只留下安装包。
.DESCRIPTION
	在仓库根目录运行：powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
	签名证书取当前用户证书存储中主题为 CN=MyDesktop、带私钥且未过期的代码签名证书（换电脑时的导入方法见 src\MyDesktop\ShellExtension\Pack-DesktopMenu.ps1）。
	程序的自动更新只接受这张证书签名的安装包，发行版附件必须用本脚本生成。
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src\MyDesktop\MyDesktop.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
& $iscc /Q (Join-Path $PSScriptRoot 'MyDesktop.iss')
if ($LASTEXITCODE -ne 0) { throw '编译安装包失败' }

$version = [Version](Get-Item (Join-Path $publish 'MyDesktop.exe')).VersionInfo.FileVersion
$setup = Join-Path $publish "MyDesktop-Setup-$($version.ToString(3)).exe"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
		Where-Object { $_.Subject -eq 'CN=MyDesktop' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
		Sort-Object NotAfter -Descending |
		Select-Object -First 1
if (-not $cert) { throw '没有找到 CN=MyDesktop 代码签名证书，安装包未签名，自动更新会拒绝它' }
# 自签名证书在本机不受信任，状态会是 UnknownError，签名本身已写入；以读出的签名者为准
Set-AuthenticodeSignature -FilePath $setup -Certificate $cert -HashAlgorithm SHA256 | Out-Null
$signer = (Get-AuthenticodeSignature $setup).SignerCertificate
if (-not $signer -or $signer.Thumbprint -ne $cert.Thumbprint) { throw '安装包签名失败' }

Get-ChildItem $publish -File | Where-Object FullName -ne $setup | Remove-Item
Write-Host "安装包：$setup"
Write-Host "签名证书：$($signer.Subject) $($signer.Thumbprint)"
Write-Host "SHA-256：$((Get-FileHash $setup -Algorithm SHA256).Hash)"
