# AGENTS.md

本文件为在此仓库中工作的 AI 编程助手提供项目约定和开发指导。只放每次都要遵守的内容，细节按需阅读下面的文档。

## 项目概况
MyDesktop：Windows 11 桌面分区整理工具（对标 Fences），.NET 10 + WPF，单个项目 `src/MyDesktop`，没有第三方依赖。没有测试项目和 lint 配置，验证靠编译加实机运行。README 面向最终用户，开发说明写在脚本开头：`installer/MyDesktop.iss`、`src/MyDesktop/ShellExtension/Pack-DesktopMenu.ps1`。
## 按需阅读的文档
| 文档 | 什么时候读 |
| --- | --- |
| [docs/dev/architecture.md](docs/dev/architecture.md) | 改接管、窗口层级、分区、标签页、背景效果、整理动画、搜索、右键菜单、安装与自动更新等模块前 |
| [docs/dev/constraints.md](docs/dev/constraints.md) | 改窗口层级、透明窗口、钩子、Shell 图标、亚克力与毛玻璃、动画、内存相关代码前（必读，都是实测结论） |
| [docs/dev/setup-and-release.md](docs/dev/setup-and-release.md) | 换电脑配环境、签名证书、打测试包或发行版安装包时 |
## 常用命令
```bash
dotnet build src/MyDesktop/MyDesktop.csproj
powershell -ExecutionPolicy Bypass -File installer/Build-Installer.ps1
```
- 编译偶尔报找不到 `obj\...\*.g.cs` 或 `App.baml`：VS Code 的 C# 扩展在后台同时编译、抢了中间文件，重试即可。
- 独立测试实例：`MyDesktop.exe --data <目录>`，配置和日志都放在该目录。测试实例同样会接管桌面图标，会和正在运行的正式实例冲突，测试前先退出正式实例；不要在真实桌面上跑一键整理。
- 正式实例的配置与日志：`%APPDATA%\MyDesktop\settings.json`、`app.log`。
## 不能违反的规则
- 导入 PFX、信任证书由开发者本人在自己的终端里执行，PFX 密码不要交给 agent；不要另建新证书，自动更新只认 `installer/MyDesktop.cer` 这一张。
- 安装包一律用 `Build-Installer.ps1` 生成并签名，漏签会让所有用户自动更新失败；版本号只改 csproj 的 `<Version>`。
- 分区窗口绝不能把 owner 或 parent 设成资源管理器的窗口：跨进程窗口关系会共享输入队列，两个进程互相等待而死锁。
## 代码约定
- 遵循 `.editorconfig`：tab 缩进；含中文的 `.ps1` 必须是 UTF-8 带 BOM（Windows PowerShell 5.1 按系统代码页读取无 BOM 的脚本）。
- 文本文件一律 LF 换行，由 `.gitattributes`（`* text=auto eol=lf`）统一，不依赖各电脑的 `core.autocrlf`：扩展包清单原样打进 msix，换行符不一致会让不同电脑打出内容不同的同版本扩展包。
- csproj 把 CS8509（switch 表达式没有覆盖全部枚举值）设为错误：对枚举优先用 switch 表达式列全所有值，不写 default。
- 注释、日志和界面文字都用中文。
