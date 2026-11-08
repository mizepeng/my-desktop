# 桌面内嵌方案调研：把分区和图标层挂进资源管理器的桌面窗口是否可行

调研时间：2026-11-08。只读调研，没有改代码，也没有在本机安装闭源产品。
开源项目都是在这一天浅克隆的默认分支，链接里带了提交号，行号对应这些提交。
标「推测」的内容没有实测过，标「未知」的是找不到可靠资料。

## 一、结论和建议

1. **不建议把分区和图标层整体改成内嵌**（`SetParent` 进 Progman / WorkerW / SHELLDLL_DefView）。
   - 同类开源产品里，接管或整理桌面的 7 个 fence 类项目有 6 个用独立顶层窗口，和 MyDesktop 一样追层级（NoFences、zsdesk 额外把 owner 设成 Progman）。只有 DeskFrame 用 `SetParent` 挂进 `SHELLDLL_DefView`，但它不接管系统图标；挂进去以后它关掉了模糊背景，要置顶时还得先拆回顶层窗口；混合缩放的双屏上有过「拖到副屏时两块屏的桌面一起冻住」的报告（#107）。
   - 真正跨进程 `SetParent` 的成熟产品只有 Lively 这类动态壁纸。壁纸窗口不直接接收输入（鼠标靠钩子转发），没有菜单、拖放、改名、输入法，所以避开了 MyDesktop 实测过的死锁场景。
   - Stardock Fences 闭源。公开资料只有用户帖和崩溃日志，显示它往 explorer.exe 里加载了 `DesktopDock64.dll`、`FencesMenu64.dll`，分区很可能就画在资源管理器进程里（推测）。这是**同进程**的内嵌，不存在跨进程共享输入队列的问题，但要注入资源管理器，风险、兼容性和杀毒误报都是另一个量级，不建议。
   - 内嵌能解决的只有两件事：任务视图关闭后晚约 0.36 秒出现（推测能解决，要实验确认），显示桌面时不用临时置顶。代价是重新踩一遍死锁，还要接受一批硬限制：子窗口的 DPI 必须跟整个窗口树一致（Progman 横跨所有显示器，混合缩放的副屏会按错的 DPI 画），「系统亚克力」用不了，WPF `Window` 本来就不支持当子窗口，屏幕坐标全部要换成父窗口坐标，资源管理器重启时子窗口会跟着被销毁。总的看是得不偿失。
2. **可以做的折中**（按性价比排序）：
   - **任务视图替身**（低风险，建议先做实验）：任务视图打开期间，在专用线程上建一个**原生**分层子窗口挂在 Progman 下（Win11 24H2）或 DefView 下（更早的版本），只贴分区和图标层的截图（`StandInWindow` 已经有截图逻辑），鼠标穿透，不接收输入，任务视图结束就撤掉。真实的分区仍然是顶层窗口，交互一律不变。难点是撤掉替身的时机，见第五节。
   - **其余维持现状**：显示桌面、Aero Peek、资源管理器重启这几块，现在的独立窗口方案已经可用。PecoFence、Pickets、zsdesk、Rainmeter 也都是这个思路（前台事件、定时检查、临时置顶），没有谁找到更省事的办法。
3. **Win10「显示桌面后背景变黑」**：值得试的方向是换掉 `SW_HIDE`，改用资源管理器自己的「不显示图标」开关（`IFolderView2::SetCurrentFolderFlags(FWF_NOICONS, FWF_NOICONS)`，zsdesk 就这样做；PecoFence、Pickets 发 `WM_COMMAND 0x7402`，也就是右键「查看 → 显示桌面图标」那个开关）。有可能根治（推测），但会影响 MyDesktop 依赖的几件事（读位置、写回位置、改名接手），必须先在 Win10 虚拟机上验证，见第六节。
4. **Max 可以自己核实的**：装 Stardock Fences 和小智桌面，用窗口查看工具看分区窗口属于哪个进程、父窗口和 owner 是谁（第三节第 3 条有步骤）。

## 二、对比表

| 产品（技术栈） | 分区窗口形态 | 挂在哪 | 显示桌面（Win+D） | 系统图标怎么藏 | 背景模糊 |
| --- | --- | --- | --- | --- | --- |
| MyDesktop（WPF） | 独立顶层，分层 | 无 owner / parent，插在桌面窗口正上方 | 跟着进入置顶层（Lift / Unlift） | `SW_HIDE` 藏起 SysListView32 | 自己画的毛玻璃，或独立原生窗口加 `WCA_ACCENT_POLICY` |
| DeskFrame（WPF） | **子窗口**（`AllowsTransparency` 的 WPF Window 加 `WS_CHILD`） | `SHELLDLL_DefView` | 子窗口跟着 DefView 走，不用处理；要置顶时先拆回顶层 | 不藏（不接管图标） | 只在拆回顶层、置顶时开 `ACCENT_ENABLE_BLURBEHIND`，挂在桌面里时关闭 |
| NoFences（WinForms） | 独立顶层 | **owner = Progman**（`GWL_HWNDPARENT`） | 没解决：显示桌面时分区消失（#2，到现在仍开着） | 不藏 | 无 |
| Palisades（WPF） | 独立顶层 | 无；`HWND_BOTTOM` 加上在 `WM_WINDOWPOSCHANGING` 里强制 `SWP_NOZORDER` | 未处理 | 不藏 | 无 |
| Desktop Frames+（limbo666/DesktopFences，WPF） | 独立顶层，`WS_EX_NOACTIVATE` | 无 | 用 DWM cloak 状态判断显示桌面；恢复时把 Topmost 先开后关 | `SW_HIDE`（可选） | 无 |
| Pickets（WPF） | 独立顶层 | 无；插在 WorkerW / Progman 之后 | 前台变成桌面时临时置顶，前台换走后放回 | `0x7402`，失败时 `SW_HIDE` | 无 |
| PecoFence（Rust，Win32 + DirectComposition） | 独立顶层，`WS_EX_NOACTIVATE` | 无；插在「装着图标的窗口」正上方，区分 24H2 前后 | 判断桌面被提到分区之上时进入置顶层，此外有 250 ms 前台轮询 | `0x7402`（会写注册表 `HideIcons`）加看门狗 | 未细看 |
| zsdesk / ZSDesk（Rust，Win32 + D2D） | 独立顶层，分层 | **owner = Progman**（`CreateWindowEx` 时传入） | 前台变成 WorkerW / Progman 时插到它上面，180 ms 后再插一次 | **`FWF_NOICONS`**，失败时 `0x7402`，加看门狗进程 | DWM blur-behind 加圆角 region |
| Rainmeter（C++，On Desktop） | 独立顶层，分层（`UpdateLayeredWindow`） | 无；靠一个辅助窗口当层级锚点 | 每 250 ms 检查显示桌面状态，进入后把辅助窗口放进置顶层，皮肤排在它后面 | 不涉及 | 可选 DWM blur-behind |
| Lively（壁纸，C#） | **子窗口**（壁纸播放器，多为独立进程） | 24H2：Progman 下、DefView 和 WorkerW 之间；更早：0x052C 之后的壁纸 WorkerW | 子窗口，不用处理 | 不涉及 | 不涉及 |
| Stardock Fences（闭源） | 未知；公开资料显示有 DLL 加载进 explorer.exe | 未知 | 未知 | 未知 | 未知 |
| 小智桌面（闭源） | 未知（没找到可靠资料） | 未知 | 未知 | 未知 | 未知 |

## 三、各产品细节（附源码位置）

### 1. DeskFrame：唯一跨进程内嵌的 fence 类项目
仓库 [PinchToDebug/DeskFrame](https://github.com/PinchToDebug/DeskFrame)，提交 `a9e4090`（2026-03），539 星。
- 内嵌：`SetAsDesktopChild` 先 `EnumWindows` 找到装着 `SHELLDLL_DefView` 的顶层窗口（Win10 上可能是 WorkerW，24H2 上是 Progman），对 WPF 窗口调 `SetParent(hwnd, shellView)`，去掉 `WS_POPUP`、加上 `WS_CHILD`，再用 `ScreenToClient` 把坐标换成相对 DefView 的坐标（[DeskFrameWindow.xaml.cs:1285-1330](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/DeskFrameWindow.xaml.cs#L1285-L1330)）。窗口是 `AllowsTransparency="True"`、`WindowStyle="None"`（[DeskFrameWindow.xaml:16-18](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/DeskFrameWindow.xaml#L16-L18)）。
- 每次移动都换算父窗口坐标（`AdjustPositionAsync`，同一文件 1332-1370）。锁定、切换样式时先 `SetParent(hwnd, 0)` 拆出来，再挂回去（2205、2222 行附近）。
- 置顶：在标题栏上滚轮向上时 `SetParent(hwnd, 0)` 拆回顶层，再设 `Topmost = true` 并打开模糊；滚轮向下时关掉模糊、重新 `SetAsDesktopChild`（[583-625](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/DeskFrameWindow.xaml.cs#L583-L625)）。模糊用的是 `SetWindowCompositionAttribute` 的 `ACCENT_ENABLE_BLURBEHIND`（[3604-3621](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/DeskFrameWindow.xaml.cs#L3604-L3621)）。也就是说**挂在桌面里时不开模糊**，和「DWM 背景效果只对顶层窗口有效」吻合（作者没写原因，这是推测）。
- 交互：右键菜单（`ShellContextMenu.ShowContextMenu`）、`DragDrop.DoDragDrop`、改名用的 TextBox 都直接在这个子窗口所在的 WPF 界面线程上跑（663、2506、2557、2825、2972 行附近），没有为跨进程做特别处理。
- 不藏系统图标：`MainWindow` 只用 SysListView32 的句柄判断鼠标是不是点在桌面空白处（[MainWindow.xaml.cs:80-146](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/MainWindow.xaml.cs#L80-L146)）。
- 清单里 Win8 / Win10 的 `supportedOS` 都注释掉了（[app.manifest:25-45](https://github.com/PinchToDebug/DeskFrame/blob/a9e409079d0de5365fa889622d58ce8ed819ab27/DeskFrame/app.manifest#L25-L45)）。按文档，分层子窗口要 Win8 起才支持。它照样能显示，原因未知（未核实）。
- 相关 issue：
  - [#107](https://github.com/PinchToDebug/DeskFrame/issues/107)：主屏 4K 150%、副屏 2K 100%，拖到副屏时「两块屏的桌面都冻住」。作者复现不了，后来标为已修复。推测和「子窗口必须跟父窗口树同一个 DPI」以及跨进程同步有关。
  - [#22](https://github.com/PinchToDebug/DeskFrame/issues/22)、[#46](https://github.com/PinchToDebug/DeskFrame/issues/46)：Win+D 之后鼠标移上去分区消失。
  - [#138](https://github.com/PinchToDebug/DeskFrame/issues/138)：低频 CPU 上右键卡顿。
  - 都没有作者给出的根因。
- 没找到它处理资源管理器重启（`TaskbarCreated`）的代码，`SetAsDesktopChild` 在 DefView 找不到时会每秒重试（1287-1302 行）。

### 2. NoFences
仓库 [Twometer/NoFences](https://github.com/Twometer/NoFences)，提交 `90374ca`（2024-01），692 星，WinForms。
- `GlueToDesktop` 把 `GWL_HWNDPARENT` 设成 Progman，也就是 **owner** 关系，不是父子关系（[Win32/DesktopUtil.cs:38-43](https://github.com/Twometer/NoFences/blob/90374ca17d3d308f135325bc62bfa21d8dde240b/NoFences/Win32/DesktopUtil.cs#L38-L43)）。拿到焦点时 `SetWindowPos(HWND_BOTTOM)`（[FenceWindow.cs:113-117](https://github.com/Twometer/NoFences/blob/90374ca17d3d308f135325bc62bfa21d8dde240b/NoFences/FenceWindow.cs#L113-L117)）。
- 显示桌面时分区消失，作者表示没找到办法（[#2](https://github.com/Twometer/NoFences/issues/2)）。可见 owner = Progman 并不能让分区挺过显示桌面。兄弟会话在本机实测，owner 设成 Progman 对任务视图也无效。
- 跨线程的 owner 关系同样会连接输入队列（见第四节），NoFences 承担了这个风险，没有做规避。

### 3. Stardock Fences（闭源）
- 公开资料：
  - Stardock 论坛 Fences 6.52 测试反馈帖（2026-07-31，**用户**发帖，说是让 Codex 分析的，不是官方说法）称：Fences 往资源管理器里注入 `FencesMenu64.dll` 和 `DesktopDock64.dll`，用来管理桌面图标和点击；在 Win11 25H2 上和 Start11 注入的 DLL 冲突（[帖子](https://forums.stardock.com/541769/fences-6-52-beta-feedback)）。
  - 更早的崩溃报告里，explorer.exe 的出错模块是 `...\Stardock\Fences\DesktopDock64.dll`（[论坛帖](https://forums.sorcererking.com/433651)）。
  - 2009 年的老帖里有用户说「分区本身是加载进 explorer 的 DLL，Fences 程序只负责配置」（搜索结果摘要，未逐字核对）。
- 推断：Fences 的分区很可能是资源管理器进程里的窗口，属于同进程内嵌，没有跨进程输入队列问题。这条路要做 DLL 注入，MyDesktop 不应该走。**以上是推测，没有官方文档。**
- 小智桌面：没找到可靠的技术资料，未知。
- **Max 自己核实的步骤**（需要 Max 同意后再装，建议在虚拟机或沙盒里装）：
  1. 安装并启动 Fences 或小智桌面，建一个分区。
  2. 用 System Informer（原 Process Hacker）或 Spy++ / WinSpy 的「查找窗口」准星拖到分区上，记下：句柄、类名、所属**进程**（是 explorer.exe 还是它自己的 exe）、`GetParent` / `GW_OWNER`、样式（有没有 `WS_CHILD`、`WS_EX_LAYERED`、`WS_EX_NOACTIVATE`）。
  3. 在 System Informer 里打开 explorer.exe →「Modules」，看有没有该产品的 DLL（Fences：`DesktopDock64.dll`、`FencesMenu64.dll`）。
  4. 分别按 Win+D、Win+Tab（看关闭任务视图时分区是不是和壁纸同时出现）、鼠标悬停任务栏最右边（Aero Peek），再在任务管理器里重启资源管理器，看分区的表现。
  5. 第 2 步在 Win+D 前后各做一次，看分区窗口的父窗口或进程有没有变化。

### 4. Desktop Frames+（limbo666/DesktopFences）
仓库 [limbo666/DesktopFences](https://github.com/limbo666/DesktopFences)，提交 `d255804`（2026-10），WPF。
- 窗口基类 `NonActivatingWindow` 加 `WS_EX_NOACTIVATE`，`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`（[NonActivatingWindow.cs](https://github.com/limbo666/DesktopFences/blob/d2558046b000482ee7e26ca1bd65aed31b9d4357/Code/Desktop%20Frames/NonActivatingWindow.cs)），没有 owner，也没有 parent。
- 判断显示桌面靠 DWM cloak 状态（[DesktopStateDetector.cs:46-80、130-137](https://github.com/limbo666/DesktopFences/blob/d2558046b000482ee7e26ca1bd65aed31b9d4357/Code/Desktop%20Frames/DesktopStateDetector.cs#L46-L137)）。恢复时把 `Topmost` 先设 true 再设 false，把窗口拉回前面（[App.xaml.cs:243-268](https://github.com/limbo666/DesktopFences/blob/d2558046b000482ee7e26ca1bd65aed31b9d4357/Code/Desktop%20Frames/App.xaml.cs#L243-L268)）。
- 藏系统图标用 `ShowWindow(SysListView32, SW_HIDE)`，和 MyDesktop 一样（[DesktopIconManager.cs:68-104](https://github.com/limbo666/DesktopFences/blob/d2558046b000482ee7e26ca1bd65aed31b9d4357/Code/Desktop%20Frames/DesktopIconManager.cs#L68-L104)）。

### 5. Palisades
仓库 [Xstoudi/Palisades](https://github.com/Xstoudi/Palisades)，提交 `d779e61`（2022-07，已停更）。加载时 `SetWindowPos(HWND_BOTTOM)`，之后在 `WM_WINDOWPOSCHANGING` 里强制加 `SWP_NOZORDER`，层级永远不变（[WindowSinker.cs:78-100](https://github.com/Xstoudi/Palisades/blob/d779e61c0a62c06518a1f0113d5d71876316469d/Palisades.Application/Helpers/WindowSinker.cs#L78-L100)）。是最简单的「沉底」方案，没有处理显示桌面。

### 6. Pickets
仓库 [chrisdfennell/Pickets](https://github.com/chrisdfennell/Pickets)，提交 `21f17b8`（2026-10），WPF。
- 启动时向 Progman 发 `0x052C`，让系统建出 WorkerW（[DesktopHelper.cs:45-48](https://github.com/chrisdfennell/Pickets/blob/21f17b8a364caed7e8db61025512c9bf9cbb55ce/Pickets/Services/DesktopHelper.cs#L45-L48)）。注意这会让 Win10 进入 0x052C 布局，和 MyDesktop 黑屏涉及的是同一种布局。
- 分区插在 WorkerW（没有就用 Progman）之后（[DesktopHelper.cs:429-439](https://github.com/chrisdfennell/Pickets/blob/21f17b8a364caed7e8db61025512c9bf9cbb55ce/Pickets/Services/DesktopHelper.cs#L429-L439)）。
- 显示桌面：`ShowDesktopWatcher` 监听前台切换，前台变成 Progman 或 WorkerW 时临时置顶，前台换走后放回（[ShowDesktopWatcher.cs:10-60](https://github.com/chrisdfennell/Pickets/blob/21f17b8a364caed7e8db61025512c9bf9cbb55ce/Pickets/Services/ShowDesktopWatcher.cs#L10-L60)），思路和 MyDesktop 的 Lift / Unlift 相同。
- 藏图标：先发 `0x7402`（右键「显示桌面图标」那个开关），失败时 `SW_HIDE`（[DesktopHelper.cs:14、56-96](https://github.com/chrisdfennell/Pickets/blob/21f17b8a364caed7e8db61025512c9bf9cbb55ce/Pickets/Services/DesktopHelper.cs#L56-L96)）。

### 7. PecoFence
仓库 [DayuanJiang/PecoFence](https://github.com/DayuanJiang/PecoFence)，提交 `f6ff439`（2026-11-08），852 星，Rust 写的 Win32 程序。
- 区分两代桌面结构：24H2 及以后 DefView 一直在 Progman 下；23H2 及以前桌面被提起时 DefView 搬进顶层 WorkerW。每次校正都重新找「装着图标的窗口」（[platform/src/desktop.rs:1-30](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/platform/src/desktop.rs#L1-L30)）。
- `insert_above` 把分区插在这个窗口正上方。注释里记了和 MyDesktop constraints.md 第 72 条相同的坑：插在以管理员身份运行的窗口之后会被 UIPI 拒绝；`HWND_BOTTOM` 会落到被提起的桌面下面（[desktop.rs:296-345](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/platform/src/desktop.rs#L296-L345)）。
- 显示桌面：检测到「桌面被提到分区之上，且桌面在前台」时进入置顶层。注释写明「系统不允许普通窗口排到被提起的桌面之上（SetWindowPos 成功但什么都不变）」（[app/src/anchor.rs:972-990](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/app/src/anchor.rs#L972-L990)）。另有 250 ms 的前台轮询，因为 Win10 上有些「桌面到前台」不发事件（[anchor.rs:22-27](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/app/src/anchor.rs#L22-L27)）。
- 藏图标：发 `WM_COMMAND 0x7402`，由资源管理器自己把状态存进注册表 `HideIcons`，再用看门狗保证恢复（[platform/src/shell_icons.rs:1-5、50-](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/platform/src/shell_icons.rs#L1-L5)）。注释里实测：1400 个图标空闲时切换约 0.3 秒，刚登录时要 20 秒（anchor.rs:37-48）。
- Peek（类似 Fences 的 Win+Space）：每个显示器铺一层半透明遮罩，分区放进置顶层。`SetForegroundWindow` 在系统把焦点交给别的输入队列时可能卡住，所以放在单独的线程上调（[app/src/peek.rs:1-10、100-110](https://github.com/DayuanJiang/PecoFence/blob/f6ff439d008d5b39a522dc013804cdb2c60f99d5/crates/app/src/peek.rs#L100-L110)）。
- 没有找到它处理任务视图的代码。

### 8. zsdesk（ZSDesk 的「桌面格子」）
仓库 [qiu7824/zsdesk](https://github.com/qiu7824/zsdesk)，提交 `6bf51a4`（2026-11-07），Rust 写的 Win32 程序，有腾讯桌面整理布局导入。
- 分区是 owner 为 Progman 的顶层分层窗口：`CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW, …, WS_POPUP, …, owner = progman)`（[desktop/host.rs:704-712](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-app/src/desktop/host.rs#L704-L712)，[desktop/mod.rs:1-8](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-app/src/desktop/mod.rs#L1-L8)）。
- 即便设了 owner，显示桌面时仍要追层级：前台变成 WorkerW / Progman 时插到它上面，180 ms 后再插一次，因为系统可能在事件之后才把 WorkerW 提完（[mod.rs:87-91、306-313](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-app/src/desktop/mod.rs#L306-L313)）。弹出菜单、owner 被激活后还要修一次层级（[host.rs:727-783](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-app/src/desktop/host.rs#L727-L783)）。
- 资源管理器重启：「owner 被销毁，分区窗口跟着 Progman 一起被销毁」，收到 `TaskbarCreated` 后全部重建（[host.rs:266-279](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-app/src/desktop/host.rs#L266-L279)）。内嵌方案也会遇到同样的问题。
- **藏图标用 `FWF_NOICONS`**：经 `IShellWindows::FindWindowSW(SWC_DESKTOP)` 拿到桌面的 `IFolderView2`，调 `SetCurrentFolderFlags(FWF_NOICONS, …)`；没生效时退回 `0x7402`；判断是否藏好看 SysListView32 是否可见（[zsdesk-windows/src/desktop.rs:1-8、100-125](https://github.com/qiu7824/zsdesk/blob/6bf51a4ed196c125c87061125254d1360eb39462/crates/zsdesk-windows/src/desktop.rs#L100-L125)）。从这个判断方式看，`FWF_NOICONS` 也会让列表窗口变成不可见，只不过是资源管理器自己藏的（推测）。

### 9. Rainmeter（On Desktop）
仓库 [Rainmeter/rainmeter](https://github.com/Rainmeter/rainmeter)，提交 `54df773`（2026-10）。
- 皮肤是独立顶层分层窗口（`WS_EX_LAYERED | WS_EX_TOOLWINDOW`，`UpdateLayeredWindow`，[Skin.cpp:295、3249](https://github.com/Rainmeter/rainmeter/blob/54df7737c615ebe5a6b1d63e539aec171b54aec7/Library/Skin.cpp#L3249)），不内嵌。
- 用一个禁用的辅助窗口 `PositioningHelper` 当层级锚点（[System.cpp:78-100](https://github.com/Rainmeter/rainmeter/blob/54df7737c615ebe5a6b1d63e539aec171b54aec7/Library/System.cpp#L78-L100)）。每 250 ms 检查一次显示桌面状态（`TIMER_SHOWDESKTOP`，29、36、117 行）。进入显示桌面后把辅助窗口放进置顶层，「On Desktop」皮肤排在它后面（[System.cpp:438-530](https://github.com/Rainmeter/rainmeter/blob/54df7737c615ebe5a6b1d63e539aec171b54aec7/Library/System.cpp#L438-L530)，[Skin.cpp:1035-1050](https://github.com/Rainmeter/rainmeter/blob/54df7737c615ebe5a6b1d63e539aec171b54aec7/Library/Skin.cpp#L1035-L1050)）。
- 注释记录了 24H2 的桌面结构变化，用 `GetCurrentMonitorTopologyId` 是否存在来判断 24H2（[System.cpp:225-252](https://github.com/Rainmeter/rainmeter/blob/54df7737c615ebe5a6b1d63e539aec171b54aec7/Library/System.cpp#L225-L252)）。
- Rainmeter 发展了十几年，桌面挂件也没有选内嵌，侧面说明内嵌的交互问题不好解决（推测）。

### 10. Lively（动态壁纸）
仓库 [rocksdanister/lively](https://github.com/rocksdanister/lively)，提交 `25a0a5f`（2026-09）。
- [WinDesktopCore.cs:147-245](https://github.com/rocksdanister/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs#L147-L245) 引用了微软对 24H2 的说明，大意是：Progman 改成带 `WS_EX_NOREDIRECTIONBITMAP`（本身没有 GDI 内容），DefView 是 `WS_EX_LAYERED` 子窗口；桌面被提起时在 DefView 下面建一个画壁纸的子 WorkerW；强制进入提起状态的程序要自己建一个分层子窗口，夹在 DefView 和 WorkerW 之间。
- 24H2：给壁纸窗口加 `WS_CHILD`，分层透明度设成 255，`SetParent` 到 Progman，再插在 DefView 之后。更早的版本：发 `0x052C` 后挂到画壁纸的 WorkerW 下（[1037-1075](https://github.com/rocksdanister/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs#L1037-L1075)）。
- 资源管理器重启：`TaskbarCreated` 时比较 explorer 的 pid，变了就重建全部壁纸；30 秒内多次崩溃就关掉壁纸，免得反复把资源管理器搞崩（[1100-1125](https://github.com/rocksdanister/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs#L1100-L1125)）。WorkerW 被销毁时重挂（249-275）。
- 壁纸在图标下面，不直接接收鼠标；交互靠低级钩子把鼠标转发给壁纸（Lively 的公开功能说明，具体代码这次没细读）。所以它没有菜单、拖放、改名、输入法这些和资源管理器线程纠缠的场景。

## 四、内嵌的已知风险和同类产品的应对

### 1. 跨进程父子 / owner 关系共享输入队列，可能死锁
- Raymond Chen 的说法：跨进程的父子或 owner 关系「技术上合法，但很难做对」；跨线程的父子 / owner 关系会把两个线程的输入队列连起来，而且有传递性，输入处理变成同步的，一方卡住另一方也跟着卡（[Is it legal to have a cross-process parent/child or owner/owned window relationship?](https://devblogs.microsoft.com/oldnewthing/?p=4683)）。Stack Overflow 上有不少「把别的进程的窗口挂成子窗口后，宿主弹 MessageBox 卡死」的报告。
- 本项目已经实测过（constraints.md 第一条：右键菜单等场景两个进程互相等待）。分区窗口的右键菜单是 Shell 的 `IContextMenu` 加 `TrackPopupMenuEx`，在界面线程上进入模态循环（`Native/ShellContextMenu.cs:178`、`Native/NativeMenu.cs:162`），菜单处理程序里有不少会和资源管理器同步通信；拖放（OLE 模态循环、资源管理器是放置目标）、`ForceForegroundWindow`（注入按键、抢前台）都在同一个线程上。这些和 DeskFrame 的做法一样，但 DeskFrame 没有接管图标、没有键盘钩子，也没有跨进程读写 IFolderView。
- 同类产品的应对：
  - **不内嵌**：Rainmeter、PecoFence、Pickets、Desktop Frames+、Palisades，大多数都这样。
  - **只内嵌不交互的窗口**：Lively，输入走钩子转发。
  - **同进程**：Fences 注入 explorer（推测）。
  - **owner 而不是 parent**：NoFences、zsdesk。owner 同样会连接输入队列（Chen 的文章把两者并列），风险一样，只是关系更松，系统不会把 owner 窗口的消息同步传给子窗口（推测）。
  - 没有找到哪个项目「专门开一个线程承载内嵌窗口、和业务线程隔离」。PecoFence 把 `SetForegroundWindow` 挪到单独线程，是同一思路的局部做法。
- 如果硬要内嵌，规避思路（都没验证）：
  1. 内嵌窗口放在**专用的界面线程**上，只负责显示和转发输入，菜单、拖放、Shell 调用、抢前台都放到别的线程上的顶层窗口做。WPF 可以多个 Dispatcher 线程，但 MyDesktop 的分区窗口里交互和显示是一体的，等于重写。
  2. 内嵌窗口完全不接收输入（`WS_EX_TRANSPARENT` 加 `WM_NCHITTEST` 返回 `HTTRANSPARENT`），交互由顶层窗口接住。这样「内嵌」只剩显示作用，就是第五节的「替身」思路。

### 2. WPF 窗口当跨进程子窗口
- WPF `Window` 不支持当子窗口：官方的子窗口做法是 `HwndSource` 加 `WS_CHILD`，或 `HwndHost`。DeskFrame 是硬改样式，能跑，但 `Left`、`Top`、`WindowState`、`Activate`、`Topmost` 这些属性的语义都会出问题，它在多处手动换算坐标、先拆出来再挂回去。
- 透明：从 Win8 开始分层窗口可以是子窗口（[Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)，要求清单里声明 Win8 及以上；MyDesktop 的 `app.manifest:14` 已经声明了 Win10）。WPF `AllowsTransparency` 用的是 `UpdateLayeredWindow`，挂成子窗口后理论上照常（推测，DeskFrame 的实际表现是可以）。
- 硬件加速：WPF 透明窗口不论开不开硬件加速都要拷回内存再 `UpdateLayeredWindow`，挂成子窗口不会变好，也不会变差（推测）。
- 背景效果：
  - 自己画的毛玻璃（`WallpaperBlur`）只是一张位图，不受影响。
  - 「系统亚克力」（`SetWindowCompositionAttribute`）和 DWM 窗口属性（圆角、边框颜色、cloak）只对顶层窗口有效（DWM 的通用行为，DeskFrame 挂在桌面里时也关掉了模糊）。所以「系统亚克力」内嵌后只能放弃，或者仍做顶层窗口，回到追层级的老路。
- DPI：子窗口必须和父窗口树同一个 DPI（[High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)：「Child windows must continue to match the scaling size of their parent」）。Progman / DefView 横跨整个虚拟屏幕，只有一个 DPI，所以**混合缩放的多屏上，副屏的分区会按主屏的 DPI 画**：WPF 按父窗口的 DPI 排版，字和图标在副屏上大小不对。只能自己按显示器缩放内容，现在 `FenceManager.RefreshWindowsDpi` 和按 DPI 记布局的逻辑都要重做。同一篇文档还说，跨进程 `SetParent` 时两边 DPI 感知模式不同会「强制重置子窗口进程的 DPI 感知」；资源管理器和 MyDesktop 都是 PMv2，这一点不会触发（推测）。
- 焦点和输入法：点击子窗口时，被激活的是顶层的 Progman / WorkerW（资源管理器的窗口），键盘焦点经共享的输入队列落到子窗口。输入法上下文按线程分配，两个进程的线程连着输入队列时 TSF 的行为没有文档（未知）。改名框、搜索这类要输入中文的地方必须实测。现在的键盘钩子转发（`DesktopMouseWatcher` 拦 Delete、F2 等按键）也要重新设计，因为前台窗口变成了资源管理器的桌面。
- 资源管理器重启：父窗口被销毁时子窗口一起被销毁，WPF 的 `HwndSource` 会被关掉。现在的分区是独立窗口，资源管理器重启后分区不动，看门狗只需重新藏图标；内嵌后要在 `TaskbarCreated` 时重建全部分区窗口，相当于重启一遍界面（zsdesk、Lively 都是这样做的）。
- Win10 的桌面结构：显示桌面时 DefView 在 Progman 和 WorkerW 之间搬家（见 `win10-show-desktop-black-wallpaper` 笔记第二轮）。挂在 Progman 下，显示桌面时会被提起的 WorkerW 盖住；挂在 DefView 下，子窗口跟着 DefView 搬（`SetParent` 的父子关系不变，DefView 自己换了父窗口，推测这样正好）。所以 Win10 上只能挂 DefView，Win11 24H2 上挂 Progman 或 DefView 都行。

### 3. 同类产品显示桌面、任务视图、Aero Peek 的处理
- 显示桌面：独立窗口方案都要追（MyDesktop、PecoFence、Pickets、zsdesk 都是检测到后临时置顶或插到被提起的桌面上面，Rainmeter 每 250 ms 轮询一次）；内嵌方案天然不用处理（DeskFrame、Lively）。
- 任务视图：开源项目里没找到专门处理的代码。兄弟会话实测：Win11 上普通顶层窗口（包括 owner 设成 Progman 的）都要等退出动画放完约 370 ms 才出现。内嵌窗口属于 Progman 的窗口树，推测会和壁纸、系统图标一起显示（Lively 的壁纸在任务视图里可见，可以旁证，未在本机实测）。
- Aero Peek：PecoFence 有自己的 Peek（遮罩加置顶），处理的是 Fences 式的「Win+Space」，不是系统的 Aero Peek。系统 Aero Peek 期间普通窗口被换成轮廓，桌面层可见；内嵌窗口推测可见，独立窗口需要 `DWMWA_EXCLUDED_FROM_PEEK`（兄弟会话实测它对任务视图无效，对 Aero Peek 未测）。

## 五、对 MyDesktop 的结论

### 1. 全面内嵌：不建议
要改的模块（按对窗口层级和屏幕坐标的依赖统计：`DesktopHost.` 被 15 个文件引用 107 次，`SetZOrder` / `PlaceAfter` / `KeepAboveDesktop` / `KeepBackdropAboveFloor` 有 36 处）：
- `Core/DesktopHost`：层级维护的大部分逻辑可以删，但要换成「找父窗口、挂上去、Win10 跟着 DefView 搬、资源管理器重启后重挂」，`LayeredWindowClass` 建的飞行图标、截图顶替窗口还得留在顶层，和内嵌窗口之间的上下关系要重新设计（顶层窗口一定在桌面整棵窗口树之上，飞行图标会盖在分区之上，这个问题不大）。
- `Views/FenceWindow*`（4554 行）：全部屏幕坐标改成父窗口坐标；去掉 Lift、悬停展开时的 `RaiseExpanded` / `LowerExpanded` 和置顶相关逻辑；卷起展开动画、`StandInWindow` 换手、`ShowInPlaceOf`（依赖 DWM cloak，子窗口不能用）都要重写；DPI 改成自己按显示器缩放。
- `Views/DesktopLayerWindow*`：同上。只盖住图标范围、拖动时扩成整屏的做法可以保留。
- `Views/AcrylicBackdrop`：系统亚克力做不成子窗口，只能下掉这个选项，或者保留顶层窗口继续追层级。
- `Services/FenceManager`：显示桌面临时置顶、`EnsureAllOnScreen` / `PullIntoWorkArea` 的坐标、标签页切换的换手、按 DPI 记布局。
- `Services/DesktopTakeover`：看门狗从「重新藏图标」变成「重建全部分区窗口」。
- `Core/DesktopMouseWatcher`、`Views/ClickCatcherWindow`：命中测试、前台判断、按键转发都要按新结构重新考虑。
- 右键菜单、拖放、改名：要么搬到不内嵌的线程或窗口上（大重构），要么接受死锁风险。
- 工作量：3～6 周，外加多版本 Windows、多显示器的回归（推测）。风险高：死锁是用户最不能接受的故障（整个桌面和资源管理器一起卡死），而它恰恰是 MyDesktop 实测出来的。

### 2. 折中方案：任务视图替身（建议先做实验）
- 做法：任务视图打开时，在一条**专用线程**上建一个原生分层子窗口（借 `DrawFrameWindow` / `FlyingIcon` 那套 `UpdateLayeredWindow` 的写法）。Win11 24H2 挂 Progman，插在 DefView 和画壁纸的 WorkerW 之间（Lively 的位置）或 DefView 之上；Win10 挂 DefView。窗口里贴每块显示器上分区、图标层的截图（WPF 的 `RenderTargetBitmap`，`StandInWindow` 已经在用）。鼠标穿透，`WM_NCHITTEST` 返回 `HTTRANSPARENT`，不接受激活。任务视图结束后撤掉，再销毁窗口、解除父子关系。
- 为什么风险低：这条线程上只有这一个窗口，只做显示，不进任何模态循环，也不调 Shell。即使和资源管理器的线程连了输入队列、被资源管理器拖住，也只是替身晚一点撤，主界面线程和分区不受影响。不过要注意传递性：这条线程上的窗口不能和本程序的其他窗口有 owner 关系。
- 要实验解决的问题：
  1. 任务视图期间，Progman / DefView 的子窗口是否真的一直可见（兄弟会话的推测，没实测过）。
  2. **撤替身的时机**：分区是半透明的，替身和真分区叠着就会显得更不透明。退出动画结束、顶层窗口重新出现的那一帧要撤掉替身，但兄弟会话测到本程序窗口的 cloak 状态、可见性都不变。要找一个信号：任务视图窗口 `XamlExplorerHostIslandWindow` 的隐藏或销毁事件、`EVENT_OBJECT_CLOAKED` / `UNCLOAKED`、前台切换的时刻再加一个固定延迟。找不到可靠信号，就只能在「晚 0.36 秒出现」和「交接时闪一下更不透明的画面」之间二选一。
  3. 替身只需要盖住关闭动画那 0.36 秒：任务视图打开时（前台变成 `XamlExplorerHostIslandWindow`，标题「任务视图」）建好窗口、贴好截图，挂上去先不显示，检测到任务视图开始关闭时再显示。要实测能不能及时知道「开始关闭」（前台切换、按键钩子看到 Esc / Win+Tab / 点击都算），晚了就还是会空一下。
  4. Win10 的任务视图行为没测过，要在 Win10 虚拟机上确认问题是否存在。
- 工作量：探针 1 天，做成功能 2～4 天（推测）。

### 3. 维持现状的部分
显示桌面、Aero Peek、资源管理器重启这几块，同类的独立窗口产品都在做同样的追踪，MyDesktop 的实现（在 `WM_WINDOWPOSCHANGING` 里改写插入位置、UIPI、owner 陷阱）比开源同类更细，没有发现更好的现成做法。PecoFence 的 250 ms 前台轮询（Win10 上有些「桌面到前台」不发事件）可以留作参考。

### 4. 如果仍想验证全面内嵌：最小原型
单独建一个 WPF 测试程序，不动 MyDesktop：
1. 一个 `AllowsTransparency` 的 WPF 窗口，改用 `HwndSource` 加 `WS_CHILD` 挂到 DefView（Win10、Win11 都试），放一个半透明面板、一个 TextBox、一个 Shell 右键菜单（照搬 `Native/ShellContextMenu`）、一个拖放源和放置目标。
2. 测试清单：
   - Win+D、Win+Tab（看关闭时是否和壁纸同时出现）、Aero Peek、任务管理器里重启资源管理器。
   - **死锁**：在分区里弹 Shell 右键菜单的同时，在桌面空白处弹资源管理器的菜单；分区菜单打开时用 Process Explorer 挂起资源管理器的桌面线程，看测试程序是否卡住；反过来让测试程序界面线程 `Sleep` 10 秒，看资源管理器的桌面和任务栏是否卡住。
   - 拖放：从分区拖到资源管理器窗口、从资源管理器窗口拖进分区、从分区拖到桌面空白处。
   - 改名框里用微软拼音输入中文；按住 Win 键的快捷键。
   - 混合缩放（100% 和 150%）的双屏，在两块屏之间拖动（对照 DeskFrame #107）。
   - 开关硬件加速时的内存（`NativeMethods.DriverMemoryBytes`）；`WCA_ACCENT_POLICY` 在子窗口上是否生效。
   - 动态壁纸（Wallpaper Engine、Lively）开着时分区是否可见。
3. 死锁那几条有任何一条复现，就可以结束这条路线了。

## 六、Win10「显示桌面后背景变黑」能否换一种藏图标的方式根治

### 1. 现状和推测的原因
见兄弟会话笔记 `win10-show-desktop-black-wallpaper.md` 第二轮：在 0x052C 布局下（解锁、登录时的壁纸淡入动画建出画壁纸的 WorkerW），DefView 所在的 WorkerW 推测靠图标列表画出可见内容；MyDesktop 用 `SW_HIDE` 从外面藏起列表后，这块没人画，就是纯黑（推测，未实测）。用户已确认不开 MyDesktop 时不黑。

### 2. 同类产品藏系统图标的方式
| 方式 | 谁在用 | 特点 |
| --- | --- | --- |
| `ShowWindow(SysListView32, SW_HIDE)` | MyDesktop、Desktop Frames+，Pickets 用作后备 | 从外面藏，资源管理器不知道，状态不保存；资源管理器自己重新显示时要再藏 |
| `WM_COMMAND 0x7402` 发给 DefView | PecoFence、Pickets，zsdesk 用作后备 | 就是右键「查看 → 显示桌面图标」，资源管理器自己藏，**写注册表 `HideIcons`**（PecoFence 注释），所以程序崩溃后图标仍然藏着，要靠看门狗恢复；图标多时切换慢（1400 个约 0.3 秒，刚登录 20 秒） |
| `IFolderView2::SetCurrentFolderFlags(FWF_NOICONS, FWF_NOICONS)` | zsdesk | 资源管理器自己藏（zsdesk 用列表是否可见来确认结果）；是否写注册表未知；zsdesk 同样配了看门狗进程 |
| 不藏 | DeskFrame、NoFences、Palisades | 不接管图标 |

### 3. `FWF_NOICONS` 能否根治黑屏
- 支持的理由（推测）：「显示桌面图标」是很多用户本来就关着的设置。如果资源管理器自己关掉图标后在 0x052C 布局下也会黑，应该到处都有人报，但没找到这类报告。资源管理器知道图标被关了，就会自己处理背景的绘制。
- 不确定的地方：资源管理器内部是不是也只是把列表 `SW_HIDE`（zsdesk 的判断方式暗示列表确实变成不可见）。如果是，黑不黑取决于 DefView 有没有因此改用别的方式画背景。只有实验能回答。兄弟会话笔记里给用户的第 3 个问题（退出 MyDesktop，关掉「显示桌面图标」，锁屏、解锁、点显示桌面，看会不会黑）问的就是这件事，答案是「不黑」的话，`FWF_NOICONS` 和 `0x7402` 都值得做。
- 对 MyDesktop 的影响（都要验证）：
  1. `ExplorerDesktopView` 的快照（位置、间距、自动排列）和 `SelectAndPositionItems` 写回：视图开着 `FWF_NOICONS` 时是否照常维护和报告图标位置（未知，这一条不成立就不能用）。
  2. 改名接手：系统「新建」后改名是在隐藏的列表里开始的，靠 WinEvent 发现；`FWF_NOICONS` 时可能根本不进入改名（未知），`DesktopTakeover` 的改名接手要重新测。
  3. 是否写注册表 `HideIcons`、资源管理器重启后是否保持：如果会保存，程序崩溃后用户的图标会一直藏着，守护进程（`--guard`）要改成恢复这个开关；还要记住用户原本的设置，用户本来就关着图标时，退出后不能替他打开。
  4. 和用户在右键菜单里切换「显示桌面图标」的交互：用户手动打开时，MyDesktop 要不要再关上（现在是看到列表重新显示就再藏）。
  5. 拖放到桌面空白处、桌面右键菜单：现在依赖藏起来的列表还在，`FWF_NOICONS` 下是否一样，要测。
- 建议：只在 Win10 上（`DesktopHost.IsWindows10`）试，Win11 维持 `SW_HIDE`。先在 Win10 22H2 虚拟机上按兄弟会话笔记里的步骤复现黑屏（`0x052C` 或锁屏解锁 → `SW_HIDE` 藏列表 → Win+D），再把第二步换成 `SetCurrentFolderFlags(FWF_NOICONS, FWF_NOICONS)` 和 `0x7402` 各试一次，同时检查上面第 1～3 条。不用 MyDesktop，PowerShell 加 `Add-Type` 写一个探针就能做（`IShellWindows.FindWindowSW(SWC_DESKTOP)` → `IShellBrowser` → `IFolderView2`，`ExplorerDesktopView` 里已经有现成的写法）。
- 如果 `FWF_NOICONS` 也黑：退回笔记里的 F3(b)，画一个壁纸替身。这个替身可以就是第五节第 2 条的那个原生子窗口（挂 DefView，只显示、不交互），两件事共用一套代码。

## 七、参考资料
- 开源仓库（2026-11-08 克隆，提交号见各节）：DeskFrame、NoFences、Palisades、limbo666/DesktopFences（Desktop Frames+）、Pickets、PecoFence、zsdesk、Rainmeter、Lively。任务里提到的「DesktopFences」就是 limbo666/DesktopFences，现名 Desktop Frames+。
- Raymond Chen，[Is it legal to have a cross-process parent/child or owner/owned window relationship?](https://devblogs.microsoft.com/oldnewthing/?p=4683)
- Microsoft Learn：[Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)（分层子窗口从 Win8 起支持）、[Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)、[High DPI Desktop Application Development on Windows](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)（子窗口 DPI 必须和父窗口一致；跨进程 SetParent 的 DPI 规则）
- Stardock 论坛：[Fences 6.52 Beta Feedback](https://forums.stardock.com/541769/fences-6-52-beta-feedback)（用户帖，提到 DLL 注入）、[Fences installs and crashes explorer immediately](https://forums.sorcererking.com/433651)（`DesktopDock64.dll` 崩溃）
- 本项目：`docs/dev/constraints.md`、`docs/dev/architecture.md`；兄弟会话笔记 `win10-show-desktop-black-wallpaper.md`、`win11-task-view-icons-flash.md`（在共享笔记文件夹里，不在仓库）
