# 续页 Relay · 原生 Windows 验证记录

本报告对应原生 WPF 版本，不是旧网页留白测试。工作时段：2026-09-30 15:02–17:02 +08:00。以下是实际完成结果，保留跳过项和历史失败边界。

## 当前验证

| 范围 | 结果 | 方法与边界 |
|---|---|---|
| 磁盘仓库 | 21/21 | 原子替换、备份恢复、冲突、锁定失败、1GiB 快速拒绝 |
| 专注磁盘状态 | 40/40 | 暂停/重启、持久完成意图、幂等结算、清空墓碑 |
| 安静提醒 | 29/29 | 22–08点、偏移、到期、持久确认、备份回退边界 |
| 资源与导出 | 82通过/11跳过/0失败 | 实际目录联接、硬链接、锁文件；部分符号链接与8.3叶名称不可用 |
| 打开资源决策 | 47通过/3跳过/0失败 | 50场景与72扩展名变体；不启动关联应用，不是安全沙箱 |
| 旧版迁移 | 18/18 | 20MiB、Unicode、完整校验、长正文无损分片 |
| 原生进程耐久 | 30/30 | 确认保存后强制终止再启动；不等价于断电或OS重启 |
| 原生 UIA | 最终Release 19/19通过（16:27） | 对dist/windows/Relay.exe实际执行，真实快捷键与工作旁续做流程均通过 |
| 原生模态草稿 | 6/6 通过 | 取消退出保留草稿、回调失败保留、陈旧编辑冲突、模态全局继续不改数据 |
| 可访问性定向 | 最后10/10通过 | 真实WPF焦点/中文Name/URL取消Enter；一次未定位间歇搜索焦点失败保留，HC运行时未测 |
| 便携实际流程 | 最终包7/7通过（16:23） | 无参数便携保存、重启、Unicode仓库、SendTo参数、非法CLI，自包含CoreCLR |
| 发布完整性 | 最终包通过（16:23） | 文件SHA256、PE x64、精确运行时许可、限制PATH/DOTNET_ROOT启动、Data不覆盖 |
| 常驻空闲 | 12分钟完成 | CPU3.703秒，8核心归一平均0.0643%；私有内存末期66.4MiB，实际工作集247.3MiB |
| 持续故障耐久 | 84分9秒通过 | 565轮、5650次随机操作、6407次保存重载比对；7类故障每类48次。最初子任务中断未算通过 |

## 环境限制与证据

- 热键注册、占用、回滚、WM_HOTKEY窗口分发已验证。合成SendInput早期被拒绝或不投递；最终16:27针对Release程序复测19/19通过，实际CtrlEnter与CtrlAltSpace均成功。历史失败与独立对照记录保留，不代表所有桌面环境都可靠。
- HKCU Explorer菜单和真实SendTo写入受宿主权限拒绝；工作区临时COM快捷方式创建/读取/更新/撤销通过。未提权或修改安全策略。入口是显式可选功能，不声称已安装。
- 早期PrintWindow图片客户区空白，不能当视觉证据。后续明确诊断参数通过本应用WPF RenderTargetBitmap输出真实视觉树，不读取其他应用或桌面。每张记录来源和像素统计；模型不支持图像输入，不能声称人工逐像素审图。
- 部分符号链接权限不可用；实际目录联接和硬链接测试已运行。没有连接网络共享。

## 最终发布与原始证据

最终ZIP SHA256：`9717dac0efbbd2a0b5e5ff5c97e979c71b338b9dffd915d6a3a3f3e702c3ad9f`。包内构建版本 `2.0.0+5583d9c`；后续文档提交不改变该二进制。

- [最终发布验证](<../artifacts/release-test/20260930-162239-4acde83df2fa44ffb63e8b3387c44ee3/report.json>)
- [最终便携CLI流程](<../artifacts/package-tests/4268ef6fe92044989858276d8eabeb29/report.json>)
- [最终Release 19项原生UIA](<../artifacts/native-tests/20260930-082629-cb49ee26/report.json>)
- [整套核心回归](<../artifacts/core-regression/20260930-155320-019d428a0afa49718b81e1abbe052dbb/report.json>)
- [更新保留Data](<../artifacts/portable-update/a50f98e605704fe6b76ac1efe84e00f8/report.json>)
- [常驻实测](<Relay-Idle-Footprint.md>) · [性能修复](<Relay-Performance.md>)
- [84分钟耐久最终报告](<../artifacts/soak/20260930T073739728Z-1f4b84946d904201b390d65374ed9fd7/summary.json>)：运行启动时的存储快照，后续仅性能分配优化另做回归；不等价断电测试。

## 测试源码

[仓库](<../native/Relay.Tests/Program.cs>) · [专注](<../native/Relay.FocusTests/Program.cs>) · [提醒](<../native/Relay.PolicyTests/Program.cs>) · [资源导出](<../native/Relay.SafetyTests/Program.cs>) · [打开决策](<../native/Relay.LauncherTests/Program.cs>) · [迁移](<../native/Relay.MigrationTests/Program.cs>) · [原生UIA](<../native/Relay.Automation/Program.cs>) · [耐久](<../native/Relay.Soak/Program.cs>)

## 诚实边界

现有工具已有任务、引用、热键、离线与会话恢复。续页尝试更窄的“留下具体下一步→工作旁继续”，没有多人竞品对照，不宣称独创或效率提高25%。

无Android、云同步、被动屏幕/剪贴板采集、未保存编辑恢复。明文数据不加密。以可信本机同一用户环境为前提，不防相同权限恶意进程抢换路径。手工删除专注完成标记再重放待结算状态不在幂等保证内；恢复早于提醒确认的旧备份可能再次提醒。
