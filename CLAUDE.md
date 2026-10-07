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
- `Services/SettingsStore`：保存时先写临时文件并刷盘，再用 `File.Replace` 替换、留下上一版 `settings.json.bak`；配置文件读不出来时从 `.bak` 恢复并写回，两份都坏才用默认设置。
- `Services/SettingsBackup`：整份配置备份到数据目录的 `backups`，一键整理、删除分区、恢复配置之前自动备份，保留最近 30 份。恢复配置时写入新配置后以 `--restart` 重启程序，新进程等旧进程释放单实例互斥体后再启动。
- 整理动画（`DesktopOrganizer.ExecuteAnimatedAsync`，一键整理和自动整理都播）：按规则顺序逐个分区归入，图标由置顶、鼠标穿透的小窗口（`Views/FlyingIcon`）从桌面原处飞进分区的格子。轮到之前图标仍留在桌面（`DesktopTakeover` 的待起飞集合不算已归入），飞行期间分区里先不显示（`FenceItem.IsInFlight`）。启动时（包括恢复配置后重启）也一样：分区里的图标先不显示，接管桌面、藏起资源管理器图标列表的那一刻，从列表里的原处飞进分区（`DesktopOrganizer.FlyInAtStartupAsync`，起点按快照推算 `DesktopTakeover.ExplorerIconRects`）。为了开机时看得到，先等登录界面（LogonUI 进程）退出、桌面淡入才接管桌面（`FenceManager.StartTakeoverWhenReadyAsync`）；接管后先等分区图标的图像加载好（最多 3 秒，`FenceManager.WaitForFenceIconsAsync`）再藏起列表、起飞：分区读到桌面视图才有图标，图像没出来的图标飞不起来；30 秒内接管不了就直接显示。一键整理归入同一组的几个标签时，等前一个标签的图标都落定再切到下一个。删除桌面分区时反过来（`FenceManager.FlyHomeAsync`）：关分区前在每个图标的位置放上飞行的图标，图标层立即重排，回到桌面的图标落地前不显示。
- 分区之间不重叠：松手时压住了别的分区（卷起的只算收起的那一条），滑到所在显示器上最近的空位，放不下就滑回拖动前的位置（`FenceManager.FindFreeSpot`、`FenceWindow.FinishMoveSize`）；调整大小时被拖动的边止于相邻分区（`FenceWindow.StopAtNeighbors`）；原本就重叠的旧布局等下次拖动时再处理。点击、拖动、悬停展开或固定展开的分区提到其他分区之上（`DesktopHost.BringAboveFences`），置顶、撤销置顶和摆回桌面上方时保持分区之间的上下次序。
- 标签页（分区合并）：`AppSettings.Groups` 记录合并成标签页的分区组（成员顺序 + 当前标签）。每个标签仍是独立的分区窗口，同组只显示当前标签那一个，其余照常加载内容但不显示（`FenceManager.IsHiddenTab`）；位置、大小、卷起和锁定在组内同步（`FenceManager.SyncGroup`、`CopyFrame`），所以缩放、显示器布局等按单个分区写的逻辑不用改，只是同组的分区互相不算重叠。切换标签时新标签先显示在当前标签正下方、画好后再隐藏当前标签（`FenceWindow.ShowInPlaceOf`），不闪。拖动分区时鼠标停在别的分区标题栏上松手就合并（`MergeTargetAt`、`MergeAsync`）；按住 Shift 拖标签调整顺序或拖出拆分（`DetachTab`）。
- 搜索桌面图标：全局快捷键注册在托盘的隐藏窗口上（`TrayIcon.SetHotkey`，默认 Alt+Space，`AppSettings.SearchHotkey` 为空表示不用），弹出 `Views/SearchWindow`，候选是各桌面分区和散放图标层里的图标，不含映射分区；没输入时分「常用」和「全部」两类列出：常用按 `Services/UsageStats` 记录的打开次数（数据目录的 `usage.json`，随时间衰减）排序、没有记录时不列，全部只列前面一部分。`Core/PinyinMatcher` 支持全拼、首字母混输，拼音表 `Assets/pinyin.txt` 由 Unicode 的 Unihan 数据生成（文件开头写明取哪些字段）。
- `Core/AutoStart`：开机自启是当前用户「登录时」的计划任务（任务计划程序 COM 接口，普通优先级、不限时长）。注册表 Run 项会被资源管理器排队延后，实测比资源管理器晚约 30 秒才启动；安装包勾选开机自启时仍写 Run 项，程序启动时迁移成计划任务，卸载时按名称前缀删除各用户的任务。
### 桌面右键菜单
签名的外部位置稀疏包 `MyDesktop.DesktopMenu`（`ShellExtension/AppxManifest.xml`）加进程外 COM：系统以包身份按需启动 `MyDesktop.exe --shell-extension`（`Core/DesktopMenuServer`，实现 IExplorerCommand）。程序启动时注册扩展包（`Core/DesktopMenuPackage`，注册记录在 `%LOCALAPPDATA%\MyDesktop\desktop-menu-package.txt`），注册失败时退回写当前用户注册表的静态菜单（`Core/DesktopMenu`）。设置里关掉菜单时后台注销扩展包要好几秒，菜单服务弹出菜单前都向主程序查询状态（`AppCommands.EncodeState`），关掉了就立即隐藏。
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
- Shell 返回的位图多数图标自下而上，图片缩略图（实测）等自上而下（用户反馈倒置的 Adobe 2026 程序图标应属此类），而 GetObject 读到的 DIBSECTION 高度一律为正、分不出来；取像素要用 GetDIBits 让系统按实际行序转换（`ShellIconLoader.ToBitmapSource`），直接读内存会把后一种上下颠倒。
- WPF 的透明窗口（`AllowsTransparency`，分区和图标层都是）每次重画都要等显卡把画面拷回内存，显卡被游戏占满时就会一卡一卡。跟着鼠标连续变化的东西（框选框、画框新建分区、拆标签预览）不要画在分区或整屏的图标层里，用 `Views/DrawFrameWindow`：CPU 画好像素交给 `UpdateLayeredWindow`，只有选框那么大。它是借系统 Static 窗口类建的原生窗口：WPF 的 `HwndSource` 不用逐像素透明时会强行去掉 `WS_EX_LAYERED`（事后用 `SetWindowLongPtr` 补上也会被改回去），`UpdateLayeredWindow` 就失败、什么都不显示。
- 硬件加速（`AppSettings.HardwareAcceleration`，`FenceManager.ApplyRenderMode`）开着时，透明窗口要按整窗大小多存几份缓存，内存实测约是 CPU 绘制的两倍（150% 缩放的 4K 屏上，一个全屏图标层加 7 个分区：182 MB 对 95 MB）。运行中切到 CPU 绘制立即生效，但已经加载的显卡驱动不会卸掉，内存要重启程序才完全降下来（同一场景实测只降到 130 MB）。
- 前台切换、层级变化的 WinEvent 钩子要在打开分区窗口之前装好：启动那一阵子桌面被提到最前（比如刚装完点「完成」，安装程序关闭）的话，分区会被压在桌面下面没人纠正，要等看门狗或点一下桌面才露出来。
- 低级鼠标钩子：每个鼠标事件都要等钩子返回才交给前台程序（包括游戏），回调里没在拖动时的移动、滚轮等直接放行、不读事件内容，钩子线程用最高优先级（`Core/DesktopMouseWatcher`）。
- 用 `dynamic` 调任务计划程序的 COM 接口时，任务不存在抛的是 `FileNotFoundException`（HResult 0x80070002），不是 `COMException`，要按 HResult 判断。
## 代码约定
- 遵循 `.editorconfig`：tab 缩进；含中文的 `.ps1` 必须是 UTF-8 带 BOM（Windows PowerShell 5.1 按系统代码页读取无 BOM 的脚本）。
- 文本文件一律 LF 换行，由 `.gitattributes`（`* text=auto eol=lf`）统一，不依赖各电脑的 `core.autocrlf`：扩展包清单原样打进 msix，换行符不一致会让不同电脑打出内容不同的同版本扩展包。
- csproj 把 CS8509（switch 表达式没有覆盖全部枚举值）设为错误：对枚举优先用 switch 表达式列全所有值，不写 default。
- 注释、日志和界面文字都用中文。
