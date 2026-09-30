using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Relay.Core;

namespace Relay.Desktop;

/// <summary>An explicit, local-only handoff editor. The caller owns persistence.</summary>
public sealed class CaptureWindow : Window
{
    private readonly RelayCard _draft;
    private readonly TextBox _title;
    private readonly TextBox _next;
    private readonly TextBox _context;
    private readonly ListBox _resources;
    private readonly TextBlock _message;
    private readonly TextBlock _resourceCount;
    private bool _dirty;
    private bool _saved;
    public RelayCard? ResultCard { get; private set; }
    /// <summary>Optional persistence boundary. Return false or throw to retain the draft in this dialog.</summary>
    public Func<RelayCard, bool>? SaveHandler { get; set; }

    /// <summary>Add explicitly forwarded Explorer resources to the current draft.</summary>
    public void AddResources(IEnumerable<string> paths) => Guard(() => AddLocalResources(paths));

    public CaptureWindow(RelayCard? existing, IEnumerable<string>? initialResources = null)
    {
        // Round-tripping the complete model also preserves metadata added by Core later.
        _draft = existing is null ? new RelayCard() :
            JsonSerializer.Deserialize<RelayCard>(JsonSerializer.Serialize(existing))
                ?? throw new InvalidOperationException("无法复制这张续页。");
        if (existing is null)
        {
            _draft.CreatedAt = DateTimeOffset.Now;
            _draft.UpdatedAt = _draft.CreatedAt;
        }
        _draft.Resources ??= new List<ResourceRef>();
        Title = existing is null ? "留个下一步 · 续页" : "编辑续页";
        Width = 620; Height = 680; MinWidth = 520; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = RelayWindowUi.Paper;
        Foreground = RelayWindowUi.Ink;
        FontFamily = RelayWindowUi.BodyFont;
        FontSize = 14;
        AllowDrop = true;

        var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(RelayWindowUi.Text("RELAY  /  留给回来的自己", 11, RelayWindowUi.Green, true));
        var heading = RelayWindowUi.Text(existing is null ? "下次，从这里继续。" : "把下一步留清楚。", 28);
        heading.FontFamily = RelayWindowUi.DisplayFont;
        heading.Margin = new Thickness(0, 7, 0, 5);
        header.Children.Add(heading);
        header.Children.Add(RelayWindowUi.Text("只记下线索，不复制文件，也不自动打开任何资源。", 12, RelayWindowUi.Muted));
        root.Children.Add(header);

        var form = new StackPanel();
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _title = RelayWindowUi.Input(_draft.Title ?? "", "capture.title", 200);
        AddField(form, "标题", _title);
        _next = RelayWindowUi.Input(_draft.NextAction ?? "", "capture.next", 2000, true);
        _next.MinHeight = 66; _next.MaxHeight = 110;
        _next.FontSize = 17;
        _next.BorderBrush = RelayWindowUi.Green;
        AddField(form, "NEXT ACTION  /  下一步做什么？ *", _next, true);
        _context = RelayWindowUi.Input(_draft.Context ?? "", "capture.context", 50000, true);
        _context.MinHeight = 84; _context.MaxHeight = 160;
        AddField(form, "上下文 · 刚做到哪，什么值得记住", _context);

        var resourceHeader = new DockPanel { Margin = new Thickness(0, 2, 0, 7) };
        _resourceCount = RelayWindowUi.Text("0 / 100", 11, RelayWindowUi.Muted);
        DockPanel.SetDock(_resourceCount, Dock.Right); resourceHeader.Children.Add(_resourceCount);
        resourceHeader.Children.Add(RelayWindowUi.Text("随手带上 · 文件、文件夹或链接", 12, RelayWindowUi.Muted, true));
        form.Children.Add(resourceHeader);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        actions.Children.Add(RelayWindowUi.Button("＋ 文件", () => Guard(AddFiles), "capture.addFile"));
        actions.Children.Add(RelayWindowUi.Button("＋ 文件夹", () => Guard(AddFolder), "capture.addFolder"));
        actions.Children.Add(RelayWindowUi.Button("＋ 网址", () => Guard(AddUrl), "capture.addUrl"));
        actions.Children.Add(RelayWindowUi.Button("粘贴剪贴板", () => Guard(PasteClipboard), "capture.clipboard"));
        form.Children.Add(actions);
        _resources = new ListBox { MinHeight = 54, MaxHeight = 132, Background = RelayWindowUi.Surface,
            BorderBrush = RelayWindowUi.Line, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_resources, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(_resources, "capture.resources");
        AutomationProperties.SetName(_resources, "附加资源，可选择后按 Delete 移除");
        _resources.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _resources.SelectedItem is FrameworkElement item && item.Tag is ResourceRef resource)
            { RemoveResource(resource); e.Handled = true; }
        };
        form.Children.Add(_resources);
        form.Children.Add(RelayWindowUi.Text("可拖入本地文件。程序与脚本只会定位，不会执行。", 11, RelayWindowUi.Muted));

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        _message = RelayWindowUi.Text("", 12, RelayWindowUi.Error);
        _message.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_message, "capture.message");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Assertive);
        footer.Children.Add(_message);
        var footerActions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var save = RelayWindowUi.Button("留下下一步", Save, "capture.save", true);
        DockPanel.SetDock(save, Dock.Right); footerActions.Children.Add(save);
        var cancel = RelayWindowUi.Button("取消", Close, "capture.cancel");
        DockPanel.SetDock(cancel, Dock.Right); footerActions.Children.Add(cancel);
        var hint = RelayWindowUi.Text("Ctrl + Enter 保存", 11, RelayWindowUi.Muted);
        hint.VerticalAlignment = VerticalAlignment.Center;
        footerActions.Children.Add(hint); footer.Children.Add(footerActions);

        _title.TextChanged += MarkDirty; _next.TextChanged += MarkDirty; _context.TextChanged += MarkDirty;
        RefreshResources();
        if (initialResources is not null)
            Guard(() => AddLocalResources(initialResources));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { Save(); e.Handled = true; }
        };
        PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        PreviewDrop += (_, e) =>
        {
            Guard(() =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddLocalResources(paths);
            });
            e.Handled = true;
        };
        Closing += ConfirmDiscard;
        Loaded += (_, _) => { _next.Focus(); _next.CaretIndex = _next.Text.Length; };
    }

    private static void AddField(Panel form, string caption, TextBox input, bool prominent = false)
    {
        var label = new Label { Content = caption, Target = input, FontSize = 12,
            Foreground = prominent ? RelayWindowUi.Green : RelayWindowUi.Muted,
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(input, caption);
        form.Children.Add(label);
        input.Margin = new Thickness(0, 0, 0, 13); form.Children.Add(input);
    }

    private void MarkDirty(object sender, TextChangedEventArgs e) => _dirty = true;

    private void ConfirmDiscard(object? sender, CancelEventArgs e)
    {
        if (_saved || !_dirty) return;
        e.Cancel = MessageBox.Show(this, "这次修改还没有保存。要放弃并关闭吗？", "保留你的下一步",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
    }

    private void Save()
    {
        Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(_next.Text))
            { ShowMessage("先留下一句具体的下一步，再保存。", true); _next.Focus(); return; }
            if (_title.Text.Length > 200 || _next.Text.Length > 2000 || _context.Text.Length > 50000 || _draft.Resources.Count > 100)
                throw new InvalidOperationException("内容超出上限：标题 200 字、下一步 2000 字、上下文 50000 字、资源 100 个。");
            _draft.Title = _title.Text.Trim();
            _draft.NextAction = _next.Text.Trim();
            if (_draft.Title.Length == 0)
            {
                var firstLine = _draft.NextAction.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                _draft.Title = firstLine[..Math.Min(firstLine.Length, 200)];
            }
            _draft.Context = _context.Text;
            _draft.UpdatedAt = DateTimeOffset.Now;
            if (SaveHandler is not null && !SaveHandler(_draft))
            {
                ShowMessage("保存未完成，草稿仍在这里。请处理保存问题后重试。", true);
                return;
            }
            ResultCard = _draft;
            _saved = true;
            try { DialogResult = true; }
            catch { _saved = false; ResultCard = null; throw; }
        });
    }

    private void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = "带上文件线索", Multiselect = true, CheckFileExists = true,
            Filter = "所有文件|*.*", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) AddLocalResources(dialog.FileNames);
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "带上文件夹线索", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddLocalResources(dialog.FolderNames);
    }

    private void AddLocalResources(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        var executableAdded = false;
        foreach (var raw in paths)
        {
            if (_draft.Resources.Count >= 100) { rejected.Add("最多可附加 100 个资源。"); break; }
            try
            {
                var path = ValidateLocalPath(raw);
                if (_draft.Resources.Any(r => string.Equals(r.Target, path, StringComparison.OrdinalIgnoreCase))) continue;
                var folder = Directory.Exists(path);
                var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrWhiteSpace(label)) label = path;
                _draft.Resources.Add(new ResourceRef { Target = path, Label = label,
                    Kind = folder ? "folder" : "file" });
                if (string.IsNullOrWhiteSpace(_title.Text)) _title.Text = label[..Math.Min(label.Length, 200)];
                executableAdded |= IsExecutable(path);
                _dirty = true;
            }
            catch (Exception ex) { rejected.Add(ex.Message); }
        }
        RefreshResources();
        if (rejected.Count != 0) ShowMessage(string.Join("\n", rejected.Distinct().Take(3)), true);
        else if (executableAdded) ShowMessage("已保留程序或脚本的引用。恢复时只定位文件，不直接运行。", false);
        else ShowMessage("只保存引用，文件内容不会被读取或复制。", false);
    }

    private static string ValidateLocalPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("无法附加空路径。");
        var path = raw.Replace('/', '\\');
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal) ||
            path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new InvalidOperationException("仅支持本机磁盘的绝对路径；不接受网络 UNC、设备路径或相对路径。");
        if (path.Length > 32767 || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl) ||
            path.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0)
            throw new InvalidOperationException("路径过长或包含通配符、设备、备用数据流等不支持的字符。");
        path = Path.GetFullPath(path);
        foreach (var part in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = part.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
                throw new InvalidOperationException("不能附加 Windows 设备名称。");
        }
        return path;
    }

    private static bool IsExecutable(string path) => new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".hta", ".lnk", ".url", ".reg", ".cpl", ".dll" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void AddUrl()
    {
        var prompt = new Window { Title = "附加网址", Owner = this, Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = RelayWindowUi.Paper, FontFamily = RelayWindowUi.BodyFont, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(RelayWindowUi.Text("把一个链接留在这里", 21));
        panel.Children.Add(RelayWindowUi.Text("仅支持 http / https；不会自动访问。", 12, RelayWindowUi.Muted));
        var input = RelayWindowUi.Input("", "capture.urlInput", 8192);
        input.Margin = new Thickness(0, 15, 0, 8); panel.Children.Add(input);
        AutomationProperties.SetName(input, "网址，仅支持 http 或 https");
        var error = RelayWindowUi.Text("", 12, RelayWindowUi.Error); panel.Children.Add(error);
        void Accept()
        {
            try { AddUrlReference(input.Text); prompt.DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; input.Focus(); }
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(RelayWindowUi.Button("取消", prompt.Close, "capture.urlCancel"));
        buttons.Children.Add(RelayWindowUi.Button("附加链接", Accept, "capture.urlSave", true));
        panel.Children.Add(buttons); prompt.Content = panel;
        prompt.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { prompt.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        prompt.Loaded += (_, _) => input.Focus();
        prompt.ShowDialog();
    }

    private void AddUrlReference(string value)
    {
        if (value.Length > 32767 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("请输入完整的 http:// 或 https:// 网址。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("请移除网址中的用户名或密码后再附加。");
        if (_draft.Resources.Any(r => r.Target == uri.AbsoluteUri)) return;
        if (_draft.Resources.Count >= 100) throw new InvalidOperationException("最多可附加 100 个资源。");
        _draft.Resources.Add(new ResourceRef { Kind = "url", Target = uri.AbsoluteUri, Label = uri.Host });
        _dirty = true; RefreshResources(); ShowMessage("链接已附加；尚未访问该网址。", false);
    }

    private void PasteClipboard()
    {
        // Deliberately called only by the visible button, never during window creation.
        if (Clipboard.ContainsFileDropList())
        { AddLocalResources(Clipboard.GetFileDropList().Cast<string>()); return; }
        if (!Clipboard.ContainsText()) { ShowMessage("剪贴板中没有可附加的文字或文件。", false); return; }
        var text = Clipboard.GetText();
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        { AddUrlReference(text); return; }
        var addition = (_context.Text.Length == 0 ? "" : Environment.NewLine) + text;
        if (addition.Length + _context.Text.Length > 50000)
            throw new InvalidOperationException("粘贴后会超过上下文 50000 字上限；请先缩短内容。");
        _context.AppendText(addition); _context.Focus(); _context.CaretIndex = _context.Text.Length;
        ShowMessage("剪贴板文字已放入上下文。", false);
    }

    private void RemoveResource(ResourceRef resource)
    { _draft.Resources.Remove(resource); _dirty = true; RefreshResources(); }

    private void RefreshResources()
    {
        _resources.Items.Clear();
        _resourceCount.Text = $"{_draft.Resources.Count} / 100";
        foreach (var resource in _draft.Resources)
        {
            var row = new DockPanel { LastChildFill = true, Tag = resource, Margin = new Thickness(2) };
            var remove = RelayWindowUi.Button("移除", () => RemoveResource(resource), "capture.removeResource");
            remove.Padding = new Thickness(9, 4, 9, 4); remove.FontSize = 11;
            AutomationProperties.SetName(remove, "移除 " + resource.Label);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var text = RelayWindowUi.Text(resource.Label ?? resource.Target, 12);
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center; text.ToolTip = resource.Target;
            row.Children.Add(text);
            _resources.Items.Add(row);
        }
        if (_draft.Resources.Count == 0)
            _resources.Items.Add(new ListBoxItem { Content = "暂时没有资源，也可以直接保存。", IsEnabled = false,
                Foreground = RelayWindowUi.Muted, FontSize = 12 });
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowMessage("未能完成：" + ex.Message, true); }
    }

    private void ShowMessage(string message, bool error)
    {
        _message.Text = message;
        _message.Foreground = error ? RelayWindowUi.Error : RelayWindowUi.Green;
        _message.Visibility = Visibility.Visible;
    }
}

// Shared only by the two small windows; no dependency on the main window's Ui helpers.
internal static class RelayWindowUi
{
    internal static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(35, 54, 47));
    internal static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(103, 112, 103));
    internal static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(244, 243, 236));
    internal static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(253, 252, 247));
    internal static readonly Brush Green = new SolidColorBrush(Color.FromRgb(28, 65, 51));
    internal static readonly Brush Line = new SolidColorBrush(Color.FromRgb(217, 222, 208));
    internal static readonly Brush Error = new SolidColorBrush(Color.FromRgb(151, 63, 44));
    internal static readonly FontFamily BodyFont = new("Microsoft YaHei UI, Segoe UI");
    internal static readonly FontFamily DisplayFont = new("STZhongsong, SimSun, Georgia");
    internal static TextBlock Text(string text, double size = 14, Brush? color = null, bool bold = false) =>
        new() { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    internal static TextBox Input(string text, string id, int maxLength, bool multiline = false)
    {
        var box = new TextBox { Text = text, MaxLength = maxLength, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            Background = Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), FontSize = 14, MinHeight = 36 };
        AutomationProperties.SetAutomationId(box, id); return box;
    }
    internal static Button Button(string label, Action click, string id, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 6, 0), Background = primary ? Green : Surface,
            Foreground = primary ? Brushes.White : Ink, BorderBrush = primary ? Green : Line,
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, FontSize = 12,
            MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => click(); return button;
    }
}
