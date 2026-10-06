# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概况
MyDesktop：Windows 11 桌面分区整理工具（对标 Fences），.NET 10 + WPF，单个项目 `src/MyDesktop`，没有第三方依赖。没有测试项目和 lint 配置，验证靠编译加实机运行。README 面向最终用户，开发说明写在脚本开头：`installer/MyDesktop.iss`、`src/MyDesktop/ShellExtension/Pack-DesktopMenu.ps1`。
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
## 常用命令
```bash
dotnet build src/MyDesktop/MyDesktop.csproj
powershell -ExecutionPolicy Bypass -File installer/Build-Installer.ps1
```
- 编译偶尔报找不到 `obj\...\*.g.cs` 或 `App.baml`：VS Code 的 C# 扩展在后台同时编译、抢了中间文件，重试即可。
- 发行版安装包一律用 `Build-Installer.ps1` 生成：发布程序、用 ISCC 编译安装包、用 `CN=MyDesktop` 证书给安装包签名，`publish` 里只留下 `MyDesktop-Setup-<版本>.exe`。自动更新只接受这张证书签名的安装包，漏签会让所有用户更新失败。版本号只改 csproj 的 `<Version>`，exe、扩展包、安装包文件名都跟着它。发版时在 `docs/CHANGELOG.md` 顶部补上本版改动。
- 桌面右键菜单扩展包（msix）在编译时生成并签名，需要签名证书（见「开发环境」），没有时跳过并给出提示。公钥 `installer/MyDesktop.cer` 随仓库提交，安装程序把它导入「受信任人」。
- 推送到 main 和提交 PR 时 GitHub Actions 会编译一遍（`.github/workflows/build.yml`，不签名）；Issue 模板在 `.github/ISSUE_TEMPLATE`。
- 独立测试实例：`MyDesktop.exe --data <目录>`，配置和日志都放在该目录。测试实例同样会接管桌面图标，会和正在运行的正式实例冲突，测试前先退出正式实例；不要在真实桌面上跑一键整理。
- 正式实例的配置与日志：`%APPDATA%\MyDesktop\settings.json`、`app.log`。
## 架构
### 接管模式
- 运行时隐藏资源管理器的桌面图标列表（`SHELLDLL_DefView` 下的 `SysListView32`），全部图标由本程序绘制：归入分区的画在分区窗口（`Views/FenceWindow`），其余「散放图标」画在每个显示器一个的透明图标层（`Views/DesktopLayerWindow`）。退出时恢复系统图标；主程序被强制结束时由守护进程 `MyDesktop.exe --guard <pid>` 恢复。
- 文件始终留在桌面文件夹：桌面分区只记成员（`FenceSettings.Members`，文件为完整路径，此电脑等系统图标为 `::{CLSID}`）；映射分区直接显示任意文件夹。一键整理、自动整理（`Services/DesktopOrganizer`）也只改成员，不移动文件。
- `Core/ExplorerDesktopView`：在独立的 STA 线程上跨进程读写资源管理器的桌面视图（IShellWindows → IShellBrowser → IFolderView2），产出快照（位置、间距、自动排列、图标大小）。散放图标的排列与位置以它为准，拖动后用 `SelectAndPositionItems` 写回；资源管理器无响应时不会拖住界面。
- `Services/DesktopTakeover` 统筹接管：图标层、隐藏与恢复、看门狗（资源管理器重启后重新接管）、守护进程、缩放变化处理，以及改名接手（系统「新建」后在隐藏列表里开始的改名，通过 WinEvent 发现后取消，改在图标层上进行）。`Services/DesktopItems` 管快照刷新和桌面文件夹监视。
- `Core/DesktopMouseWatcher`：低级鼠标、键盘钩子。负责双击桌面空白处隐藏、按住右键画框新建分区、空白处框选；桌面在前台时拦下 Delete、F2、Ctrl+A 等按键和首字母定位，转给图标层，防止作用到隐藏列表里看不见的文件（按着 Win 键时一律放行）。
- `Core/DesktopHost`：窗口层级。图标层紧贴桌面窗口之上，分区在图标层之上，靠在 `WM_WINDOWPOSCHANGING` 中改写层级维持。
- `Services/FenceManager`：分区窗口的生命周期、托盘菜单、命令分发、按缩放比例记忆分区布局（`LayoutDpi`、`BoundsByDpi`），按显示器组合记忆分区布局（`DisplayKey`、`LayoutByDisplay`，接上或拔掉显示器时 `SwitchDisplayLayout` 存旧组合、恢复新组合的布局）。命令定义在 `Core/AppCommand`：再次启动程序时用 `--command <名称>` 广播给正在运行的实例，`exit` 供安装程序在升级、卸载前让实例正常退出。
- `Views/ItemOps` 汇集分区和图标层共用的项目操作（打开、删除、剪贴板、改名、拖放）。
- `Services/WallpaperBlur`：分区的毛玻璃背景。在后台线程经 IDesktopWallpaper 读各显示器的壁纸和契合度，按 1/8 尺寸画好并模糊，拼成覆盖整个虚拟屏幕的一张图；分区在背景颜色下面铺一层这张图，按自己的屏幕位置截取。拖动、调整大小时毛玻璃先淡出、松手再淡入：边拖边更新的话，背景要等重绘才跟上，看上去慢半拍。动态壁纸（Progman 或资源管理器的 WorkerW 下出现别的程序的可见窗口）、读不出图片的壁纸（如视频）时不画，分区退回半透明。
- `Services/SettingsBackup`：整份配置备份到数据目录的 `backups`，一键整理、删除分区、恢复配置之前自动备份，保留最近 30 份。恢复配置时写入新配置后以 `--restart` 重启程序，新进程等旧进程释放单实例互斥体后再启动。
### 桌面右键菜单
签名的外部位置稀疏包 `MyDesktop.DesktopMenu`（`ShellExtension/AppxManifest.xml`）加进程外 COM：系统以包身份按需启动 `MyDesktop.exe --shell-extension`（`Core/DesktopMenuServer`，实现 IExplorerCommand）。程序启动时注册扩展包（`Core/DesktopMenuPackage`，注册记录在 `%LOCALAPPDATA%\MyDesktop\desktop-menu-package.txt`），注册失败时退回写当前用户注册表的静态菜单（`Core/DesktopMenu`）。
### 安装包与自动更新
- `installer/MyDesktop.iss`（Inno Setup 6，中文界面用 `installer/ChineseSimplified.isl`）：缺少 .NET 10 桌面运行时时下载固定版本并校验 SHA-256；安装、卸载前先让正在运行的实例正常退出；卸载时注销扩展包、删除证书和开机自启，并询问是否删除用户配置（默认保留，静默卸载时保留）。
- `Services/Updater`：匿名查询 GitHub 的 `releases/latest`，取名为 `MyDesktop-Setup-*.exe` 的附件；提示后下载，用 WinVerifyTrust 核对签名完好、签名者是嵌入程序的 `installer/MyDesktop.cer`，再以 `/VERYSILENT /autoupdate=1` 静默安装。安装程序装完按 `/autoupdate=1` 以原来的用户身份重新启动程序。程序发现版本比上次运行时（`LastRunVersion`）新，就在通知区域提示一次已更新，手动安装升级也一样。
## 实测得出的约束（改相关代码前先看）
- 分区窗口绝不能把 owner 或 parent 设成资源管理器的窗口：跨进程窗口关系会共享输入队列，右键菜单等场景下两个进程互相等待而死锁。
- 拖动吸附要按鼠标相对拖动起点的绝对位移计算；系统按「上次结果 + 鼠标增量」推算位置，直接改写会把窗口粘住。
- 「用户文件夹」、OneDrive 等系统图标的解析名是真实路径（如 `C:\Users\xxx`）。只有父目录是用户桌面或公共桌面的项目才能按文件删除、改名、移动，否则会作用到整个目录。
- 资源管理器隐藏着的图标列表不随缩放比例变化更新自己的 DPI，IFolderView 报告的间距会按新旧 DPI 之比失真，图标位置却已按新比例排好，读取时要校正（`ExplorerDesktopView.CorrectSpacing`）；分区窗口在缩放变化后也可能停在旧 DPI（`FenceManager.RefreshWindowsDpi`）。
- 关闭自动排列时，IFolderView 报告的位置是图标图像左边缘、比图标顶端高约 2 DIP 处；自动排列时报告的是格子左上角，两种模式要分别换算（`DesktopTakeover.ToCell`、`ToPosition`）。
- 系统图像列表对象在本进程里不应答 IImageList 的 QueryInterface，角标按虚表直接调用（`Native/ShellIconLoader`）。
- 系统提供的模糊（亚克力、云母等）对分区这类几乎总是未激活的窗口不起作用，所以毛玻璃是自己画的（`Services/WallpaperBlur`），前提是分区下面只有壁纸。
## 代码约定
- 遵循 `.editorconfig`：tab 缩进；含中文的 `.ps1` 必须是 UTF-8 带 BOM（Windows PowerShell 5.1 按系统代码页读取无 BOM 的脚本）。
- 文本文件一律 LF 换行，由 `.gitattributes`（`* text=auto eol=lf`）统一，不依赖各电脑的 `core.autocrlf`：扩展包清单原样打进 msix，换行符不一致会让不同电脑打出内容不同的同版本扩展包。
- csproj 把 CS8509（switch 表达式没有覆盖全部枚举值）设为错误：对枚举优先用 switch 表达式列全所有值，不写 default。
- 注释、日志和界面文字都用中文。
