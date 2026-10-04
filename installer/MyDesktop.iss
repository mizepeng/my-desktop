; MyDesktop 安装程序（Inno Setup 6）：安装程序本体与桌面右键菜单扩展包，信任扩展包的签名证书，
; 电脑上没有 .NET 10 桌面运行时时自动下载安装。在仓库根目录先发布程序，再编译本脚本，安装包生成在 publish 目录：
;   dotnet publish src/MyDesktop/MyDesktop.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\MyDesktop.iss
; （ISCC.exe 在 Inno Setup 的安装目录里，winget 默认按用户装在上面这个位置）
; publish 目录里还要有签名证书的公钥 MyDesktop.cer（导出方法见 src\MyDesktop\ShellExtension\Pack-DesktopMenu.ps1）。

#define PublishDir AddBackslash(SourcePath) + "..\publish\"
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(PublishDir + "MyDesktop.exe", VerMajor, VerMinor, VerRev, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)
; 卸载时按指纹删除导入的证书；.cer 是 DER 格式，文件本身的 SHA-1 就是证书指纹
#define CertThumbprint GetSHA1OfFile(PublishDir + "MyDesktop.cer")
; 运行时固定版本并校验 SHA-256，只在电脑上没有任何 10.x 版本时下载
#define RuntimeFile "windowsdesktop-runtime-10.0.12-win-x64.exe"
#define RuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/" + RuntimeFile
#define RuntimeSha256 "55a67d8476cde95a9cc43a95803b4f54446e7c9251ed22aa6421d4908174ae84"

[Setup]
AppId={{26C2B19F-E49A-4CA1-B523-E8932B5A74F6}
AppName=MyDesktop 桌面分区
AppVersion={#AppVersion}
AppPublisher=MyDesktop
DefaultDirName={autopf}\MyDesktop
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir=..\publish
OutputBaseFilename=MyDesktop-Setup-{#AppVersion}
SetupIconFile=..\src\MyDesktop\Assets\app.ico
UninstallDisplayIcon={app}\MyDesktop.exe
UninstallDisplayName=MyDesktop 桌面分区
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; 升级时如果还有本程序的进程占用文件（如系统按需启动的右键菜单服务进程），直接结束它们
CloseApplications=force
; 开机自启、右键菜单扩展包都按用户注册，归运行安装程序的这个用户
UsedUserAreasWarning=no

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机自动启动"

[Files]
Source: "{#PublishDir}MyDesktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}MyDesktop.DesktopMenu.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}MyDesktop.cer"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\MyDesktop 桌面分区"; Filename: "{app}\MyDesktop.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyDesktop"; ValueData: """{app}\MyDesktop.exe"" --autostart"; Tasks: autostart
; 卸载时删除开机自启（程序里也能开关，不论安装时是否勾选都要删），以及没有扩展包时程序退而写入的注册表右键菜单
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MyDesktop"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\MyDesktop"; Flags: uninsdeletekey dontcreatekey

[Run]
; 信任右键菜单扩展包的签名证书，程序启动时会自动注册扩展包
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f TrustedPeople ""{tmp}\MyDesktop.cer"""; Flags: runhidden; StatusMsg: "正在配置桌面右键菜单…"
Filename: "{app}\MyDesktop.exe"; Description: "启动 MyDesktop"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 先让程序正常退出（恢复桌面图标），再注销右键菜单扩展包、删除证书
Filename: "{app}\MyDesktop.exe"; Parameters: "--command exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitApp"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-AppxPackage -Name MyDesktop.DesktopMenu | Remove-AppxPackage"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMenuPackage"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople {#CertThumbprint}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveCertificate"

[UninstallDelete]
; 扩展包的注册记录，不删的话重装同一版本时程序会以为已经注册过
Type: files; Name: "{localappdata}\MyDesktop\desktop-menu-package.txt"
Type: dirifempty; Name: "{localappdata}\MyDesktop"

[Code]
var
	DownloadPage: TDownloadWizardPage;

// 是否已装 .NET 10 桌面运行时，任意 10.x 版本都能运行本程序
function IsRuntimeInstalled: Boolean;
var
	FindRec: TFindRec;
begin
	Result := False;
	if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
	begin
		try
			repeat
				Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
			until Result or not FindNext(FindRec);
		finally
			FindClose(FindRec);
		end;
	end;
end;

procedure InitializeWizard;
begin
	DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
	DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

// 下载并静默安装运行时；失败时提示并停在「准备安装」页，可以再点「安装」重试
function InstallRuntime: Boolean;
var
	ResultCode: Integer;
begin
	Result := False;
	DownloadPage.Clear;
	DownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '{#RuntimeSha256}');
	DownloadPage.Show;
	try
		try
			DownloadPage.Download;
			Result := True;
		except
			if not DownloadPage.AbortedByUser then
				SuppressibleMsgBox('下载 .NET 桌面运行时失败：' + GetExceptionMessage + #13#10#13#10 + '请检查网络连接后重试。', mbCriticalError, MB_OK, IDOK);
		end;
		if Result then
		begin
			DownloadPage.SetText('正在安装 .NET 桌面运行时，请稍候…', '');
			DownloadPage.ProgressBar.Style := npbstMarquee;
			DownloadPage.ProgressBar.Show;
			// 3010 表示安装成功、需要重启才能完全生效，本程序不受影响
			Result := Exec(ExpandConstant('{tmp}\{#RuntimeFile}'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
					and ((ResultCode = 0) or (ResultCode = 3010));
			if not Result then
				SuppressibleMsgBox('安装 .NET 桌面运行时失败，错误代码：' + IntToStr(ResultCode), mbCriticalError, MB_OK, IDOK);
		end;
	finally
		DownloadPage.ProgressBar.Style := npbstNormal;
		DownloadPage.Hide;
	end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
	Result := True;
	if (CurPageID = wpReady) and not IsRuntimeInstalled then
		Result := InstallRuntime;
end;

// 覆盖安装或升级前，用新版程序让正在运行的 MyDesktop 正常退出并恢复桌面图标，不论它是从哪里启动的
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
	ResultCode: Integer;
begin
	ExtractTemporaryFile('MyDesktop.exe');
	Exec(ExpandConstant('{tmp}\MyDesktop.exe'), '--command exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
	Result := '';
end;
