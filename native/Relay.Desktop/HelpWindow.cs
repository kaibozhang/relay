using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Relay.Desktop;

/// <summary>Read-only onboarding; only explicit button activation invokes a caller action.</summary>
public sealed class HelpWindow : Window
{
    public HelpWindow(Window owner, Action capture, Action settings, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        Owner = owner;
        Title = "续页 · 从哪里继续";
        Width = 720;
        Height = 740;
        MinWidth = 580;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Ui.Paper;
        Foreground = Ui.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = Ui.Stack(
            Ui.Text("RELAY / 留给回来的自己", 11, Ui.Accent, true),
            Ui.Text("被打断之后，不必重新找。", 27, Ui.Ink, true),
            Ui.Text("中断 → 下一步 → 资源线索 → 工作旁的续做条", 13, Ui.Muted));
        header.Margin = new Thickness(0, 0, 0, 18);
        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        AddSection(body, "为什么还要一个独立小工具？",
            "待办适合安排任务，笔记适合存资料；续页只把“刚做到哪、下一步做什么、资源在哪”放在一起。回来时，把下一步留在工作旁，不必一直停在管理页面。",
            "它可能减少重新寻找线索的步骤，但这不是已测出的效率优势。若现有工具已能顺手做到这些，就不必再维护一份。无需浏览器、宿主应用或账号；不在后台监控窗口、文件或剪贴板。");

        var sample = Ui.Stack(Ui.Text("三步试用 · 仅为说明，不会创建示例数据", 15, Ui.Green, true));
        Ui.Gap(sample, 12);
        AddStep(sample, "01  要离开时，留一个可执行动作",
            "例如：核对报告第3页收入数据。\n不要只写：继续做报告。再补一句刚做到哪。这里是假设情境，不是你的真实任务。");
        AddStep(sample, "02  带上资源的引用",
            "主动选择或拖入文件、文件夹，或添加网址。只保存路径／链接，不复制文件内容；剪贴板也只有你点击粘贴时才读取。确认保存后，再离开。");
        AddStep(sample, "03  回来后，接着做",
            "选择续做卡，打开需要的资源，把续做条放在工作窗口旁。资源由你确认打开；做完一段后记下进展，再留下下一步。");
        body.Children.Add(Ui.Panel(sample));
        Ui.Gap(body, 18);

        AddSection(body, "顺手的入口 · 以下是默认快捷键",
            "Ctrl+Alt+Space  留下一步\nCtrl+Alt+R  续做最近一张\nCtrl+N  主窗口内新建；Ctrl+F  主窗口内搜索",
            "两组全局快捷键可在“空间与系统设置”自定义，以设置中的当前值和注册状态为准。冲突或受系统策略限制时，仍可用主窗口按钮或可用的托盘入口。");

        AddSection(body, "本机保存，不是云端保险箱",
            "确认保存等操作会自动写入本机磁盘；只有写入成功，才应看到“已保存／已写入磁盘”的确认。输入中的草稿不是已经保存的数据。写入失败时编辑草稿保留在窗口中，请先处理错误再重试，不要直接退出。",
            "vault.json 是卡片与设置，vault.json.bak 是更新时保留的上一版，focus.json 保存专注计时状态。它们是未加密的本地 JSON；备份不是完整历史，也不能代替另一位置的定期备份。");
        body.Children.Add(Ui.Text("当前数据目录（可选中复制）", 12, Ui.Muted, true));
        Ui.Gap(body, 6);
        var directory = Ui.Input(dataDirectory, true);
        directory.IsReadOnly = true;
        directory.MaxHeight = 100;
        directory.ToolTip = "只显示目录；不会自动打开文件夹或修改数据。";
        body.Children.Add(directory);
        Ui.Gap(body, 18);

        AddSection(body, "边界说清楚，才值得留下",
            "续页不能恢复其他应用未保存的编辑内容、光标位置或窗口布局。打开网址仍需系统浏览器，打开文件仍需对应应用；续页本身不需要浏览器或 WebView。Android 版暂缓，当前没有跨设备自动同步。",
            "设置中可导出／恢复明文 JSON；迁移前先备份，文件引用指向的原始资源也要另行迁移。系统右键等入口只在你主动启用时注册，部分环境会失败；没有启动时自动修改注册表。托盘驻留用于快捷键与提醒，不等于后台监控。");
        AddSection(body, "留不留下，用自己的中断场景判断",
            "先用原来的办法，再用续页处理几次相近的中断。分别记下：留线索耗时、回来找到下一步的耗时、找资源的次数，以及是否真的完成下一步。若维护成本更高，就继续用原来的工具。这里没有预设的提升百分比。");

        var footer = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var captureButton = Ui.Id(Ui.Button("留自己的第一步", () => OpenAction(capture), true), "help.capture");
        captureButton.ToolTip = "关闭帮助并打开空白捕捉窗口；不会插入示例。";
        footer.Children.Add(captureButton);
        footer.Children.Add(Ui.Id(Ui.Button("空间与系统设置", () => OpenAction(settings)), "help.settings"));
        var close = Ui.Id(Ui.Button("关闭", Close), "help.close");
        close.IsCancel = true;
        close.Margin = new Thickness(0);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void OpenAction(Action action)
    {
        // Exit the owner's modal help loop before opening another owned window.
        Close();
        Dispatcher.BeginInvoke(action);
    }

    private static void AddSection(Panel parent, string title, params string[] paragraphs)
    {
        parent.Children.Add(Ui.Text(title, 16, Ui.Ink, true));
        foreach (var paragraph in paragraphs)
        {
            Ui.Gap(parent, 7);
            parent.Children.Add(Ui.Text(paragraph, 14));
        }
        Ui.Gap(parent, 20);
    }

    private static void AddStep(Panel parent, string title, string explanation)
    {
        parent.Children.Add(Ui.Text(title, 14, Ui.Green, true));
        Ui.Gap(parent, 5);
        parent.Children.Add(Ui.Text(explanation, 14));
        Ui.Gap(parent, 12);
    }
}
