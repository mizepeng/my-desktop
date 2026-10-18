<#
.SYNOPSIS
	打发行版安装包：发布程序、编译安装包、用 MyDesktop 证书给安装包签名，publish 目录只留下安装包。
.DESCRIPTION
	在仓库根目录运行：powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
	打测试包加 -Test：输出 publish\测试\MyDesktop-Setup-<版本>-测试.exe，不动 publish 根目录里的正式包，publish\测试 里只留这一个；
	个别情况要同时留几个测试包测不同场景时再加 -Label <场景>，输出 MyDesktop-Setup-<版本>-测试-<场景>.exe，不删别的测试包。
	WinUI 重构测试包使用 -Test -WinUI，可加 -TestVersion 1.3.0 覆盖本次包的版本，不修改工程中的正式版本号。
	签名证书取当前用户证书存储中主题为 CN=MyDesktop、带私钥且未过期的代码签名证书（换电脑时的导入方法见 src\MyDesktop\ShellExtension\Pack-DesktopMenu.ps1）。
	程序的自动更新只接受这张证书签名的安装包，发行版附件必须用本脚本生成。
#>
param(
	[switch]$Test,
	[string]$Label,
	[switch]$WinUI,
	[string]$TestVersion
)
$ErrorActionPreference = 'Stop'
if ($WinUI -and -not $Test) { throw 'WinUI 入口尚未完成迁移，只能生成测试包，请同时指定 -Test' }
if ($TestVersion -and (-not $Test -or $TestVersion -notmatch '^\d+\.\d+\.\d+$')) { throw '-TestVersion 只用于测试包，版本号必须为三段数字' }
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
# 测试包放 publish\测试，程序先发布到它下面的临时目录，打完删掉；正式包照旧发布到 publish 本身
$output = if ($Test) { Join-Path $publish '测试' } else { $publish }
$staging = if ($Test) { Join-Path $output '发布' } else { $publish }

$versionArguments = @()
if ($TestVersion) { $versionArguments += "-p:Version=$TestVersion" }
if ($Test) {
	# 只清理本脚本位于测试输出目录内的暂存目录，防止旧入口或旧依赖混入新包。
	$staging = [IO.Path]::GetFullPath($staging)
	if (-not $staging.StartsWith([IO.Path]::GetFullPath($output) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
		throw '测试暂存目录超出测试输出目录'
	}
	if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
if ($WinUI) {
	$project = Join-Path $root 'src\MyDesktop.WinUI\MyDesktop.WinUI.csproj'
	dotnet publish $project -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=false -o $staging -nologo -v q @versionArguments
	if ($LASTEXITCODE -ne 0) { throw '发布 WinUI 测试程序失败' }
	# 沿用安装程序已有的单文件退出助手，解压到临时目录后不需要 WinUI 的配套文件。
	$exitHelper = Join-Path $staging 'InstallerExit'
	$legacyProject = Join-Path $root 'src\MyDesktop\MyDesktop.csproj'
	dotnet publish $legacyProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $exitHelper -nologo -v q
	$executable = 'MyDesktop.WinUI.exe'
} else {
	$legacyProject = Join-Path $root 'src\MyDesktop\MyDesktop.csproj'
	dotnet publish $legacyProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $staging -nologo -v q @versionArguments
	$executable = 'MyDesktop.exe'
}
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$version = [Version](Get-Item (Join-Path $staging $executable)).VersionInfo.FileVersion
$name = "MyDesktop-Setup-$($version.ToString(3))"
if ($Test) { $name += '-测试' }
if ($Test -and $Label) { $name += "-$Label" }
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
$installerArguments = @('/Q', "/DPublishFrom=$staging", "/O$output", "/F$name")
if ($WinUI) { $installerArguments += '/DWinUITest' }
& $iscc @installerArguments (Join-Path $PSScriptRoot 'MyDesktop.iss')
if ($LASTEXITCODE -ne 0) { throw '编译安装包失败' }

$setup = Join-Path $output "$name.exe"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
		Where-Object { $_.Subject -eq 'CN=MyDesktop' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
		Sort-Object NotAfter -Descending |
		Select-Object -First 1
if (-not $cert) { throw '没有找到 CN=MyDesktop 代码签名证书，安装包未签名，自动更新会拒绝它' }
# 自签名证书在本机不受信任，状态会是 UnknownError，签名本身已写入；以读出的签名者为准
Set-AuthenticodeSignature -FilePath $setup -Certificate $cert -HashAlgorithm SHA256 | Out-Null
$signer = (Get-AuthenticodeSignature $setup).SignerCertificate
if (-not $signer -or $signer.Thumbprint -ne $cert.Thumbprint) { throw '安装包签名失败' }

if ($Test) { Remove-Item -LiteralPath $staging -Recurse -Force }
# 正式包、测试包各只留最新的一个；加了 -Label 的测试包是特意留几个测不同场景，不删别的
if (-not ($Test -and $Label)) {
	Get-ChildItem $output -File | Where-Object FullName -ne $setup | Remove-Item
}
Write-Host "安装包：$setup"
Write-Host "签名证书：$($signer.Subject) $($signer.Thumbprint)"
Write-Host "SHA-256：$((Get-FileHash $setup -Algorithm SHA256).Hash)"
