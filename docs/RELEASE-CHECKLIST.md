# Relay Windows x64 发布检查单

本文件是**待执行的发布门禁，不是已经通过的测试报告**。修改源码后必须重新发布、测试最终包；Debug 的成功结果不能代替 Release 包验证。当前目标为 .NET 10 / WPF 原生、x64、自包含文件夹分发，不是单文件、不包含 WebView/WebView2、浏览器前端或安装器。

## 1. 冻结与准备

- [ ] 与其他构建／自动化任务确认源码冻结，停止共享项目的并发 build/publish；发布者不要在他人 Debug 构建时启动发布。
- [ ] 完全退出所有 Relay，包括托盘实例。脚本发现任意 Relay 进程就拒绝发布，不会杀掉用户进程。
- [ ] 构建机为 Windows，安装 .NET 10 SDK、PowerShell 7.6+。发布脚本最低支持 PowerShell 7.2；测试脚本需要 PowerShell 7.6（.NET 10）加载随包 UIAutomation。**用户运行程序无需 SDK、PowerShell、另装 .NET 或 WebView。**
- [ ] 核对版本与原生图标：[项目配置](<../native/Relay.Desktop/Relay.Desktop.csproj>)；核对[用户指南](<Relay-Windows-Guide.md>)、[独立快速说明](<Relay-Quickstart.txt>)、根目录[LICENSE](<../LICENSE>) 与 [NOTICE](<../NOTICE>) 已定稿。脚本缺少任何必需文档就失败，不捏造许可证。
- [ ] 备份用户仓库，保留证据。发布脚本不访问实际 AppData 仓库、不读取／删除 Data、不执行注册表／防火墙任务，也不下载包装安装器。

## 2. 构建与分发模式

从任意工作目录调用脚本均可；根目录由脚本所在目录推导，不依赖当前目录。

~~~powershell
pwsh -NoProfile -File .\scripts\Publish-Relay.ps1
# 明确指定便携模式，结果相同：
pwsh -NoProfile -File .\scripts\Publish-Relay.ps1 -Portable
~~~

[发布脚本](<../scripts/Publish-Relay.ps1>) 使用以下发布参数，但先将输出写入工作区内唯一的版本化 staging，再将核验后的程序文件复制到稳定输出：

~~~powershell
dotnet publish native/Relay.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/windows
~~~

- 默认便携包：程序旁明确放置 portable.txt 标记，首次正常启动将数据写入程序旁 Data。解压到有写权限的位置，不要放到只读介质或受保护目录。分发 ZIP 不含 Data，也没有示例用户仓库。
- 可选桌面模式：在**独立、干净的工作区**运行下面的命令。不生成便携标记，应用默认使用本机 LocalAppData 下的 Relay/Vault。不会迁移、删除或合并其他仓库；实际目录以应用显示为准。

~~~powershell
pwsh -NoProfile -Command '& .\scripts\Publish-Relay.ps1 -Portable:$false'
~~~

- 已有便携输出时，脚本拒绝切换到桌面模式，以免程序突然显示另一份仓库。不提供绕过开关。
- 覆盖目标必须严格等于工作区 dist/windows；检查已有祖先及文件的 reparse point，拒绝路径逸出。**不做递归清理／目录替换／Data 删除**。ZIP 只取新的 staging payload，不从曾运行过的输出目录打包。
- 更新现有程序文件并非原子事务：中途磁盘满、权限变化等失败后应保留 staging、退出程序、排除故障再重新运行。不要分发半更新的文件夹。
- 旧输出中未被新清单覆盖的文件会保留，以免删除未知用户文件。因此只分发本次干净 ZIP；不要手工压缩整个旧输出目录。脚本不自动清理旧 staging／日志，避免误删；空间管理由发布者核对确切路径后另行处理。
- 运行时许可证不一定由 dotnet publish 自动复制。脚本按 runtimeconfig 的 includedFrameworks 精确版本，从 NuGet 缓存读取 Core 的 LICENSE.TXT／THIRD-PARTY-NOTICES.TXT、WindowsDesktop 的 LICENSE（及包内存在的第三方通知），放入 licenses/框架名/版本目录并纳入哈希清单。优先使用 -RuntimeLicenseRoot 参数指定的 NuGet 布局目录，否则使用 NUGET_PACKAGES，再否则用户目录的 .nuget/packages；只读取这些外部输入，绝不向缓存写入。不下载、不推测另一版本许可；缺失时发布失败。
- dotnet 失败时最多三次尝试，只有识别到临时网络／还原故障才重试，间隔 2 秒、5 秒；编译错误或认证／授权失败直接停止，不交互登录或无限重试。

## 3. 核对交付物

- [ ] [完整运行文件夹](<../dist/windows/Relay.exe>)：启动入口及所有同目录／语言子目录依赖必须一起保留，不能只复制 EXE。
- [ ] [ZIP](<../dist/Relay-Windows-x64.zip>)：解压根目录直接包含启动入口、运行时、WPF、[开始使用](<../dist/windows/开始使用.txt>)、[使用说明](<../dist/windows/使用说明.md>)、[LICENSE](<../dist/windows/LICENSE>)、[NOTICE](<../dist/windows/NOTICE>)。
- [ ] [SHA256 文件](<../dist/Relay-Windows-x64.zip.sha256>) 对应本次 ZIP；[外部清单](<../dist/Relay-Windows-x64.manifest.json>) 与[包内清单](<../dist/windows/release-manifest.json>) 一致。清单列出每个相对文件名、SHA256、字节数及发布配置；不包含真实数据，清单自身不递归计算自身哈希。
- [ ] ZIP 没有 Data、用户仓库／备份、测试文件、.git、日志／转储／截图、HTML/JS/CSS、本地浏览器存储或 WebView2 DLL。运行时诊断可执行文件 createdump 也不分发。
- [ ] 本项目当前为**未签名分发**；清单和报告记录实际 Authenticode 状态。未签名不等于文件一定危险，也不保证 SmartScreen 放行。SHA256 仅证明与所给校验值一致，不能独立证明发布者身份。不要提示用户关闭安全防护、绕过组织策略或保证“无警告”。后续若签名，须在生成清单／ZIP 前完成，并重新测试。

~~~powershell
Get-FileHash .\dist\Relay-Windows-x64.zip -Algorithm SHA256
~~~

## 4. 最终包自动验证

~~~powershell
pwsh -NoProfile -File .\scripts\Test-Release.ps1
# 没有交互式 Windows 桌面时只能做静态核验；不能称为启动测试通过：
pwsh -NoProfile -File .\scripts\Test-Release.ps1 -SkipLaunch
~~~

[验证脚本](<../scripts/Test-Release.ps1>) 执行以下检查；失败退出码为 1，成功为 0。显式跳过启动时报告状态是 static-only，**不是 passed**。

- [ ] 验证 ZIP SHA256、条目路径／重复项／解压上限、清单完整性；核对最终 ZIP 和稳定输出中的每个清单文件大小／SHA256。
- [ ] 检查运行时配置 includedFrameworks 含 .NET 10 与 WindowsDesktop，而非需要外部框架的 framework/frameworks；检查 hostpolicy、hostfxr、coreclr 及 WPF 依赖存在。
- [ ] 用二进制流读取真正的 MZ／PE 签名、AMD64 与 PE32+ 头，不仅凭 EXE 后缀断言原生程序；验证 EXE 图标可提取。图标视觉质量仍需人工检查。
- [ ] 从本次 ZIP 解压得到的副本启动，使用 ProcessStartInfo.ArgumentList 传入唯一的 --vault，路径位于工作区 artifacts/release-test 下；不接触个人仓库，也不截图桌面。
- [ ] 只在自有 PID 的窗口树里等待可见、可用的 main.capture UIAutomation 控件；不以“进程存活”代替界面就绪。
- [ ] 只对测试子进程设置精简 PATH、空的 DOTNET_ROOT 路径及禁用多级查找；确认实际加载的是包内 coreclr。不会改主机环境或卸载运行时。
- [ ] 检查明确的测试仓库已创建，包旁 Data 与重定向的默认 AppData 仓库未生成。不访问真正用户的 AppData，因此不声称已检查其内容。
- [ ] finally 中只终止本脚本创建的进程，不按名称批量杀进程，不终止别人的程序，不删除测试证据。
- [ ] 查看本次运行目录中的 JSON 报告：状态、ZIP 哈希、各项证据、签名状态、PID、启动时间、工作集、隔离限制和失败原因。报告与合成仓库留在 artifacts/release-test；publish 日志位于 artifacts/release。

**证据边界：** 包内运行时和隔离启动是“不依赖已安装 .NET”的强证据，但不等于在无 SDK／运行时的干净虚拟机验证。若没测干净机器，交付说明必须保留这一限制；不能把 Debug 自动化或一次冒烟等同于完整兼容性认证。

## 5. 人工与回归门禁

- [ ] 单元／持久化／UI 自动化针对最终源码通过；记录具体命令、退出码、报告和时间。不把旧报告算到新包上。
- [ ] 在最终 Release 包用独立测试仓库核对：新建与重启保存、资源引用／失效提示、续做条、进展记录、计时恢复、导出／恢复及错误提示。禁止用真实用户数据做破坏性测试。
- [ ] 核对默认便携包首次启动为空仓库；关闭再启动可保留用户主动保存的数据。升级时只替换程序文件，保留完整 Data；移机前完全退出并备份整个仓库与引用资源。
- [ ] 卸载前先导出／移出 Data。删除整个便携程序目录会一并删除其中数据；桌面模式 AppData 不应随程序目录删除。
- [ ] 在实际可用的 Windows x64 干净机器验证：解压后直接启动，无 SDK／另装 .NET／WebView；记录操作系统版本、DPI、启动结果。未执行就明确标注未验证。
- [ ] 全局热键、托盘、用户主动启用的 Shell 集成按实际环境验证；权限或策略拒绝如实报告，不做注册表／防火墙提权修复。打包和冒烟脚本不执行此类系统改动。
- [ ] 文档和最终答复明确：Windows 原生版本、完整目录运行、默认便携仓库、明文数据、未签名与 SmartScreen 可能提示、没有 Android／自动跨设备同步、没有另行下载的安装器。

最终发布记录应给出：版本、ZIP SHA256、实际测试报告路径、通过项、未验证项和已知限制。没有真实运行证据的复选框保持未勾选。
