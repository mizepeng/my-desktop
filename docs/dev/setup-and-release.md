# 开发环境与发版

本文从 AGENTS.md 拆出，换电脑、打安装包、发版时阅读。

## 开发环境
换电脑后按下面准备（Windows 11 64 位）。其中导入 PFX、信任证书要输入 PFX 密码或管理员确认，由开发者本人在自己的终端里执行，PFX 密码不要交给 agent：
- Git、.NET 10 SDK：`winget install Git.Git`、`winget install Microsoft.DotNet.SDK.10`。
- Inno Setup 6（打安装包用）：`winget install JRSoftware.InnoSetup`，默认按用户装到 `%LOCALAPPDATA%\Programs\Inno Setup 6`；`Build-Installer.ps1` 在这里或 `Program Files (x86)` 下找 `ISCC.exe`。
- 签名证书 `CN=MyDesktop`（指纹 `6C9E104E1EADB0E1E2C87A7DB52110ACC0ECA3D3`）分两部分：
    - 公钥 `installer/MyDesktop.cer`：随仓库提交。编译时嵌进程序，自动更新用它核对下载的安装包；安装包把它导入用户电脑的「受信任人」，桌面右键菜单扩展包才能注册。
    - 私钥：只在开发电脑的当前用户证书存储里，用来签名；文件随 Windows 用户加密，不能直接拷走。备份是带密码的 PFX 文件，密码是导出时自己设的，导入时要用；PFX 和密码分开妥善保存，绝不能放进仓库。
- 换电脑时把 PFX 导入当前用户证书存储（不需要管理员权限），导出、导入命令见 `Pack-DesktopMenu.ps1` 开头。导入后不用手动签名：编译时 `Pack-DesktopMenu.ps1` 自动给右键菜单扩展包签名，`Build-Installer.ps1` 自动给安装包签名，都按 `CN=MyDesktop` 在当前用户证书存储里找证书。`Build-Installer.ps1` 最后输出的签名证书指纹应与上面一致。
- 不要另建新证书：自动更新只认 `installer/MyDesktop.cer` 这一张，换了证书，所有已安装的程序都会拒绝新安装包，用户只能手动安装一次。没有证书也能编译调试，只是不生成右键菜单扩展包、打不了发行版安装包。
- 不经安装包、直接运行编译结果又要用桌面右键菜单时：以管理员身份把 `installer/MyDesktop.cer` 导入本机「受信任人」（命令同在脚本开头），或者用安装包装一次。
- 不需要 Windows SDK：扩展包的打包和签名调用的是系统自带接口。

## 安装包与发版
- 测试包用 `Build-Installer.ps1 -Test`：输出 `publish\测试\MyDesktop-Setup-<版本>-测试.exe`，不动 `publish` 根目录里的正式包，`publish\测试` 里只留这一个；个别情况要同时留几个测试包测不同场景时加 `-Label <场景>`。
- 发行版安装包一律用 `Build-Installer.ps1` 生成：发布程序、用 ISCC 编译安装包、用 `CN=MyDesktop` 证书给安装包签名，`publish` 里只留下 `MyDesktop-Setup-<版本>.exe`。自动更新只接受这张证书签名的安装包，漏签会让所有用户更新失败。版本号只改 csproj 的 `<Version>`，exe、扩展包、安装包文件名都跟着它。发版时在 `docs/CHANGELOG.md` 顶部补上本版改动。
- 桌面右键菜单扩展包（msix）在编译时生成并签名，需要签名证书（见「开发环境」），没有时跳过并给出提示。公钥 `installer/MyDesktop.cer` 随仓库提交，安装程序把它导入「受信任人」。
- 推送到 main 和提交 PR 时 GitHub Actions 会编译一遍（`.github/workflows/build.yml`，不签名）；Issue 模板在 `.github/ISSUE_TEMPLATE`。
- WinUI 重构测试包用 `Build-Installer.ps1 -Test -WinUI -TestVersion 1.3.0 -Label WinUI3`；版本覆盖仅用于本次产物，完整依赖安装在 `WinUI-Test` 子目录，沿用同一安装标识与用户配置。
  新入口支持安装程序共用的 `--command exit`，正常退出时先恢复系统图标。WinUI 尚未接入自动更新，测试后用最新正式安装包覆盖安装恢复；测试包不注册新的开机启动或右键菜单扩展。
  非 MSIX 发布显式加入应用 PRI 与 XBF 文件；编译安装包前必须直接运行发布目录的隔离 UI 验证，失败就停止打包，不能用编译目录的验证结果代替。
