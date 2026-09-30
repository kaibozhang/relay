using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Relay.Core;
using Relay.Desktop.Services;

namespace Relay.Desktop;

/// <summary>Explicit resource selection. No resources are launched merely by showing the dialog.</summary>
public sealed class ResourceResumeWindow : Window
{
    private sealed class Row
    {
        internal required ResourceRef Resource;
        internal required CheckBox Selection;
        internal required TextBlock Status;
    }
    private readonly List<Row> _rows = new();
    private readonly Func<ResourceRef, bool> _open;
    private readonly TextBlock _summary;
    private readonly Button _openSelected;
    private bool _opening;

    public ResourceResumeWindow(RelayCard card, Func<ResourceRef, bool> open)
    {
        ArgumentNullException.ThrowIfNull(card); ArgumentNullException.ThrowIfNull(open);
        _open = open;
        Title = "选择要恢复的资源 · 续页";
        Width = 650; Height = 540; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = RelayWindowUi.Paper; Foreground = RelayWindowUi.Ink;
        FontFamily = RelayWindowUi.BodyFont;
        var root = new Grid { Margin = new Thickness(25, 22, 25, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        header.Children.Add(RelayWindowUi.Text("RELAY  /  只带回需要的线索", 11, RelayWindowUi.Green, true));
        var heading = RelayWindowUi.Text("选择，再打开。", 27);
        heading.FontFamily = RelayWindowUi.DisplayFont; heading.Margin = new Thickness(0, 8, 0, 6);
        header.Children.Add(heading);
        var title = RelayWindowUi.Text(card.Title ?? "", 13, RelayWindowUi.Muted);
        title.MaxHeight = 42; title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(title);
        header.Children.Add(RelayWindowUi.Text("默认不选中任何资源；每次最多 10 个。程序或脚本只定位，不执行。", 12, RelayWindowUi.Muted));
        root.Children.Add(header);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "resources.list");
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _summary = RelayWindowUi.Text("选择需要的资源后，再点击打开。", 12, RelayWindowUi.Muted);
        AutomationProperties.SetAutomationId(_summary, "resources.status");
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer); footer.Children.Add(_summary);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(RelayWindowUi.Button("关闭", Close, "resources.cancel"));
        _openSelected = RelayWindowUi.Button("打开选中资源", OpenSelected, "resources.openSelected", true);
        actions.Children.Add(_openSelected); footer.Children.Add(actions);
        foreach (var resource in card.Resources ?? new List<ResourceRef>()) AddRow(list, resource);
        if (_rows.Count == 0) list.Children.Add(RelayWindowUi.Text("这张续页还没有附加资源。", 14, RelayWindowUi.Muted));
        UpdateSelectionSummary();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_opening) { Close(); e.Handled = true; }
        };
        Closing += (_, e) => { if (_opening) e.Cancel = true; };
    }

    private void AddRow(Panel list, ResourceRef source)
    {
        // Snapshot exactly the target shown to the user, even if the caller later updates the card.
        var resource = new ResourceRef { Id = source.Id, Kind = source.Kind, Target = source.Target, Label = source.Label };
        var content = new StackPanel();
        content.Children.Add(RelayWindowUi.Text(string.IsNullOrWhiteSpace(resource.Label) ? resource.Target : resource.Label, 14, null, true));
        var target = RelayWindowUi.Text(resource.Target, 12, RelayWindowUi.Muted);
        target.Margin = new Thickness(0, 5, 0, 5);
        // Never truncate the path; wrapping leaves the exact original target visible.
        AutomationProperties.SetName(target, resource.Target);
        content.Children.Add(target);
        var status = RelayWindowUi.Text("尚未请求打开", 11, RelayWindowUi.Green);
        content.Children.Add(status);
        var checkbox = new CheckBox { Content = content, IsChecked = false, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(checkbox, "resources.select." + resource.Id);
        AutomationProperties.SetName(checkbox, resource.Label + " — " + resource.Target);
        var row = new Row { Resource = resource, Selection = checkbox, Status = status };
        _rows.Add(row);
        if (!CanOpen(resource, out var reason)) Disable(row, reason);
        checkbox.Checked += (_, _) => UpdateSelectionSummary();
        checkbox.Unchecked += (_, _) => UpdateSelectionSummary();
        list.Children.Add(new Border { Child = checkbox, Background = RelayWindowUi.Surface,
            BorderBrush = RelayWindowUi.Line, BorderThickness = new Thickness(1), Padding = new Thickness(13),
            Margin = new Thickness(0, 0, 0, 8) });
    }

    private static bool CanOpen(ResourceRef resource, out string reason)
    {
        try
        {
            if (resource.Kind == "url")
            {
                if (Uri.TryCreate(resource.Target, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
                    !resource.Target.Any(char.IsWhiteSpace) && !resource.Target.Contains('\\'))
                { reason = ""; return true; }
                reason = "网址无效，或包含不支持的协议、登录凭据。"; return false;
            }
            if (resource.Kind != "file" && resource.Kind != "folder")
            { reason = "不支持的资源类型。"; return false; }
            return ResourceSafety.IsLocalNonRedirected(resource.Target, out reason);
        }
        catch (Exception ex) { reason = "无法验证资源：" + ex.Message; return false; }
    }

    private static void Disable(Row row, string reason)
    {
        row.Selection.IsChecked = false; row.Selection.IsEnabled = false;
        row.Status.Foreground = RelayWindowUi.Error;
        row.Status.Text = "不可打开 · " + reason;
    }

    private void UpdateSelectionSummary()
    {
        if (_opening) return;
        var count = _rows.Count(r => r.Selection.IsEnabled && r.Selection.IsChecked == true);
        _summary.Text = count > 10 ? "每次最多打开 10 个资源，请减少选择。" : $"已选 {count} 个 · 不会自动打开未选中的资源。";
        _summary.Foreground = count > 10 ? RelayWindowUi.Error : RelayWindowUi.Muted;
        _openSelected.IsEnabled = count is > 0 and <= 10;
    }

    private void OpenSelected()
    {
        if (_opening) return;
        var selected = _rows.Where(r => r.Selection.IsEnabled && r.Selection.IsChecked == true).ToList();
        if (selected.Count == 0 || selected.Count > 10) { UpdateSelectionSummary(); return; }
        _opening = true; _openSelected.IsEnabled = false;
        var launched = 0;
        try
        {
            foreach (var row in selected)
            {
                // Recheck immediately before delegating. The parent also checks before OS launch.
                if (!CanOpen(row.Resource, out var reason)) { Disable(row, reason); continue; }
                row.Status.Text = "等待打开确认…";
                try
                {
                    if (_open(row.Resource))
                    {
                        launched++; row.Selection.IsChecked = false;
                        row.Status.Foreground = RelayWindowUi.Green;
                        row.Status.Text = "已提交打开请求 · 请在目标应用中确认";
                    }
                    else
                    {
                        row.Status.Foreground = RelayWindowUi.Muted;
                        row.Status.Text = "未提交打开请求 · 已取消或未能打开";
                    }
                }
                catch (Exception ex)
                { row.Status.Foreground = RelayWindowUi.Error; row.Status.Text = "打开失败 · " + ex.Message; }
            }
        }
        finally
        {
            _opening = false;
            UpdateSelectionSummary();
            _summary.Text = $"已提交 {launched} / {selected.Count} 个打开请求。实际打开结果请在对应应用中确认。";
        }
    }
}
