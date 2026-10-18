# WinUI 3 重构交接（2026-10-18）

本文交接 MyDesktop 从 WPF 向 WinUI 3 + Composition 的迁移工作，供后续 Agent 直接接手。开发者称呼为“麦格马戈登”，历史文件中的 Max 指同一位开发者。

## 1. 接手结论与第一项工作

**先修复“拖动分区导致程序崩溃退出”，再继续功能迁移。**

2026-10-18 最新测试包已交付，麦格马戈登实机测试时报告拖动分区崩溃。已找到同时间的应用异常栈、Windows 事件和本机 dump。
当前只确认了异常发生位置，尚未复现并查清根因，也没有修改代码修复。不要把之前隔离回归通过当成此问题已经解决。

接手基线：

| 项目 | 状态 |
| --- | --- |
| 工作区 | `F:\Workspaces\GitHub\my-desktop` |
| 目标分支 | `features/20261018_winui3-rewrite` |
| 最新代码提交 | `20aa535`：迁移 WinUI 四向收起与悬停临时展开 |
| Git 状态 | 编写交接文档前已 fetch，与跟踪远程同步，工作区干净；本轮只新增本文 |
| 技术方向 | .NET 10、WinUI 3、Windows App SDK 2.5.1、Composition |
| 发行状态 | WinUI 仅为重构测试入口，完整正式版仍使用 WPF |
| 后续优先级 | 拖动崩溃 → 补齐功能迁移 → 内存、拖动跟手及视觉细节专项 |

不要另起迁移分支，不要改变业务需求，不要把未完成的 WinUI 入口切成正式发行入口。

## 2. 首要缺陷：拖动分区时崩溃退出

### 2.1 用户反馈与已确认事实

- 用户原话：“拖动分区的时候软件崩溃退出了。”
- 测试包为最新代码的 WinUI 包，安装包版本故意标为 `1.3.0`，不能据此判断代码旧。
- 用户尚未说明当时拖动方向、分区收起方向、是否悬停临时展开、是否已做过 Win+D、拖动持续时间及复现频率；这些条件不能自行补成事实。
- 应用日志显示本次启动于 `2026-10-18 22:45:27`，创建了 1 个桌面宿主、3 个分区视图。
- 应用异常记录时间为 **`2026-10-18 22:46:02.836`（Asia/Shanghai）**。
- 异常是 **`System.ComponentModel.Win32Exception (87): 参数错误。`**。
- 当前源码 `WindowApi.SetRegion` 第 31 行位于 **`CreateRoundRectRgn` 返回 0 后的抛错分支**。
- 不能仅凭方法名，把它误写为 `SetWindowRgn` 调用失败；后者的失败分支在另一个位置。

### 2.2 异常调用链

下列为应用日志的异常消息与调用链摘录，省略了每个栈帧重复的绝对目录。行号对应提交 `20aa535`，后续修改会变化。

```text
2026-10-18 22:46:02.836 [ERROR] WinUI 界面发生未处理异常
System.ComponentModel.Win32Exception (87): 参数错误。
   at MyDesktop.WinUI.Native.WindowApi.SetRegion(...)                 WindowApi.cs:31
   at MyDesktop.WinUI.Views.DesktopSurface.UpdateRegion()             DesktopSurface.xaml.cs:415
   at MyDesktop.WinUI.Views.FenceView.ApplyReveal()                    FenceView.Roll.cs:181
   at MyDesktop.WinUI.Views.FenceView.UpdateRollLayout()               FenceView.Roll.cs:169
   at MyDesktop.WinUI.Views.FenceView.TranslateTo(Double x, Double y)  FenceView.xaml.cs:302
   at MyDesktop.WinUI.Services.FencePlacement.Apply(...)              FencePlacement.cs:156
   at MyDesktop.WinUI.Services.FencePlacement.Update(Point point)      FencePlacement.cs:68
   at MyDesktop.WinUI.Views.FenceView.UpdatePlacementPointer(...)      FenceView.Placement.cs:82
   at MyDesktop.WinUI.Views.FenceView.Placement_PointerMoved(...)      FenceView.Placement.cs:67
   at ABI.Microsoft.UI.Xaml.Input.PointerEventHandler.Do_Abi_Invoke(...)
```

首批应阅读的代码：

1. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Native\WindowApi.cs`：区域创建、合并和转交系统的资源生命周期。
2. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Views\DesktopSurface.xaml.cs`：`ExpandedBoundsOf`、`BoundsOf`、`UpdateRegion`。
3. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Views\FenceView.Roll.cs`：`RevealAt`、`ClampTemporary`、`UpdateRollLayout`、`ApplyReveal`。
4. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Views\FenceView.xaml.cs`：`TranslateTo` 用 Composition 父视觉的平移矩阵移动整个分区。
5. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Services\FencePlacement.cs`：按起点绝对位移计算移动、缩放、避让和落点。
6. `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI\Views\FenceView.Placement.cs`：真实 XAML 指针捕获及移动事件入口。

失败点当前代码：

```csharp
var part = CreateRoundRectRgn(rectangle.Left, rectangle.Top, rectangle.Right + 1, rectangle.Bottom + 1, radius * 2, radius * 2);
if (part == 0)
{
	throw new Win32Exception();
}
```

### 2.3 本机证据位置

- 应用日志：`C:\Users\mizep\AppData\Roaming\MyDesktop\app.log`。
- 已确认存在的 dump：`C:\Users\mizep\AppData\Local\CrashDumps\MyDesktop.WinUI.exe.25392.dmp`。
- dump 大小为 `42194351` 字节，最后修改于 `2026-10-18 22:46:06`。不要把 dump 提交到 Git。
- 实际故障程序：`E:\Software\MyDesktop\WinUI-Test\MyDesktop.WinUI.exe`。
- Windows“应用程序”日志：`Application Error`，事件 ID `1000`，时间 `22:46:02`。
- 该事件记录 `Microsoft.UI.Xaml.dll`、异常代码 `0xc000027b`、进程 ID `0x6330`（十进制 25392）。
- `Windows Error Reporting` 事件 ID `1001`，时间 `22:46:05`，关联报告 ID 为 `ef046a1a-61fb-47a8-bd4d-b9f33d0ee45d`。
- WER 的问题签名记录的是 `combase.dll`、`80004005`；它与 Application Error 的模块和代码不同，原样保留，不自行改成一致。

WER 归档目录：

```text
C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_MyDesktop.WinUI._c055d472b2997c956aed4c9bbb8ad6f5022af5b_bf6ddb76_47b7cd85-d357-4520-a311-9718f9a697b5
```

查验时该目录只有 `Report.wer`。事件里列出的 WER 临时 dump 路径不代表文件仍存在；优先使用上面已确认存在的 `CrashDumps` 文件。
本机证据可能被系统清理，本文的异常摘录用于保留最小定位依据。

### 2.4 根因尚未确定，建议这样推进

1. 在独立测试桌面复现；必要时请麦格马戈登补充操作条件，不要自行操控其正在使用的桌面。
2. 在区域创建失败的位置取得准确的矩形、半径、宿主、分区和线程上下文；逐项区分创建、合并、`SetWindowRgn` 的失败。
3. 检查所有参与区域并集的矩形，而不只检查正在拖动的分区。基本宿主还纳入散放图标和提示条，临时浮层也有自己的区域更新。
4. 核对 DIP、物理像素、工作区本地坐标与屏幕坐标的转换，以及 Composition 平移叠加后的边界值。
5. 检查非有限数值、异常尺寸、整数换算、窗口迁移时序和 GDI 区域资源生命周期。它们是待验证方向，不是已确认根因。
6. 根据实际复现结果补失败回归，再做最小修复。不要单纯吞掉异常、跳过失败区域或把全屏加入区域来让界面“看起来能用”。

日志没有记录失败当帧的具体 `RECT`、半径或分区 ID，因此目前不能断言错误矩形来自哪个对象。
当前 `App.xaml.cs` 的未处理异常回调只记录日志，没有将异常标为已处理；真实运行结果已经证明该路径会结束进程。

建议修复后的验收：

- 普通固定展开、收起条、悬停临时展开分别反复拖动，覆盖四个收起方向和屏幕边缘。
- 增加负坐标、多 DPI、多个标签及邻区避让场景；对尚未迁移的跨屏行为，不能伪称已支持。
- 覆盖移动中取消、松手吸附、标签合并，以及临时浮层进入／退出。
- 区域合法、旧位置释放、新位置可命中、外部空白穿透，没有新增黑屏或 GDI 句柄持续增长。
- 退出或异常结束仍能恢复系统图标；固定展开不置顶、悬停展开临时置顶的语义不变。
- 运行现有全部回归并重新打测试包，由麦格马戈登验证实际拖动。

## 3. 已确定的迁移目标和工作方式

- 用户倾向采用新的 Windows 视觉技术，当前已进入正式工程的 WinUI 迁移，不需要重新回到原型选型阶段。
- 目标是减少“每分区独立窗口 + 每分区背景窗口”的层级复杂度；每屏一个基本宿主，分区和标签在宿主内部绘制。
- 悬停临时展开需要跨普通应用显示，因此当前采用每屏按需复用一个临时展开浮层。不是每个分区再创建一对窗口。
- 保留现有业务规则和配置兼容；不要在迁移中顺便改变文件归属、整理、标签或展开的需求。
- **功能迁移优先，内存专项和拖动跟手专项后置。** 进程崩溃属于当前阻塞缺陷，不能与可后置的拖动延迟混为一谈。
- 用户正在使用电脑，明确要求不要操控电脑。只能做代码、编译、只读诊断和不切换过去的独立桌面验证。
- 不要在真实桌面启动第二个接管实例、注入输入、切换桌面、结束 Explorer，或运行一键整理。
- 不擅自执行 Git commit / push。阶段完成且验证通过后，实际检查 `git status`，给中文提交信息，由用户自行提交。

## 4. 工程结构与当前架构

| 工程 | 职责 |
| --- | --- |
| `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.Core` | 与界面框架无关的模型、配置、备份、使用记录、拼音及整理规则、标签、选择、拖放和布局规则 |
| `F:\Workspaces\GitHub\my-desktop\src\MyDesktop.WinUI` | 新入口、每屏宿主、分区及散放视图、系统材质、输入、接管及文件交互 |
| `F:\Workspaces\GitHub\my-desktop\src\MyDesktop` | 保留的 WPF 正式入口，与新入口共用 Core；部分原生 Shell 代码由 WinUI 直接链接复用 |
| `F:\Workspaces\GitHub\my-desktop\tests\MyDesktop.Core.Tests` | 控制台核心规则回归，当前 43 项 |
| `F:\Workspaces\GitHub\my-desktop\tests\MyDesktop.WinUI.Smoke` | 独立 Windows Desktop 的跨进程测试宿主与恢复验证启动器 |

主要入口与职责如下，均位于 `F:\Workspaces\GitHub\my-desktop`：

- `src/MyDesktop.WinUI/Program.cs`、`App.xaml.cs`：启动、单实例、命令退出、会话生命周期和日志。
- `src/MyDesktop.WinUI/Services/DesktopSession.cs`：连接每屏宿主、共享选择、拖放、标签、文件监视和接管。
- `src/MyDesktop.WinUI/Views/DesktopSurface.xaml.cs`：每屏工作区窗口、视图管理、坐标转换、输入区域、临时浮层和编辑宿主。
- `src/MyDesktop.WinUI/Views/FenceView.xaml.cs` 及其 partial 文件：一个标签组的单个视图；切页复用视图，只有活动标签提供内容。
- `src/MyDesktop.WinUI/Views/DesktopIconsView.xaml.cs`：散放图标，画在已有的每屏宿主内部。
- `src/MyDesktop.WinUI/Views/ExpandedSurface.cs`：同屏共享的临时展开浮层。
- `src/MyDesktop.WinUI/Effects/SystemBackdrop.cs`：透明窗口底面及元素级系统亚克力控制器。
- `src/MyDesktop.WinUI/Native/DesktopPlacement.cs`：宿主与 Explorer 的外部层级维护，事件合并触发纠正。
- `src/MyDesktop.WinUI/Native/DesktopInputHooks.cs`、`DesktopInput.cs`：桌面键盘保护、框选协调及临时编辑激活。
- `src/MyDesktop.WinUI/Services/DesktopTakeover.cs`、`Native/IconRecovery.cs`：接管、失败释放及异常结束后的系统图标恢复。
- `src/MyDesktop.WinUI/Services/DesktopSnapshotSource.cs`：独立 STA 读取 Explorer 快照，避免拖住界面线程。
- `src/MyDesktop.WinUI/Services/FileCommands.cs`、`FileDragDrop.cs`、`FileRename.cs`、`FileChangeMonitor.cs`：文件操作及引用同步。
- `src/MyDesktop.WinUI/Services/FencePlacement.cs`、`FenceTabs.cs`、`DesktopGestures.cs`：移动缩放、标签拆合及框选。
- `src/MyDesktop.Core/Services/FenceRoll.cs`、`FenceGeometry.cs`、`FenceWorkspace.cs`：收起、几何及组内布局同步规则。

以上短路径只是目录索引，读取时以本地工作区文件为准。

## 5. 已迁移的功能与边界

### 5.1 基础数据和桌面接管

- Core 保留原配置命名空间及 JSON 格式；新旧入口共用配置、数据目录和单实例互斥体。
- 默认数据目录为 `C:\Users\mizep\AppData\Roaming\MyDesktop`。同一配置不能由 WPF 和 WinUI 同时运行。
- 接入真实桌面分区、映射文件夹、Shell 图像、标签组和物理像素布局保存。
- 散放图标排除全部桌面标签成员，自动排列避开分区，自由排列沿用 Explorer 坐标。
- 图像、键盘保护及恢复进程就绪后才隐藏系统图标；读取或监视保护失败时恢复系统列表。
- 正常退出、退出命令、Explorer 重建及主进程被强制结束均有隔离恢复回归；尊重用户主动隐藏桌面图标的设置。

### 5.2 选择、文件和监视

- 分区及散放共用选择，支持追加选择、全选、导航、打开、框选和取消恢复。
- 接入分区标题、普通文件及普通文件夹改名；输入法组词不直接提交，候选窗及实际输入体验仍需实机验收。
- 普通文件支持复制、剪切、粘贴和删除；桌面内部移动只改归属，跨目录复用 Shell 文件操作。
- 逐项按 Shell 实际结果更新；部分失败不会虚报全部成功，也不会清掉操作期间的新剪贴板。
- 基础拖放使用 WinUI 数据包，支持跨区域多选、按需异步 StorageItems、同盘移动／跨盘复制及 Ctrl／Shift 修饰。
- 拖回桌面写回 Explorer 位置；隔离测试只写测试快照。
- 文件监视同步外部改名、映射目录和全部标签成员、自定义顺序与使用记录；刷新保留正在输入的草稿。

### 5.3 分区布局、标签和四向收起

- 移动、边角缩放、屏幕与邻区吸附、整行整列尺寸吸附、重叠避让已接入；本次真实拖动崩溃使其稳定性仍待修复。
- 普通标题拖动到另一个标题栏可合并标签组；Shift 拖标签支持排序和拖出拆分。
- 悬停／滚轮切换遵循现有开关，滚轮到头不循环；锁定分区可切页，不能修改分组与布局。
- 支持四向收起、自动方向、移动收起条后换边、展开范围保存及组内同步。
- Composition 执行圆角几何裁剪的 Size／Offset 动画，约 220ms；UI 线程同步输入区域，不逐帧调整窗口尺寸。
- 临时展开状态只存在视图内，不改写配置中的 `RolledUp`；菜单、编辑、框选及拖放期间延后收回。
- 浮层中的框选与标签拆分预览跟随来源视图选择绘制宿主，结束或取消后清理预览和输入区域。

## 6. 必须保留的约束与已踩过的坑

### 6.1 层级与固定／临时展开

main 上 #14 已 squash 合入 `b6d0c78`，该行为已经纳入当前重构分支：

1. **只有悬停或拖放临时展开才临时置顶。**
2. **固定展开不置顶。** 从临时展开改成固定展开时立即归还基本桌面宿主。
3. 临时收回动画结束后回到桌面层，其他分区及散放图标不能被连带提升。
4. 浮层应位于普通应用上方、任务栏及其他置顶窗口下方；Win+D 全体临时提升另行协调。

- 不能把窗口 owner / parent 设成 Explorer 的窗口，会引入跨进程输入队列和死锁风险。
- `ExpandedSurface` 的 owner 只指向同进程 `DesktopSurface`，这是允许的同进程关系。
- 具体层级参照在窗口自己的 `WM_WINDOWPOSCHANGING` 里改写；不要直接用具体窗口作为 `SetWindowPos` 的插入参数。
- 不能恢复旧的 `HWND_BOTTOM` 排序方案，不能选隐藏 IME 或本窗口名下窗口作为参照。
- 桌面最前且没有安全参照时，`HWND_TOP` 可能解析到隐藏 IME 窗口；当前用临时 `HWND_TOPMOST`，有普通参照后撤销。
- Win+D 把主宿主提升到置顶层时，浮层的参照不能越过主宿主，否则会选中它后面的系统输入指示器。
- 浮层闲置后隐藏并停止主动显示；系统关闭命令不能单独关闭正在使用的浮层，其生命周期由会话管理。

### 6.2 材质、透明与资源

- 分区材质使用 `SystemBackdropElement` + `DesktopAcrylicController`，`IsInputActive=true`，以保留非激活桌面宿主的材质。
- 旧的毛玻璃枚举当前映射为系统亚克力，尚未完整还原旧版所有外观选项。
- `SystemAcrylicBackdrop.OnTargetDisconnected` 必须在 UI 线程移除控制器目标、释放控制器并显式关闭外部背景链接。
- 不能把链接留给 .NET 终结线程释放，否则曾在 `ContentExternalBackdropLink::Dispose` 关闭 `ContentSite` 时触发原生 fail-fast。
- 相同材质复用已有 backdrop 对象，不要非空替换后重用已关闭链接；该类只供元素使用，不能用于窗口或弹出窗口背景。
- **本次拖动崩溃的日志已落到区域创建路径，不能直接按历史 backdrop 析构问题处理。**
- 保留透明底面的 DWM 配置、原生背景清除和非客户区处理。历史上改动这些造成过黑边及拖动时分区外黑屏。
- 不要为了接输入无条件把窗口区域扩成整屏；空白穿透必须继续成立。

### 6.3 输入、文件与测试保护

- 桌面键盘钩子成对处理按下和松开，Win 快捷键和其他程序输入照常放行。
- 不要在通用 `CancelInteractions` 里无条件收回临时展开；右键按下也会经过它，容易破坏菜单。
- 接管释放时可以回收临时浮层；不能因此取消普通桌面分区的编辑。
- 拖动或框选期间暂缓替换内容控件，避免移除持有捕获或拖放的来源控件。
- 系统入口不按普通文件删除、改名或移动；不能把用户目录等系统图标的解析路径当成普通桌面子项。
- 验证不能修改用户剪贴板、回收站、Shell 撤销历史或真实桌面文件。

## 7. 仍需迁移的主要工作

先修本次崩溃。之后按功能闭环分阶段推进，不要把下面内容都当作小修饰：

| 范围 | 待完成内容 |
| --- | --- |
| 分区管理及设置 | 完整设置窗口、分区新增／删除／映射管理、外观及行为设置入口 |
| 搜索和整理 | 搜索界面与全局快捷键、一键整理、自动整理和启动／归位飞行动画；Core 有部分规则不等于入口已迁移 |
| Shell 交互 | 完整系统菜单、系统入口操作、系统新建后改名接手、图标自身的 Shell 放置目标 |
| 文件细节 | Shift+Delete、快捷方式创建、分区内排序、跨标签悬停放置等 |
| 多屏与布局 | 分区跨屏拖动、不同 DPI 迁移、显示器插拔及组合布局、动态缩放处理 |
| 应用管理 | 完整托盘菜单、开机自启管理、配置备份／恢复入口、右键扩展命令接入、自动更新与更新通知 |
| 实机验收 | 外部 OLE 拖放、Explorer 剪贴板互通、输入法候选窗、动态壁纸、真实 Win+D 及日常系统菜单 |
| 性能与体验 | 用户反馈的内存偏高、拖动延迟／文字与背景同步、视觉细节，按用户要求在功能迁移后集中做 |

基础多屏宿主已经存在，但不代表跨屏交互和动态布局完成。
旧架构与新架构目前不能仅凭原型数据判断整体内存优劣，也不能把属性即时变化等同于屏幕显示跟手。
不为总迁移进度提供没有统一口径的百分比。

## 8. 编译与验证

命令均在 `F:\Workspaces\GitHub\my-desktop` 根目录的 PowerShell 运行。遵循根目录的 `AGENTS.md` 和 `.editorconfig`：中文沟通、tab、UTF-8、LF，无行尾注释。
需要新建其他文档时，仍先询问存放位置；不要因本文在 docs 就默认所有后续文档都放这里。

### 8.1 完整编译和核心回归

```powershell
dotnet build MyDesktop.slnx -t:Rebuild -c Release -p:Platform=x64 --nologo
dotnet tests/MyDesktop.Core.Tests/bin/Release/net10.0/MyDesktop.Core.Tests.dll
```

必须检查每条命令的退出码。不能在编译失败后继续测试旧二进制。
VS Code 后台编译曾与 WPF 构建争用中间文件，出现缺少 `.g.cs` / `.baml`；先重试完整 Rebuild，不结束用户的 IDE。

### 8.2 对发布产物运行隔离 UI 回归

```powershell
dotnet publish src/MyDesktop.WinUI/MyDesktop.WinUI.csproj -c Release -r win-x64 --self-contained false `
	-p:Platform=x64 -p:PublishSingleFile=false -o artifacts/verification/winui/handoff-publish --nologo

& 'tests/MyDesktop.WinUI.Smoke/bin/Release/net10.0-windows/MyDesktop.WinUI.Smoke.exe' `
	'F:/Workspaces/GitHub/my-desktop/artifacts/verification/winui/handoff-publish/MyDesktop.WinUI.exe' `
	'F:/Workspaces/GitHub/my-desktop/artifacts/verification/winui/handoff-publish.log'
```

- 启动器创建独立 Windows Desktop、测试底层窗口、独立配置及文件，不切换用户桌面，不注入输入。
- UI 检查在新入口的 `Verification` 目录内执行；不能从当前桌面直接操控该测试桌面的窗口。
- 核心检查覆盖配置、标签、选择、文件及几何规则；UI 检查覆盖接管、层级、编辑、布局、标签、四向收起、框选、拖放及文件监视。
- 原生剪贴板只在私有窗口站验证；本机创建私有窗口站返回错误 183 时标记“未执行”，命令流程改用内存剪贴板，不能改测共享剪贴板。
- 自动检查的布局入口调用及合成状态检查不能代替真实 PointerMoved、系统拖放循环和可见画面验收。

### 8.3 最新通过的基线及其限度

- 最新测试包生成前，完整 Rebuild 为 0 警告、0 错误，核心 43 项全部通过。
- 即将装包的实际发布目录通过隔离 UI 回归，退出码 0，含恢复进程、命令退出和异常结束恢复。
- 收起回归覆盖四方向、动画取消与反转、菜单和编辑保持展开、标签拆合、浮层框选及预览。
- 另覆盖 10 轮模拟显示桌面／恢复并切标签；没有注入真实 Win+D。
- 最新安装包报告：`F:\Workspaces\GitHub\my-desktop\artifacts\verification\winui\installer-publish.log`。
- 收起阶段报告：`F:\Workspaces\GitHub\my-desktop\artifacts\verification\winui\roll-publish.log`。
- **这些通过记录早于 22:46 的实机拖动崩溃，不能覆盖或否定用户反馈。**

只改本文不需要重新编译；接手者修代码后必须重新运行相应验证。

## 9. 测试包、恢复包和自启差异

当前已交付测试包：

```text
F:\Workspaces\GitHub\my-desktop\publish\测试\MyDesktop-Setup-1.3.0-测试-WinUI3.exe
```

- 来源为 `20aa535`，文件大小 `39861784` 字节，约 38.02 MiB。
- 版本 `1.3.0` 是用户要求的临时产物覆盖，不是旧代码，也没有修改工程正式版本号。
- 签名核验为 Valid，签名者 `CN=MyDesktop`。
- 证书指纹：`6C9E104E1EADB0E1E2C87A7DB52110ACC0ECA3D3`。
- SHA-256：`E4FB1C6D4614F0BF7E3987571AF2AE0259D6A672B23F90E78DBEB015419A8A6A`。

重新打包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Build-Installer.ps1 -Test -WinUI -TestVersion 1.3.0 -Label WinUI3
```

脚本会发布 WinUI、直接验证发布文件、生成旧入口单文件退出助手、编译并签名测试安装包。不能省掉发布产物验证。
WinUI 目前只允许测试包，不能去掉 `-Test`。签名使用现有当前用户证书，不要新建替代证书或向用户索取 PFX 密码。

测试安装沿用同一安装标识与用户配置，程序放在安装目录的 `WinUI-Test` 子目录；安装前用退出助手正常结束现有实例。
测试包不安装新的自启项、不注册新的桌面右键扩展，当前也不接自动更新。

**用户刚询问过“安装时的开机自启选项为何不见了，是否强制开启”。已核对并解释：**

- `WinUITest` 条件同时移除了自启勾选任务和自启注册表写入，绝不是强制选中。
- 以前已启用的正式版自启项会保留，仍指向旧正式入口，不会自动改成 WinUI 入口。
- 安装完成页的“启动 MyDesktop”只是安装后立即运行，与开机自启无关。
- 后续迁移需要恢复原有的可选自启行为，不能强制开启。

本机已核验有效签名的恢复包：

```text
F:\Workspaces\GitHub\my-desktop\publish\测试\恢复正式版\MyDesktop-Setup-1.3.3.exe
```

WinUI 测试后需由用户运行正式安装包覆盖恢复，不能承诺在 WinUI 里点自动更新就能恢复。

## 10. 最近提交与建议接手顺序

| 提交 | 内容 |
| --- | --- |
| `20aa535` | 四向收起、Composition 裁剪动画及悬停临时浮层 |
| `38a8427` | 标签合并、排序、拆分及 backdrop 资源断开修复 |
| `a0516fa` | 分区移动缩放、吸附避让与发布验证 |
| `bd508fa` | 旧版本 WinUI 测试包及安装退出助手 |
| `2a80f07` | 图标拖放与左键框选 |
| `045e410` | 共享选择与基础文件操作 |
| `5477cfe` | 散放改名及外部文件变化同步 |
| `63b1dce` | 散放图标及接管恢复保护 |
| `2227913` | 改名交互及输入后的层级修复 |
| `6554ae8` | WinUI 工程与共享业务核心 |

接手时先 fetch + status，确认当前目标分支；干净且落后时才 ff-only 更新，有未提交改动时先交由用户决定。
默认读取本地文件，包括用户未提交内容。不要通过远程版本文件代替工作区代码。

建议顺序：

1. 阅读项目约定与本文第 2 节，确认异常证据和当前源码；先定位 `CreateRoundRectRgn` 失败的真实输入或资源原因。
2. 建立能覆盖真实失败条件的隔离回归，做最小修复；保留透明、穿透和临时展开层级规则。
3. 完整编译、核心回归、发布产物隔离回归、签名测试包，交给麦格马戈登实机确认不再崩溃。
4. 以自洽阶段继续补齐第 7 节的功能，每阶段明确实现边界和验证结果，不顺手重做无关代码。
5. 功能迁移完成后再做内存及拖动延迟测量，并安排完整实机验收，最后才考虑切换正式发行入口。

原型位于 `F:\Workspaces\GitHub\my-desktop\artifacts\architecture-prototypes`，只作历史实验参考。
该目录及验证产物不保证随 Git 交付；其中 README 的阶段结论可能早于正式工程迁移。当前业务代码以 `src/MyDesktop.WinUI` 和本文基线为准。
