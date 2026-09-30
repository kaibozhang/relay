# 已交付价值复核：续页 Windows

## 结论：窄场景试用 GO；“用了两小时就值得留下”尚未证明

保留的理由只有一个：**主动留下一步与资源引用，回来把动作放在工作旁，再更新下一步。** 这不是笔记库，也不是自动恢复工作现场。
如果现有工具的一张模板／置顶笔记已足够顺手，就不要再维护一份。18 项 native + 30 项 restart 通过是父任务提供的测试状态，不是竞品效率、留存或用户价值证据；焦点补丁测试仍在进行。本复核只读代码，不启动应用、不运行测试，只新增本文。

相比“继续扩功能”“立刻放弃”，建议选择第三条：**冻结范围，只修把人拉回管理页的一个断点，再验证真实中断场景。** 17:02 前不加入 Android、生态集成或新恢复机制。

## 实际交付了什么

- **捕捉无需先开管理页：** 全局入口调用 background capture；仅下一步必填，焦点直接进入该字段，Ctrl+Enter 保存；空标题由下一步生成。[入口](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L64-L69>) · [捕捉调用](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L191-L198>) · [必填与保存](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L151-L198>)。
- **资源是主动加入的引用：** 文件／文件夹选择、拖入、网址、按钮触发粘贴；不是后台收集。粘贴普通文字进入上下文；不会复制引用文件。[资源入口](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L202-L241>) · [粘贴边界](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L300-L327>)。
- **进展会更新当前下一步：** 同一保存调用写进展和 NextAction，成功后更新续做条；不是只积累日志。[调用](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L221-L225>)。
- **续做条确实能留在工作旁：** 小窗、可拖动、可收起，置顶可关闭；不是必须一直停在主窗。但不能据此断言已消除焦点干扰。[条](<D:/DPH/native/Relay.Desktop/ResumeStrip.cs#L26-L95>) · [置顶设置](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L345-L346>)。
- **帮助没有伪造优势：** 明说没有测出的效率优势、现有方法顺手就不必迁移；说明不恢复未保存编辑／光标／窗口布局。[帮助](<D:/DPH/native/Relay.Desktop/HelpWindow.cs#L56-L91>)。

## 严重摩擦：只列三项（静态审阅时的行为）

| 优先级 | 实际断点 | 本轮判断与最小处理 |
| --- | --- | --- |
| 1 | 续做条“留个进展”“完成”都调用 ShowMain；全局 ResumeLatest 也先唤起主窗。工作旁的小窗又把人拉回管理页。[证据](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L211-L225>) | **唯一建议立即修的行为项。** 先让 strip 进展／完成保持主窗原可见性；进展以可见 strip 为 owner，失败提示也必须归属可见窗口，按 card.Id 更新 strip。不能只删 ShowMain；主窗入口继续保留原逻辑。实现估计 5–10 分钟，不含验证；若不能连同焦点／失败路径验证就暂缓。全局恢复同类问题可后置，勿仓促扩大补丁。 |
| 2 | “继续”仅改状态并显示条，不打开资源；批量资源入口在主窗，要另开选择窗，每次默认全不选。[继续](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L206-L214>) · [主窗资源入口](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L154-L160>) · [选择窗](<D:/DPH/native/Relay.Desktop/ResourceResumeWindow.cs#L43-L67>) | 因而**没有交付“一次恢复动作带回资源”的完整承诺**。当前保留显式选择和验证，不用自动全选／自动打开换少一步。若有余量，仅将“一起找回现场”改成“选择并打开资源”（约 1 分钟），避免暗示状态恢复；不临时再加一套资源流程。 |
| 3 | 捕捉只需一个动作，但“留个进展”要求同时填进展和下一步；仅想改下一步也要写一段回顾，焦点先落进展。[表单](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L49-L79>) · [校验](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L90-L103>) | 这是小日志负担，不是必要的恢复条件。后续可让进展说明可选、只更新下一步时不追加空日志（实现估计 5–10 分钟，需同步调用方并验证）。**本轮不与焦点补丁叠加；不新增第三个表单。** |

以上估时是代码范围判断，不是承诺；不要把三项全部塞进最后十分钟。报告不把尚未验证的修复当作已交付。

## 为什么不是现有开源工具的换皮宣传

沿用[已有研究与证据边界](<D:/DPH/docs/research/windows-positioning.md#L24-L55>)：Task Coach 已有描述／提醒／文件与 URL 附件（[官方功能](https://taskcoach.org/features.html)）；QOwnNotes 已有便携 Windows、Markdown 与浮动面板（[官方概览](https://www.qownnotes.org/getting-started/overview.html)）；CherryTree 已有文件／文件夹／网页链接（[官方功能](https://www.giuspen.net/cherrytree/)）；Ditto 已有全局热键与持久剪贴板历史（[SourceForge](https://sourceforge.net/projects/ditto-cp/)）。这些是此前取回的能力文档，不是实操测量。

独立保留的候选价值是**这几个动作被收束到专门的小流程，不要求进入笔记宿主或维护分类**，而非热键、便携、本地存储、引用本身新颖。不能断言竞品没有同样流程，也不能声称更快多少。

## 删减取舍

- 保留捕捉、下一步、显式资源、续做条、编辑／完成、备份导出及可退出性；管理页退为查找旧卡与设置的辅助入口。
- 专注计时、回来次数、长进展历史不是当前差异化依据：不再加码，不用“功能很多”证明两小时值得。计时会自动增加“完成一段专注”日志，却不推进下一步。[现状](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L284-L300>)。后续优先弱化／移除主流程中的计时与次数展示；截止前不拆已测试的存储／恢复逻辑。
- 捕捉标题虽然可空，却排在下一步前且未标可选；可只改为“标题（可选，留空取下一步）”（约 1 分钟）。不必为简化视觉再造折叠面板。[现状](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L75-L84>)。
- 不加连续打卡、罪恶感文案、催促升级、虚假损失或强迫通知。提醒不代替用户打开资源；允许停用、导出和离开。

## 一句话价值与去留门槛

**续页是 Windows 上的手动中断接续工具：离开前用快捷键留下一步和资源引用，回来按需打开资源，把下一步放在工作旁；它不自动恢复应用现场，也不要求你再经营一个笔记库。**

先在相近的真实中断里，与自己已经配置顺手的旧办法比较：保存线索的负担、回来找到下一步／资源的步骤、是否真开始下一步。若依然必须管理第二份清单，或优势只剩外观，**NO-GO：用原工具，停止扩功能**。即使修好小窗断点，也只能说值得继续验证，不能说已证明两小时后的留存。
