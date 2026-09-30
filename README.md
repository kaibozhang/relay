# 续页 Relay · Windows

**被打断之后，不必重新找。**

这次不再做一个“笔记＋任务＋番茄钟”的网页。续页是一个原生 Windows 小工具，只管一件事：把“我做到哪儿了，回来先做什么，资源在哪”留在一张续做卡上。

## 直接运行

打开 [Relay.exe](<dist/windows/Relay.exe>)。发布包是完整的自包含 Windows x64 文件夹，不需要浏览器、Node、.NET SDK、WebView2、账号或网络。

便携包里的标记启用 **Data 随程序目录保存**；更新程序时保留 Data。没有便携标记时默认保存到当前用户本地应用数据目录。不是浏览器缓存。

> 发布与两小时耐久验证仍在进行，最终结果见 [原生验证报告](<docs/Relay-Verification.md>)。

## 为什么还要留一个新工具

调研确认 [Task Coach](https://sourceforge.net/projects/taskcoach/)、[QOwnNotes](https://www.qownnotes.org/)、[CherryTree](https://www.giuspen.net/cherrytree/)、[Ditto](https://sourceforge.net/projects/ditto-cp/) 等已经有大量重合能力。**本地存储、原生窗口、快捷键都不是独创。**

这里值得尝试的是更窄的一条路径：

1. 在其他软件里按 **Ctrl+Alt+Space**，只弹一个小捕捉窗口，不把管理页面抢到前面。
2. 写一句具体的下一步，**Ctrl+Enter**，确认写入磁盘后回去工作。
3. 回来时，叫回续页，把下一步放在工作旁的悬浮条上；文件、文件夹和网址按需明确选择打开。
4. 做完一小步，用同一个窗口记录进展、改好下一步；没有打卡、积分和催促。

如果你原来的工具已让这个流程同样顺手，就不需要多维护一份。没有未经测量的效率优势承诺。完整取舍见 [开源对照与定位](<docs/research/windows-positioning.md>)。

## 做好的基础

- **纯 WPF 原生界面**：不是浏览器外壳，没有 Chromium 运行时。
- **真实磁盘仓库**：原子写入、上一版备份、损坏保留、只读恢复、冲突检测、实际文件锁。
- **顺手入口**：全局快捷键可配置并显示冲突、托盘、任务栏缩略按钮与跳转列表；右键/发送到/开始菜单入口由你显式启用和撤销。
- **继续工作**：一句下一步、上下文、资源引用、检查点、可收起的置顶续做条。
- **不打扰**：选择“稍后回来”，到期合并提醒；22:00–08:00 安静，不自动打开资源，不偷偷安装自启服务。
- **可带走**：卡片 Markdown、完整卡片 JSON、旧版留白导出文件的显式迁移；不读取浏览器资料。
- **专注不丢进度**：计时、暂停、完成待结算状态落盘，重启后恢复。

## 边界先说明

- 不恢复其他软件未保存的内容、光标或窗口布局。资源只是路径，不复制原文件。
- 不记录屏幕、键盘、窗口历史或后台剪贴板。不扫描文件。
- 明文数据未加密，无云同步；Android 按要求暂缓。
- 右键和快捷方式实际安装受系统权限影响。本次测试环境的相关系统写入被拒绝，程序会报告，不假称已安装；不会为此提权或关闭安全保护。
- 未签名。SmartScreen/组织策略是否允许，需由你根据来源与政策判断。

## 使用与证据

- [中文快速指南](<docs/Relay-Windows-Guide.md>)
- [原生验证报告](<docs/Relay-Verification.md>)
- [发布检查清单](<docs/RELEASE-CHECKLIST.md>)
- [纯原生源码](<native/Relay.Desktop/Relay.Desktop.csproj>)
- [磁盘核心](<native/Relay.Core/VaultStore.cs>)
- [真实 Windows 自动化](<native/Relay.Automation/README.md>)

## 开发

只在开发/测试时需要 .NET 10 SDK；应用本身无 NuGet 第三方包。


```powershell
dotnet build native/Relay.Desktop/Relay.Desktop.csproj -c Release
dotnet run --project native/Relay.Tests -c Release
dotnet run --project native/Relay.FocusTests -c Release
dotnet run --project native/Relay.PolicyTests -c Release
dotnet run --project native/Relay.SafetyTests -c Release
dotnet run --project native/Relay.LauncherTests -c Release
dotnet run --project native/Relay.MigrationTests -c Release
```

发布脚本：[Publish-Relay.ps1](<scripts/Publish-Relay.ps1>)。保留数据、不打包测试资料，生成 ZIP、SHA256 和逐文件清单。许可见 [LICENSE](<LICENSE>) 与 [NOTICE](<NOTICE>)。

---

旧网页版 [index.html](<index.html>) 仅作为第一版历史原型保留，**不是当前推荐入口**。它的数据仍在浏览器，需主动导出旧 JSON 后在续页设置里迁移。不会悄悄搬走你的数据。
