# 续页桌面端：键盘与可访问性静态复核

## 范围与证据边界

本次只读审查 [Theme.xaml](<D:/DPH/native/Relay.Desktop/Theme.xaml>)、[MainWindow](<D:/DPH/native/Relay.Desktop/MainWindow.cs>)、[CaptureWindow](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs>)、[ProgressWindow](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs>)、[HelpWindow](<D:/DPH/native/Relay.Desktop/HelpWindow.cs>)、[ResumeStrip](<D:/DPH/native/Relay.Desktop/ResumeStrip.cs>)，并追读共用 UI helper 与全局快捷键派发路径。初始审查阶段仅新增本文、不改 UI；随后收到父代理的明确追加授权，实施了下述有限修补。没有启动/停止应用、进程检查、模拟键盘、屏幕捕获或图像输入；**不是视觉审查，也不是完整可访问性审计**。

父代理提供的 **18 项 UIA / 30 次重启通过**作为已有运行验证背景，不在此重复执行，也不把它扩大解释为读屏、IME、键盘焦点或高对比度全部通过。下述区分源码确定行为与需要后续运行确认的风险。目标是中文 Windows 小工具的低摩擦使用，不建议扩展产品功能。

## 五项优先修复（按操作影响排序）

### 1. 保留重绘前后的键盘焦点与可预测 Tab 顺序（高）

**证据：**[MainWindow:98–126](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L98-L126>) 的导航、卡片点击会调用 Render / RenderList；这些方法先清空容器再新建按钮，当前获得焦点的控件随即被移出树。[MainWindow:129–163](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L129-L163>) 同样重建详情；暂停计时等操作也调用它。没有按卡片 ID / 控件用途恢复焦点的代码。到期处理也能在主窗口操作期间重建列表（[MainWindow:289–306](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L289-L306>)）。**源码确定移除了焦点目标；WPF 最终把焦点落到哪里尚未运行核验。**

另，侧栏先添加底部设置/帮助，再添加导航（[MainWindow:71–76](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L71-L76>)）；捕捉/进展底部先添加保存、再添加同为 Dock.Right 的取消（[CaptureWindow:118–125](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L118-L125>)、[ProgressWindow:62–69](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L62-L69>)）。未显式设置 TabIndex，默认树顺序不等于屏幕位置顺序。

**最小修复：**重绘前记录是否焦点在受影响区域及逻辑目标，重绘后仅在确有需要时恢复到同卡片/同操作；目标消失时落到相邻卡片或空态入口。搜索输入更新列表时不能抢走搜索焦点。按明确的导航→内容、取消→保存顺序整理添加顺序或局部 TabIndex；不要求改成新导航框架。

**后续验收：**键盘选卡、切筛选、置顶、暂停/继续后焦点仍可见且下一次 Tab 有确定去向；删除选中卡有明确后继；异步到期更新不打断输入。这里未执行这些步骤。

### 2. 全局命令尊重当前模态窗口（高）

**证据：**[WindowsIntegration:201–215](<D:/DPH/native/Relay.Desktop/Services/WindowsIntegration.cs#L201-L215>) 收到 WM_HOTKEY 即把 capture/resume 委托放入 Dispatcher，没有模态状态检查。[MainWindow:179–210](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L179-L210>) 中 Capture 只检查已有 capture，Checkpoint 只检查已有 progress；ResumeLatest 不检查任一模态窗口，直接 ShowMain、改变卡片状态、重绘并 ShowStrip。帮助、设置、URL 子对话框也没有统一的模态入口保护。ShowDialog 的消息循环不等于停止这些 Dispatcher 回调。

**影响：**用户在编辑或确认时触发全局续做，源码路径仍可改变背后状态并打开新窗口；捕捉快捷键也可能在进展/设置上方再套一层对话框。实际前台激活效果受 Windows/WPF 控制，不能据静态代码声称已观察到“抢焦点”。

**最小修复：**为全局/托盘/外部参数入口共用一个“当前模态交互”守卫。已有模态窗口时激活最内层可操作窗口，不修改卡片、不另开续做条；不绕过其子确认框。保持当前快捷键可配置、冲突回滚的实现，不增加新快捷键或后台监控。

**后续验收：**捕捉、进展、帮助、设置以及 URL/放弃确认打开期间，全局两组快捷键不会造成第二个可操作模态层或改变当前卡片；关闭后焦点回到合理调用点。

### 3. URL 对话框不要用窗口级 Enter 覆盖取消按钮；IME 优先（中高）

**证据：**[CaptureWindow:268–295](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L268-L295>) 已有“取消”按钮，但窗口 PreviewKeyDown 对任意 `Key.Enter` 都执行 `Accept()` 并标记 Handled。**在取消按钮有键盘焦点且 URL 有效时，按 Enter 走的是附加链接，不是取消。**这比纯粹的快捷键一致性更重要。

[CaptureWindow:131–136](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L131-L136>)、[ProgressWindow:72–77](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L72-L77>) 的 Escape/Control+Enter 以及 [MainWindow:57](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L57>) 的全窗口 Escape 清搜索都没有显式 composition 状态守卫。WPF 可以将输入法按键报告为 `Key.ImeProcessed`；**不能仅凭缺少守卫断言中文候选 Escape 必然关窗或 Enter 必然误保存**。这仍是中文输入的定向验证缺口。

**最小修复：**移除 URL 窗口对所有 Enter 的截获，采用 WPF 默认按钮语义或只在 URL 输入框且非 composition 状态下提交，让有焦点的取消按钮保留 Enter 行为。统一对编辑窗口的 Escape/保存快捷键给 IME composition 优先权；主窗口清搜索只针对适当搜索上下文。保留现有未保存确认。

**后续验收：**取消按钮的 Enter/Space 均取消；URL 输入框 Enter 才按预期提交；微软拼音候选开启时首次 Escape 只处理候选，候选确定不触发附加/保存；非 composition 的 Escape 与 Ctrl+Enter 仍可用。

### 4. 给核心输入和重复动作补中文可访问名称（中高）

**证据：**[Ui:23–26](<D:/DPH/native/Relay.Desktop/Ui.cs#L23-L26>) 中 Input 不设置名称，Id 仅设置 AutomationId。[MainWindow:25](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L25>)、[MainWindow:83](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L83>) 的搜索只设置 ID / ToolTip，没有明确 Name/Label；[MainWindow:325–326](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L325-L326>) 两个快捷键字段只有共同说明，无法从字段名称区分“捕捉”和“续做”。[HelpWindow:78–84](<D:/DPH/native/Relay.Desktop/HelpWindow.cs#L78-L84>) 的目录是独立 TextBlock 加只读 TextBox，也未绑定标签。主详情资源的重复“打开/更新”按钮不包含资源名称（[MainWindow:145–150](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L145-L150>)）。AutomationId 可供测试定位，但不是面向读屏用户的名称。

**最小修复：**为上述字段设置中文 AutomationProperties.Name 或 Label.Target/LabeledBy；给重复资源按钮设置“打开/更新 + 资源名”的名称。保留已有 AutomationId。不要以英文内部 ID 代替中文名称，也不必在按钮视觉文案上重复长路径。

**后续验收：**UIA Name 能唯一说明这些字段与动作用途，单独聚焦两个快捷键字段能区分其作用。当前没有实际读屏输出证据。捕捉与进展窗口已使用 Label.Target + 中文 SetName，可直接沿用其模式。

### 5. 修正主题的小字对比度，并使颜色/焦点响应 Windows 高对比度（中高）

**证据：**[Ui:10–20](<D:/DPH/native/Relay.Desktop/Ui.cs#L10-L20>) 与 [CaptureWindow:368–399](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L368-L399>) 使用固定 RGB 画笔；[Theme:3–8](<D:/DPH/native/Relay.Desktop/Theme.xaml#L3-L8>) 固定选择、光标、提示和焦点颜色，Button 关闭默认 FocusVisualStyle 后另用 2px 焦点边框。**不是“完全没有焦点提示”**，但没有高对比度分支/系统色资源；控件局部 Background/Foreground 赋值也需要一起处理，单改一个样式不足。

仅用 Node 对源码不透明 sRGB 颜色按 WCAG 相对亮度公式计算，未采样屏幕、未评价渲染字体：

| 静态颜色组合 | 对比度 | 解读 |
|---|---:|---|
| Accent `#AD6636` / Paper `#F4F3EC` | 3.991:1 | 不满足普通小字 4.5:1 参考线 |
| Accent `#AD6636` / Surface `#FDFCF7` | 4.322:1 | 同上 |
| Muted `#677067` / Paper | 4.615:1 | 静态常态组合达到 4.5:1，不应笼统判全体灰字不合格 |
| Ink `#23362F` / Paper | 11.510:1 | 静态常态组合充足 |
| 焦点 Accent / 主按钮 Green `#1C4133` | 2.553:1 | 与按钮内侧相邻色偏弱；不单凭此宣布完整焦点标准失败 |

Accent 确实用于 10–11px 文本（如 [MainWindow:133–136](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L133-L136>)、[HelpWindow:38–41](<D:/DPH/native/Relay.Desktop/HelpWindow.cs#L38-L41>)），不是只用于装饰。Opacity 的悬停/禁用效果未计入这些静态比值。

**最小修复：**把小字号 Accent 文本改为足够深的文字 token（或现有 Ink/Green），同时建立受系统高对比度控制的画笔与焦点资源，覆盖两套 helper 的局部赋值；为主按钮保留独立且清楚的焦点轮廓。无需重做品牌或增加主题设置页。

**后续验收：**实际 Windows 对比度主题切换后文字、输入选区/光标、按钮与焦点仍可辨认，常态小字达到目标静态对比度；需另行真实环境验证。

## 已检查但不额外扩成修复清单的边界

- **输入与关闭保护：**捕捉初始焦点是下一步，进展初始焦点是进展；缺必填内容时回焦对应字段；两者脏草稿关闭确认默认 No（[CaptureWindow:150–178](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs#L150-L178>)、[ProgressWindow:79–96](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L79-L96>)、[ProgressWindow:115–121](<D:/DPH/native/Relay.Desktop/ProgressWindow.cs#L115-L121>)）。多行框启用 AcceptsReturn、没有启用 AcceptsTab，未发现源码层面的必然 Tab 陷阱；捕捉初始跳过标题是符合“先写下一步”的设计，不直接当作缺陷。
- **帮助：**帮助先关闭自己再 Dispatcher 调用捕捉/设置，避免从帮助按钮直接嵌套 owner 模态循环（[HelpWindow:116–120](<D:/DPH/native/Relay.Desktop/HelpWindow.cs#L116-L120>)）。有取消/关闭与 Escape，并不缺退出入口。
- **关闭到托盘：**只在设置开启且 HasTray 时 Hide，设置文字明确解释退出方式，托盘有退出（[MainWindow:239–243](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L239-L243>)、[MainWindow:330](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L330>)、[WindowsIntegration:231–245](<D:/DPH/native/Relay.Desktop/Services/WindowsIntegration.cs#L231-L245>)）。因此不认定“关闭必然困住键盘用户”。一次性气泡仍写默认 Ctrl+Alt+Space，而非当前配置，侧栏只在构造时取配置；这属于次要提示准确性问题，未挤入以上五项。通知是否被读屏播报/系统压制未验证。
- **最小尺寸：**主窗口明确 MinWidth=900 / MinHeight=620（WPF DIP），三列前两列固定 196+345，详情再扣左右 60 边距，900 DIP 宽时详情约不足 299 DIP（尚未扣窗口边框/滚动条）；详情有纵向滚动而横向禁用（[MainWindow:47–54](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L47-L54>)、[MainWindow:87–91](<D:/DPH/native/Relay.Desktop/MainWindow.cs#L87-L91>)）。例如 1920×1080 物理像素在 200% 缩放下可用逻辑高度最多 540 DIP，620 DIP 最小高度无法完全放入。这个算术约束成立，但未观察实际裁切或断言 900×620 必然不可用；应在上述焦点验收时记录可达性，而不是本轮发散成布局重设计。
- **续做条：**有标准 ToolWindow 标题栏、四个真实按钮和 Escape；保持顶层可配置，不把 Topmost 本身当作缺陷。固定高度 170、下一步 MaxHeight=50、内容截断后以 ToolTip 展示全文（[ResumeStrip:26–30](<D:/DPH/native/Relay.Desktop/ResumeStrip.cs#L26-L30>)、[ResumeStrip:61–79](<D:/DPH/native/Relay.Desktop/ResumeStrip.cs#L61-L79>)、[ResumeStrip:99–105](<D:/DPH/native/Relay.Desktop/ResumeStrip.cs#L99-L105>)）；“展开”提供回主窗口路径。长中文/放大文字时是否可读、隐藏任务栏的条是否方便重新获得键盘焦点，均没有本轮运行证据。

## 追加授权后的有限修补状态

父代理在报告完成后授权修改 [Ui](<D:/DPH/native/Relay.Desktop/Ui.cs>)、[CaptureWindow](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs>) 与 [Theme](<D:/DPH/native/Relay.Desktop/Theme.xaml>)。已将 URL Enter 限制为 URL 输入框拥有键盘焦点且无修饰键时提交；取消/确认按钮使用原生按钮键盘行为。Ui Accent 改为 `#965122`；两套 helper 在启动时选择系统高对比度画笔，主按钮采用 HighlightText；主题不再清空默认 FocusVisualStyle，高对比度下使用系统选择/光标/提示颜色和前景焦点边框，不降低按钮 hover opacity。

这些是第 3、5 项的局部修补，不是 IME 或全程序高对比度通过证明：没有增加 composition 生命周期跟踪，也不承诺运行中切换高对比度即时更新。本静态审查子代理没有执行编译或运行操作，交父代理统一构建，避免并发构建与违反原进程操作限制。上面的源码行号/证据描述以修补前审查快照为准，局部修改可能使后续行号偏移。

**父代理随后报告的修补（此处记录其消息，不冒充本子代理独立复核）：**

- [MainWindow](<D:/DPH/native/Relay.Desktop/MainWindow.cs>) 的 Render/List/Detail 已按稳定 ID 保存/恢复焦点，并用 IsActive 防止后台抢焦点；ResumeLatest 增加捕捉/进展/owned dialog 的模态守卫，进展期间转发资源暂存。
- 主搜索、捕捉快捷键、续做快捷键、专注分钟字段补中文 AutomationProperties.Name；这些是第 4 项的核心字段子集，不表示帮助目录/所有重复资源按钮都已核验。
- [Ui](<D:/DPH/native/Relay.Desktop/Ui.cs>) 与 [RelayWindowUi](<D:/DPH/native/Relay.Desktop/CaptureWindow.cs>) 的 Text helper 在高对比度启动状态下统一使用 WindowText，避免 Highlight 同时充当文字色；主窗口侧栏/选中卡用 WindowBrush，选中导航用 HighlightText。
- 父代理报告最终 **6 项模态测试通过，包含 ResumeLatest 路径**；这是父代理运行结果转述，测试步骤和日志未在本子代理中独立检查。父代理报告的 **18 项 UIA 通过**仍是更新打包前基线，更新版本重跑待其打包测试后完成，本文不提前记为更新版本通过。

**仍未验证：**真实中文 IME 候选过程、真实读屏朗读、系统高对比度人工切换、实际缩放/裁切和全部键盘 Tab 路径。父代理计划统一编译与 native suite；本次记录时不宣称该构建已经成功，也不据已有测试声称完整可访问性合规。

## 结论

现有标准 WPF 控件、中文标签、草稿确认和快捷键可配置是基础优势。优先做 **焦点保存 → 模态入口保护 → URL Enter/IME 语义 → 明确中文名称 → 系统配色与小字对比度** 五项局部修补。已有 UIA/重启通过继续有效，但不能替代上述尚未执行的定向验收；本报告不宣称整体可访问性合规。
