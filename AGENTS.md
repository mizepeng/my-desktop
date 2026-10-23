# AGENTS.md

本文件为在此仓库中工作的 AI 编程助手提供项目约定和开发指导。只放每次都要遵守的内容，细节按需阅读下面的文档。

## 项目概况
MyDesktop：Windows 10 / 11 桌面分区整理工具（对标 Fences），.NET 10，正在迁移到 WinUI 3 + Composition。
- `src/MyDesktop.Core`：不依赖界面框架的模型、配置存储、备份、使用记录、拼音搜索、标签组、桌面快照和散放布局规则；保留现有配置命名空间及 JSON 格式。
- `src/MyDesktop.WinUI`：新界面入口，使用 Windows App SDK 2.5.1；每屏一个桌面宿主，分区和标签均为内部视图，收起的分区常驻同屏浮层。
- `src/MyDesktop`：迁移期间保留的 WPF 入口，引用同一个核心工程；安装包仍使用此入口，尚未切换发行入口。
- `tests/MyDesktop.Core.Tests`：无第三方依赖的控制台回归检查，直接运行返回通过或失败退出码。
- `tests/MyDesktop.WinUI.Smoke`：不切换过去的独立桌面验证启动器，生成独立配置及测试文件，不在真实桌面注入鼠标键盘。
README 面向最终用户；构建与迁移说明维护在本文件、`docs/dev` 及现有脚本中，不把原型产物当成已经完成的正式功能。
## 按需阅读的文档
| 文档 | 什么时候读 |
| --- | --- |
| [docs/dev/winui-migration.md](docs/dev/winui-migration.md) | 改 WinUI 入口的代码前（已迁移功能、实现要点、WinUI 实测约束、尚未迁移的部分） |
| [docs/dev/architecture.md](docs/dev/architecture.md) | 改 WPF 入口的接管、窗口层级、分区、标签页、背景效果、整理动画、搜索、右键菜单、安装与自动更新等模块前；迁移对应功能时作为行为参照 |
| [docs/dev/constraints.md](docs/dev/constraints.md) | 改窗口层级、透明窗口、钩子、Shell 图标、亚克力与毛玻璃、动画、内存相关代码前（必读，都是实测结论） |
| [docs/dev/setup-and-release.md](docs/dev/setup-and-release.md) | 换电脑配环境、签名证书、打测试包（含 WinUI 测试包）或发行版安装包时 |
| [docs/TODO.md](docs/TODO.md) | 开始或结束一项工作时。由 AI 助手维护：新增待办、完成打勾、做不了或撤回时取消勾选，完成很久的可以删除 |
## 常用命令
```bash
dotnet build src/MyDesktop/MyDesktop.csproj
dotnet build MyDesktop.slnx -t:Rebuild -p:Platform=x64
dotnet run --project tests/MyDesktop.Core.Tests/MyDesktop.Core.Tests.csproj
powershell -ExecutionPolicy Bypass -File installer/Build-Installer.ps1
```
- 修改代码后使用 `-t:Rebuild` 完整重建，不能只依赖增量编译结果。WinUI 单独构建需加 `-p:Platform=x64`。
- 编译偶尔报找不到 `obj\...\*.g.cs` 或 `App.baml`：VS Code 的 C# 扩展在后台同时编译、抢了中间文件，重试即可。
- WinUI 默认数据目录和单实例互斥体与 WPF 相同，同一份配置不能由两个入口同时运行。使用 `--data <独立目录>` 做可保存的独立实例验证，但仍会接管同一桌面，实机验证前须退出其他接管实例；`--preview <settings.json>` 只读预览指定配置，不回写配置，也不接管系统图标。
- 隔离 UI 验证：完整构建后，运行 `MyDesktop.WinUI.Smoke.exe <MyDesktop.WinUI.exe绝对路径> <报告log绝对路径>`。启动器只创建及操作它自己的测试桌面和测试进程，不切换当前桌面；测试文件与独立配置保存在报告目录。
- `MyDesktop.WinUI.Smoke.exe --read-desktop` 只读一次真实 Explorer 快照，不隐藏图标、改位置或打开界面。隔离 UI 验证的快照来自测试文件，不连接真实 Explorer 的 COM 服务；恢复检查在同一测试桌面的辅助进程中运行，不能从当前桌面直接访问测试窗口。
- 独立测试实例：`MyDesktop.exe --data <目录>`，配置和日志都放在该目录。测试实例同样会接管桌面图标，会和正在运行的正式实例冲突，测试前先退出正式实例；不要在真实桌面上跑一键整理。
- 正式实例的配置与日志：`%APPDATA%\MyDesktop\settings.json`、`app.log`。
## 不能违反的规则
- 导入 PFX、信任证书由开发者本人在自己的终端里执行，PFX 密码不要交给 agent；不要另建新证书，自动更新只认 `installer/MyDesktop.cer` 这一张。
- 安装包一律用 `Build-Installer.ps1` 生成并签名，漏签会让所有用户自动更新失败；版本号只改 csproj 的 `<Version>`。
- 分区窗口绝不能把 owner 或 parent 设成资源管理器的窗口：跨进程窗口关系会共享输入队列，两个进程互相等待而死锁。
- 开发者在用这台电脑，不要操控真实桌面：不在真实桌面启动第二个接管实例、注入鼠标键盘、切换桌面或结束资源管理器；界面验证用隔离 UI 验证，实际效果交给开发者实机确认。
## 代码约定
- 遵循 `.editorconfig`：tab 缩进；含中文的 `.ps1` 必须是 UTF-8 带 BOM（Windows PowerShell 5.1 按系统代码页读取无 BOM 的脚本）。
- 文本文件一律 LF 换行，由 `.gitattributes`（`* text=auto eol=lf`）统一，不依赖各电脑的 `core.autocrlf`：扩展包清单原样打进 msix，换行符不一致会让不同电脑打出内容不同的同版本扩展包。
- csproj 把 CS8509（switch 表达式没有覆盖全部枚举值）设为错误：对枚举优先用 switch 表达式列全所有值，不写 default。
- 注释、日志和界面文字都用中文。
