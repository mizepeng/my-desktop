# MyDesktop 桌面分区
Windows 11 桌面分区整理工具，对标 Fences / 小智桌面：用半透明的分区把桌面图标分门别类地收纳起来。基于 .NET 10 + WPF，无第三方依赖。
## 一、功能
- **分区**：半透明圆角窗口，始终位于应用窗口之下；Win+D 显示桌面时依然可见。可拖动、缩放、吸附对齐（间距可调，调整大小时按图标整行整列吸附并显示列数 × 行数），双击标题栏卷起、悬停展开，可锁定
- **两种分区**：托管分区（拖入即把文件移动到该分区对应的文件夹，桌面真正变整洁）；文件夹映射分区（直接展示任意已有文件夹，如下载目录）
- **分区内操作**：系统图标与缩略图、双击打开、系统原生右键菜单、F2 重命名、Delete 删除到回收站、Ctrl+C/X/V、框选多选、拖动图标自定义排序；可与资源管理器、桌面、其他分区互相拖放
- **外观**：每个分区可单独设置颜色、不透明度、图标大小、列表视图和排序方式，默认外观在设置里统一调整
- **整理**：一键按规则把桌面文件归入对应分区；可选自动整理新出现的文件（等下载、解压完成、内容稳定后才移动）；规则可编辑
- **桌面交互**：双击桌面空白处渐隐/渐显所有图标和分区；在桌面空白处按住右键拖出一个框即可新建分区；桌面右键菜单里有 MyDesktop 子菜单
- **其他**：托盘菜单、开机自启、Win11 风格设置窗口、分辨率或显示器变化后自动把分区拉回屏幕内
## 二、使用
- 首次启动会询问是否一键整理桌面，整理前会列出将移动的文件数量供确认
- 拖入分区时默认移动，按住 Ctrl 为复制，按住 Alt 为创建快捷方式；跨磁盘拖动默认复制（与资源管理器一致）
- 右键分区空白处或点标题栏的「⋯」打开分区菜单；右键托盘图标打开全局菜单
- 桌面空白处的右键菜单里有 MyDesktop 子菜单：装好已签名的扩展包后，Windows 11 新式菜单和「显示更多选项」里都有；没有扩展包时只出现在「显示更多选项」里（见 §三）
- 不再使用前，在 设置 → 常规 → 「全部移回桌面…」可把所有托管分区里的文件还原到桌面
## 三、构建与发布
构建需要 .NET 10 SDK，运行需要 .NET 10 桌面运行时。
```bash
dotnet build src/MyDesktop/MyDesktop.csproj
```
发布为单文件 exe（约 0.5 MB，依赖本机的 .NET 10 桌面运行时）：
```bash
dotnet publish src/MyDesktop/MyDesktop.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
开发测试时可以用 `--data <目录>` 启动一个使用独立配置的实例，与正式实例互不干扰：
```bash
MyDesktop.exe --data D:\temp\mydesktop-test
```
桌面右键菜单扩展包：编译时会在 exe 旁生成并签名 `MyDesktop.DesktopMenu.msix`（发布时一并复制），需要当前用户证书存储里有主题为 `CN=MyDesktop` 的代码签名证书，没有时跳过并给出提示。首次在一台电脑上使用：
1. 创建签名证书（只需一次，私钥留在本机，不需要管理员权限）：
```powershell
New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MyDesktop' -CertStoreLocation Cert:\CurrentUser\My -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
```
2. 以管理员身份信任这张证书（每台要使用扩展包的电脑一次；别的电脑把导出的 `MyDesktop.cer` 拷过去，只执行最后一行）：
```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=MyDesktop' | Select-Object -First 1
Export-Certificate -Cert $cert -FilePath MyDesktop.cer
Import-Certificate -FilePath MyDesktop.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```
3. 重新编译后启动 MyDesktop，程序会自动注册扩展包（不需要开发者模式）
## 四、数据位置
- 配置与日志：`%APPDATA%\MyDesktop`（`settings.json`、`app.log`）
- 托管分区的文件：默认在桌面文件夹同级的 `桌面分区` 目录（与桌面同盘，移动是瞬时的），可在设置中修改
- 开机自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `MyDesktop`
- 桌面右键菜单：扩展包 `MyDesktop.DesktopMenu`（按用户注册，注册记录在 `%LOCALAPPDATA%\MyDesktop\desktop-menu-package.txt`），退回方式写在 `HKCU\Software\Classes\DesktopBackground\Shell\MyDesktop`；在设置中取消勾选即注销/删除，扩展包被手动卸载后取消再勾选一次即可重新注册
## 五、项目结构
- `src/MyDesktop/Native`：Win32 / Shell 互操作（系统右键菜单、图标加载、SHFileOperation 文件操作、原生菜单）
- `src/MyDesktop/Core`：桌面层级维护、鼠标钩子、托盘图标、开机自启、桌面右键菜单（扩展包注册与菜单服务进程）、日志
- `src/MyDesktop/Services`：分区管理（`FenceManager`）、桌面整理（`DesktopOrganizer`）、配置读写
- `src/MyDesktop/Views`：分区窗口、设置窗口、对话框
- `src/MyDesktop/Models`：配置模型与外观预设
- `src/MyDesktop/ShellExtension`：桌面右键菜单扩展包的清单与打包签名脚本
## 六、实现要点
- **不与 Explorer 建立跨进程窗口关系**：把分区窗口的所有者设为桌面窗口能让它天然随桌面显示，但跨进程的所有者/父子关系会让两个进程共享输入队列，右键菜单等场景下会互相等待而死锁。分区改为普通工具窗口，通过监听前台切换与顶层窗口层级变化，始终维持在桌面窗口正上方
- **拖动吸附按鼠标绝对位移计算**：系统在拖动时按「上一次位置 + 鼠标增量」推算新位置，直接改写会吃掉位移导致窗口被粘住
- **文件操作统一走 SHFileOperation**：带系统进度框、重名确认，可在资源管理器中 Ctrl+Z 撤销，删除进回收站；执行前校验目标文件夹存在
- **图标位图逐张判断是否预乘 alpha**：Shell 返回的多为非预乘格式，按预乘解读会让半透明边缘发白
- **桌面右键菜单走扩展包**：Windows 11 新式菜单只显示带包身份的 IExplorerCommand 扩展，部分预览版的 Explorer 还会忽略当前用户注册表里的静态菜单。扩展包只含清单，以「外部位置」指向 exe 目录；菜单由系统按需以包身份启动的 `MyDesktop.exe --shell-extension` 进程提供（进程外 COM，不往 Explorer 里加载代码），它只在桌面空白处显示菜单，点击后把命令转给正在运行的 MyDesktop，主程序退出后随之退出。未签名的包不允许注册程序入口，所以扩展包必须签名
## 七、已知限制
- 桌面右键菜单扩展包用自签名证书签名，每台电脑要以管理员身份信任一次证书（见 §三）；面向他人分发时需要正规的代码签名证书
- 桌面图标属于 Explorer，在不注入 Explorer 的前提下无法做渐隐，隐藏/显示时只有分区带渐变效果
- 开启「右键拖动画框新建分区」后，从桌面图标上开始的右键拖动不再触发 Explorer 的右键拖放菜单
- 分区中的快捷方式不显示箭头角标
