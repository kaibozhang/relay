using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Relay.Core;

namespace Relay.Desktop;

/// <summary>A quiet, always-visible reminder; scheduling and persistence remain with the parent.</summary>
public sealed class ResumeStrip : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _next;
    private readonly TextBlock _feedback;
    private readonly Button? _resourcesButton;
    private bool _positioned;
    public event Action? PauseRequested;

    public ResumeStrip(RelayCard card, Action showMain, Action checkpoint, Action complete, Action? resources = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(showMain);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(complete);
        Title = "续页 · 正在继续";
        Width = 460; Height = 180; MinWidth = 360; MinHeight = 160;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = RelayWindowUi.Paper; Foreground = RelayWindowUi.Ink;
        FontFamily = RelayWindowUi.BodyFont;
        AutomationProperties.SetAutomationId(this, "resume.window");

        var root = new Grid { Margin = new Thickness(15, 8, 15, 10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var bar = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            Margin = new Thickness(0, 0, 0, 5), ToolTip = "拖动此处移动续做条" };
        var mark = RelayWindowUi.Text("●  续做中", 10, RelayWindowUi.Green, true);
        mark.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(mark, Dock.Left); bar.Children.Add(mark);
        _title = RelayWindowUi.Text("", 11, RelayWindowUi.Muted);
        _title.TextWrapping = TextWrapping.NoWrap; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(_title, "resume.title");
        bar.Children.Add(_title); root.Children.Add(bar);
        // Only this top bar (and the native title bar) moves the window, not content/buttons.
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); ClampToWorkArea(); }
            catch (InvalidOperationException) { /* Mouse release raced with the native drag loop. */ }
            e.Handled = true;
        };

        _next = RelayWindowUi.Text("", 17, RelayWindowUi.Ink, true);
        _next.FontFamily = RelayWindowUi.DisplayFont;
        _next.TextTrimming = TextTrimming.CharacterEllipsis;
        _next.MaxHeight = 50; _next.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_next, "resume.next");
        Grid.SetRow(_next, 1); root.Children.Add(_next);
        _feedback = RelayWindowUi.Text("", 10, RelayWindowUi.Error);
        _feedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Assertive);
        Grid.SetRow(_feedback, 2); root.Children.Add(_feedback);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        buttons.Children.Add(RelayWindowUi.Button("展开", () => Invoke(showMain), "resume.expand"));
        if (resources is not null)
        {
            _resourcesButton = RelayWindowUi.Button("资源", () => Invoke(resources), "resume.resources");
            _resourcesButton.ToolTip = "选择需要恢复的资源，不会自动打开";
            buttons.Children.Add(_resourcesButton);
        }
        buttons.Children.Add(RelayWindowUi.Button("留个进展", () => Invoke(checkpoint), "resume.checkpoint"));
        buttons.Children.Add(RelayWindowUi.Button("完成", () => Invoke(complete), "resume.complete", true));
        var collapse = RelayWindowUi.Button("收起", Close, "resume.collapse");
        collapse.ToolTip = "收起续做条，不标记完成";
        buttons.Children.Add(collapse);
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        UpdateCard(card);
        Loaded += (_, _) =>
        {
            if (!_positioned)
            {
                var work = SystemParameters.WorkArea;
                Left = work.Right - ActualWidth - 20;
                Top = work.Bottom - ActualHeight - 20;
                _positioned = true;
            }
            ClampToWorkArea();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Closed += (_, _) => PauseRequested?.Invoke();
    }

    public void UpdateCard(RelayCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _title.Text = string.IsNullOrWhiteSpace(card.Title) ? "未命名续页" : card.Title;
        _title.ToolTip = _title.Text;
        _next.Text = string.IsNullOrWhiteSpace(card.NextAction) ? "展开续页，留下一步。" : card.NextAction;
        _next.ToolTip = _next.Text;
        if (_resourcesButton is not null)
            _resourcesButton.Visibility = card.Resources is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _feedback.Visibility = Visibility.Collapsed;
    }

    private void Invoke(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _feedback.Text = "未能完成：" + ex.Message;
            _feedback.Visibility = Visibility.Visible;
        }
    }

    private void ClampToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }
}
