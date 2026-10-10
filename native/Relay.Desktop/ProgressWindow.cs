using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Relay.Desktop;

/// <summary>Updates the next step with an optional checkpoint in one caller-owned transaction.</summary>
public sealed class ProgressWindow : Window
{
    private readonly TextBox _progress;
    private readonly TextBox _next;
    private readonly TextBlock _error;
    private readonly Button _confirm;
    private readonly Func<string, string, bool> _save;
    private readonly string _initialNext;
    private bool _dirty;
    private bool _saved;
    private bool _saving;

    public ProgressWindow(Window? owner, string title, string currentNext, Func<string, string, bool> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = save;
        _initialNext = (currentNext ?? "").Trim();
        if (owner is not null) Owner = owner;
        Title = "留个进展 · 续页";
        Width = 540; Height = 480; MinWidth = 440; MinHeight = 420;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = RelayWindowUi.Paper; Foreground = RelayWindowUi.Ink;
        FontFamily = RelayWindowUi.BodyFont;
        var root = new Grid { Margin = new Thickness(26, 22, 26, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(RelayWindowUi.Text("RELAY  /  记住这一小步", 11, RelayWindowUi.Green, true));
        var heading = RelayWindowUi.Text("下一步更清楚，进展按需留。", 25);
        heading.FontFamily = RelayWindowUi.DisplayFont; heading.Margin = new Thickness(0, 8, 0, 6);
        header.Children.Add(heading);
        var cardTitle = RelayWindowUi.Text(title ?? "", 12, RelayWindowUi.Muted);
        cardTitle.MaxHeight = 40; cardTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(cardTitle); root.Children.Add(header);
        var form = new StackPanel();
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _next = RelayWindowUi.Input(currentNext ?? "", "prompt.next", 2000, true);
        _next.MinHeight = 68; _next.MaxHeight = 130; _next.BorderBrush = RelayWindowUi.Green;
        AddField(form, "NEXT ACTION  /  接下来做什么？ *", _next);
        _progress = RelayWindowUi.Input("", "prompt.input", 2000, true);
        _progress.MinHeight = 85; _progress.MaxHeight = 150;
        AddField(form, "刚刚推进了什么？（选填）", _progress);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        _error = RelayWindowUi.Text("", 12, RelayWindowUi.Error);
        _error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_error, "prompt.error");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        footer.Children.Add(_error);
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _confirm = RelayWindowUi.Button("保存下一步", Save, "prompt.confirm", true);
        DockPanel.SetDock(_confirm, Dock.Right); actions.Children.Add(_confirm);
        var cancel = RelayWindowUi.Button("取消", Close, "prompt.cancel");
        DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
        var hint = RelayWindowUi.Text("Ctrl + Enter 保存", 11, RelayWindowUi.Muted);
        hint.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(hint);
        footer.Children.Add(actions);
        _progress.TextChanged += (_, _) => _dirty = true;
        _next.TextChanged += (_, _) => _dirty = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { Save(); e.Handled = true; }
        };
        Closing += ConfirmDiscard;
        Loaded += (_, _) => _next.Focus();
    }

    private static void AddField(Panel panel, string caption, TextBox field)
    {
        panel.Children.Add(new Label { Content = caption, Target = field, Foreground = RelayWindowUi.Green,
            FontSize = 12, Padding = new Thickness(0, 0, 0, 7) });
        AutomationProperties.SetName(field, caption);
        field.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(field);
    }

    private void Save()
    {
        if (_saving) return;
        var progress = _progress.Text.Trim();
        var next = _next.Text.Trim();
        if (next.Length == 0)
        { Error("请留下接下来的一个动作。"); _next.Focus(); return; }
        if (_progress.Text.Length > 2000 || _next.Text.Length > 2000)
        { Error("进展和下一步各最多 2000 字。"); return; }
        if (progress.Length == 0 && string.Equals(next, _initialNext, StringComparison.Ordinal))
        { Error("下一步没有变化；修改后再保存，或关闭。"); _next.Focus(); return; }
        _saving = true; _confirm.IsEnabled = false;
        try
        {
            if (!_save(progress, next))
            { Error("保存未完成，内容仍在这里。处理保存问题后可以重试。"); return; }
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        { _saved = false; Error("未能保存：" + ex.Message); }
        finally { _saving = false; _confirm.IsEnabled = true; }
    }

    private void Error(string message)
    { _error.Text = message; _error.Visibility = Visibility.Visible; }

    private void ConfirmDiscard(object? sender, CancelEventArgs e)
    {
        if (_saved) return;
        if (_saving) { e.Cancel = true; return; }
        if (_dirty)
            e.Cancel = MessageBox.Show(this, "这次进展还没有保存。要放弃并关闭吗？", "保留你的进展",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
    }
}
