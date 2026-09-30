using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Relay.Core;
using Relay.Desktop.Services;

namespace Relay.Desktop;
public sealed class MainWindow : Window
{
    private readonly VaultStore _store;
    private VaultDocument _document;
    private readonly Grid _root=new();
    private readonly StackPanel _navigation=new();
    private readonly StackPanel _list=new();
    private readonly StackPanel _detail=new();
    private readonly TextBox _search=Ui.Id(Ui.Input(),"main.search");
    private readonly TextBlock _status=Ui.Text("",11,Ui.Muted);
    private readonly TextBlock _heading=Ui.Text("留下一步，回来就能继续。",27,Ui.Ink,true);
    private readonly TextBlock _count=Ui.Text("",12,Ui.Muted);
    private string _filter="ready";
    private string? _selected;
    private WindowsIntegration? _integration;
    private CaptureWindow? _capture;
    private ResumeStrip? _strip;
    private bool _quitting;
    private bool _trayHint;
    private readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly FocusSessionStore _focusStore;
    private string? _focusErrorId;
    private string? _focusCard => _focusStore.Current?.CardId;
    private bool _focusPaused => _focusStore.Current?.Paused==true;
    private TextBlock? _focusLabel;
    private readonly HashSet<string> _notifiedDue=new();
    private DateTimeOffset _lastDueCheck=DateTimeOffset.MinValue;

    public MainWindow(string directory)
    {
        Title="续页 · Relay — 接着做，不必重新找";Width=1180;Height=790;MinWidth=900;MinHeight=620;
        Background=Ui.Paper;Foreground=Ui.Ink;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _store=new VaultStore(directory);_document=_store.Load();Ui.Id(_status,"main.status");
        _focusStore=new FocusSessionStore(directory);try{_focusStore.Load();}catch(Exception error){Notice("专注状态未能读取："+error.Message);}
        _root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(196)});
        _root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(345)});
        _root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        BuildSidebar();BuildList();BuildDetailShell();Content=_root;
        _search.TextChanged+=(_,_)=>RenderList();
        PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.N){Capture();e.Handled=true;}else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.F){_search.Focus();e.Handled=true;}else if(e.Key==Key.Escape&&_capture==null){_search.Clear();}};
        AllowDrop=true;DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Link:DragDropEffects.None;e.Handled=true;};Drop+=(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)Capture(paths);};
        Loaded+=(_,_)=>{InitializeSystem();Render();};
        Closing+=OnClosing;
        Closed+=(_,_)=>{_clock.Stop();_integration?.Dispose();_strip?.Close();System.Windows.Application.Current.Shutdown();};
    }
    public void InitializeSystem()
    {
        if(_integration!=null)return;
        _integration=new WindowsIntegration(this,()=>Capture(background:true),ResumeLatest,ShowMain,Quit,_store.DirectoryPath);
        try{_integration.Initialize();}catch(Exception ex){Notice("系统入口部分未启用："+ex.Message);}
        _clock.Tick+=(_,_)=>Tick();_clock.Start();Render();
        if(!string.IsNullOrEmpty(_store.RecoveryNotice))Dispatcher.BeginInvoke(new Action(()=>{ShowMain();MessageBox.Show(this,_store.RecoveryNotice,"数据恢复提示",MessageBoxButton.OK,MessageBoxImage.Warning);}));
    }
    private void BuildSidebar()
    {
        var outer=new DockPanel{Background=new SolidColorBrush(Color.FromRgb(232,234,223)),LastChildFill=true};
        var brand=Ui.Stack(Ui.Text("↳  续页",29,Ui.Green,true),Ui.Text("RELAY / PICK UP WHERE YOU LEFT",8,Ui.Muted),new Border{Height=30},Ui.Text("不是更多待办，\n是少一点重新开始。",13,Ui.Muted));brand.Margin=new Thickness(23,30,18,24);DockPanel.SetDock(brand,Dock.Top);outer.Children.Add(brand);
        var bottom=Ui.Stack(Ui.Id(Ui.Button("空间与系统设置",ShowSettings),"main.settings"),new Border{Height=12},Ui.Text("Ctrl + Alt + Space\n随时留下一步",11,Ui.Muted),new Border{Height=12},Ui.Text("本机磁盘 · 无账号 · 不监控",10,Ui.Muted));bottom.Margin=new Thickness(18,15,10,24);DockPanel.SetDock(bottom,Dock.Bottom);outer.Children.Add(bottom);
        _navigation.Margin=new Thickness(12,0,12,0);outer.Children.Add(_navigation);Grid.SetColumn(outer,0);_root.Children.Add(outer);
    }
    private void BuildList()
    {
        var dock=new DockPanel{LastChildFill=true,Margin=new Thickness(22,27,19,18)};
        var top=Ui.Stack(Ui.Text("把下一步，留给未来的自己。",15,Ui.Ink,true),new Border{Height=14});
        var capture=Ui.Id(Ui.Button("＋ 留下续做卡",()=>Capture(),true),"main.capture");capture.HorizontalAlignment=HorizontalAlignment.Stretch;capture.Margin=new Thickness(0,0,0,16);top.Children.Add(capture);
        _search.ToolTip="搜索标题、下一步、上下文或资源名称 · Ctrl+F";top.Children.Add(_search);Ui.Gap(top,13);top.Children.Add(_count);Ui.Gap(top,12);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var scroll=new ScrollViewer{Content=_list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};dock.Children.Add(scroll);Grid.SetColumn(dock,1);_root.Children.Add(dock);
        var divider=new Border{Width=1,Background=Ui.Line,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(divider,1);_root.Children.Add(divider);
    }
    private void BuildDetailShell()
    {
        var dock=new DockPanel{Margin=new Thickness(30,30,30,18),LastChildFill=true};
        var foot=Ui.Stack(new Border{Height=10},_status);DockPanel.SetDock(foot,Dock.Bottom);dock.Children.Add(foot);
        var scroll=new ScrollViewer{Content=_detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};dock.Children.Add(scroll);Grid.SetColumn(dock,2);_root.Children.Add(dock);
    }
    private void Render()
    {
        RenderNavigation();RenderList();
        _status.Text=_store.IsReadOnly?"只读恢复模式 · 先导出或选择新仓库":"已写入磁盘 · "+_store.DataPath;
    }
    private void RenderNavigation()
    {
        _navigation.Children.Clear();
        foreach(var (key,label) in new[]{("ready","◉  回来继续"),("active","↗  正在进行"),("snoozed","◷  稍后回来"),("done","✓  已经完成"),("all","≡  全部续页")})
        {
            var b=Ui.Id(Ui.Button(label,()=>{_filter=key;_selected=null;Render();}),"nav."+key);b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,0,0,7);b.BorderThickness=new Thickness(0);b.Background=_filter==key?Ui.Green:Brushes.Transparent;b.Foreground=_filter==key?Brushes.White:Ui.Ink;_navigation.Children.Add(b);
        }
        Ui.Gap(_navigation,24);_navigation.Children.Add(Ui.Text("留下一句明确的下一步，\n比攒下一百条待办更有用。",11,Ui.Muted));
    }
    private IEnumerable<RelayCard> VisibleCards()
    {
        var now=DateTimeOffset.Now;var query=_search.Text.Trim();
        return _document.Cards.Where(c=>_filter switch{"ready"=>c.Status!="done"&&(!c.SnoozedUntil.HasValue||c.SnoozedUntil<=now),"active"=>c.Status=="active","snoozed"=>c.Status!="done"&&c.SnoozedUntil>now,"done"=>c.Status=="done",_=>true})
            .Where(c=>query.Length==0||(c.Title+" "+c.NextAction+" "+c.Context+" "+string.Join(" ",c.Resources.Select(r=>r.Label+" "+r.Target))).Contains(query,StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c=>c.Pinned).ThenByDescending(c=>c.Status=="active").ThenByDescending(c=>c.UpdatedAt);
    }
    private void RenderList()
    {
        _list.Children.Clear();var cards=VisibleCards().ToList();_count.Text=cards.Count+" 张续页 · 不必一次做完";
        if(_selected==null||!cards.Any(c=>c.Id==_selected))_selected=cards.FirstOrDefault()?.Id;
        foreach(var card in cards.Take(200))
        {
            var inner=Ui.Stack(Ui.Text((card.Pinned?"⌖ ":"")+card.Title,15,null,true),new Border{Height=8},Ui.Text(card.NextAction,12,Ui.Muted),new Border{Height=12});
            var meta=(card.Status=="active"?"正在继续":card.Status=="done"?"已完成":card.SnoozedUntil>DateTimeOffset.Now?"稍后 "+card.SnoozedUntil.Value.ToLocalTime().ToString("MM/dd HH:mm"):"等你回来");inner.Children.Add(Ui.Text(meta+"  ·  "+card.Resources.Count+" 个资源",10,card.Status=="active"?Ui.Accent:Ui.Muted));
            var b=new Button{Content=inner,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(16),Margin=new Thickness(0,0,0,10),Background=card.Id==_selected?new SolidColorBrush(Color.FromRgb(224,232,216)):Ui.Surface,BorderBrush=card.Id==_selected?Ui.Green:Ui.Line,BorderThickness=new Thickness(1),Cursor=Cursors.Hand};Ui.Id(b,"card."+card.Id);b.Click+=(_,_)=>{_selected=card.Id;RenderList();};_list.Children.Add(b);
        }
        if(cards.Count>200)_list.Children.Add(Ui.Text("列表显示前 200 张，请搜索定位其他续页。",11,Ui.Muted));
        if(cards.Count==0)_list.Children.Add(Ui.Panel(Ui.Stack(Ui.Text("这里留着一点空白。",17,null,true),new Border{Height=10},Ui.Text(_search.Text.Length>0?"没有匹配的续页，试试更短的关键词。":"下一次被打断时，按快捷键留下一句话。回来后，不用重新猜。",12,Ui.Muted))));
        RenderDetail();
    }
    private RelayCard? Selected=>_document.Cards.FirstOrDefault(c=>c.Id==_selected);
    private void RenderDetail()
    {
        _detail.Children.Clear();_focusLabel=null;var card=Selected;
        if(card==null){RenderWelcome();return;}
        _detail.Children.Add(Ui.Text("PICK UP WHERE YOU LEFT",10,Ui.Accent));Ui.Gap(_detail,9);
        _detail.Children.Add(Ui.Text(card.Title,28,Ui.Ink,true));Ui.Gap(_detail,7);
        _detail.Children.Add(Ui.Text("留下于 "+card.CreatedAt.ToLocalTime().ToString("MM月dd日 HH:mm")+"  ·  已回来 "+card.ResumeCount+" 次",11,Ui.Muted));Ui.Gap(_detail,23);
        var action=Ui.Stack(Ui.Text("回来后的第一步",11,Ui.Accent,true),new Border{Height=9},Ui.Text(card.NextAction,22,Ui.Green,true));
        if(card.Status!="done"){
            Ui.Gap(action,18);var row=new WrapPanel();row.Children.Add(Ui.Id(Ui.Button(card.Status=="active"?"↗ 展开续做条":"↗ 就从这里继续",()=>Resume(card.Id),true),"detail.resume"));row.Children.Add(Ui.Id(Ui.Button("留个进展",()=>Checkpoint(card.Id)),"detail.checkpoint"));action.Children.Add(row);
        }else{Ui.Gap(action,12);action.Children.Add(Ui.Text("这件事，已经告一段落。",12,Ui.Muted));}
        _detail.Children.Add(Ui.Panel(action));Ui.Gap(_detail,21);
        if(!string.IsNullOrWhiteSpace(card.Context)){_detail.Children.Add(Ui.Text("当时的思路",13,Ui.Ink,true));Ui.Gap(_detail,8);var context=Ui.Input(card.Context,true);context.IsReadOnly=true;context.MaxHeight=180;context.BorderThickness=new Thickness(0);context.Background=Brushes.Transparent;_detail.Children.Add(context);Ui.Gap(_detail,18);}
        var resourcesHeader=new DockPanel();resourcesHeader.Children.Add(Ui.Text("工作现场",13,Ui.Ink,true));_detail.Children.Add(resourcesHeader);Ui.Gap(_detail,7);
        _detail.Children.Add(Ui.Text("只保存路径，不复制文件。点击后才会打开；不存在的资源会标记出来。",10,Ui.Muted));Ui.Gap(_detail,10);
        if(card.Resources.Count==0)_detail.Children.Add(Ui.Text("还没有附加资源。编辑续页可添加文件、文件夹或网址。",12,Ui.Muted));
        foreach(var resource in card.Resources)
        {
            var exists=ResourceLauncher.Exists(resource);var row=new DockPanel{Margin=new Thickness(0,0,0,8)};
            var open=Ui.Button(exists?"打开":"更新",()=>{if(exists)ResourceLauncher.Open(resource,this);else Edit(card.Id);});open.FontSize=11;open.Padding=new Thickness(10,5,10,5);DockPanel.SetDock(open,Dock.Right);row.Children.Add(open);
            var data=Ui.Stack(Ui.Text((resource.Kind=="folder"?"▣ ":resource.Kind=="url"?"↗ ":"▤ ")+resource.Label,12,exists?Ui.Ink:Ui.Accent,true),Ui.Text(exists?ResourceLauncher.Explain(resource):"路径已失效 · "+resource.Target,10,Ui.Muted));data.ToolTip=resource.Target;row.Children.Add(data);_detail.Children.Add(Ui.Panel(row,new Thickness(12,8,8,8)));
        }
        Ui.Gap(_detail,20);
        if(card.Status=="active")
        {
            var focus=Ui.Stack(Ui.Text("给这一小步，一段不被打断的时间。",12,Ui.Muted));Ui.Gap(focus,10);
            _focusLabel=Ui.Text(FocusText(card.Id),23,Ui.Green,true);focus.Children.Add(_focusLabel);Ui.Gap(focus,8);var row=new WrapPanel();row.Children.Add(Ui.Id(Ui.Button(_focusCard==card.Id?(_focusPaused?"继续计时":"暂停计时"):"专注 "+_document.Settings.FocusMinutes+" 分钟",()=>ToggleFocus(card.Id)),"detail.focus"));if(_focusCard==card.Id)row.Children.Add(Ui.Button("结束计时",StopFocus));focus.Children.Add(row);_detail.Children.Add(Ui.Panel(focus));Ui.Gap(_detail,20);
        }
        var operations=new WrapPanel();operations.Children.Add(Ui.Id(Ui.Button("编辑",()=>Edit(card.Id)),"detail.edit"));operations.Children.Add(Ui.Button(card.Pinned?"取消置顶":"置顶",()=>Mutate(d=>d.Cards.First(c=>c.Id==card.Id).Pinned=!card.Pinned)));
        if(card.Status!="done"){operations.Children.Add(Ui.Id(Ui.Button("稍后回来",()=>Snooze(card.Id)),"detail.snooze"));operations.Children.Add(Ui.Id(Ui.Button("✓ 完成",()=>Complete(card.Id)),"detail.complete"));}else operations.Children.Add(Ui.Button("重新打开",()=>Mutate(d=>{var c=d.Cards.First(x=>x.Id==card.Id);c.Status="parked";c.CompletedAt=null;})));
        operations.Children.Add(Ui.Id(Ui.Button("导出卡片",()=>ExportCard(card)),"detail.export"));_detail.Children.Add(operations);Ui.Gap(_detail,23);
        _detail.Children.Add(Ui.Text("留下的进展",13,Ui.Ink,true));Ui.Gap(_detail,9);
        if(card.Checkpoints.Count==0)_detail.Children.Add(Ui.Text("完成一小步后，留下一句。下一次回来，就少一次回忆。",12,Ui.Muted));
        foreach(var check in card.Checkpoints.OrderByDescending(c=>c.At).Take(20)){_detail.Children.Add(Ui.Text(check.At.ToLocalTime().ToString("MM/dd HH:mm"),10,Ui.Accent));_detail.Children.Add(Ui.Text(check.Text,13));Ui.Gap(_detail,12);}
        Ui.Gap(_detail,20);var delete=Ui.Button("永久删除这张续页…",()=>Delete(card.Id));delete.Foreground=Ui.Muted;delete.BorderThickness=new Thickness(0);delete.Background=Brushes.Transparent;_detail.Children.Add(delete);
    }
    private void RenderWelcome()
    {
        _detail.Children.Add(Ui.Text("RELAY / A SMALL BRIDGE BACK",10,Ui.Accent));Ui.Gap(_detail,15);_detail.Children.Add(_heading);Ui.Gap(_detail,18);
        _detail.Children.Add(Ui.Text("你不缺另一个待办清单。\n你缺的是被打断之后，那句“我刚才做到哪儿了”。",17,Ui.Muted));Ui.Gap(_detail,28);
        foreach(var (n,title,text) in new[]{("01","离开前，留下一句","不是“继续做报告”，而是“核对第三页的收入数字”。"),("02","把工作现场放在旁边","附上正在用的文件、目录或链接，不复制、不监控。"),("03","回来后，接着这一小步","全局快捷键、托盘或任务栏都能叫回；续做条陪你待在真正的工作里。")})
        {var row=Ui.Stack(Ui.Text(n+" / "+title,16,Ui.Ink,true),new Border{Height=7},Ui.Text(text,13,Ui.Muted));_detail.Children.Add(Ui.Panel(row));Ui.Gap(_detail,12);}
        Ui.Gap(_detail,15);_detail.Children.Add(Ui.Button("留下我的第一张续页",()=>Capture(),true));Ui.Gap(_detail,20);
        _detail.Children.Add(Ui.Text("没有假数据，也没有连续打卡压力。\n真正用过的下一步，才值得留下。",11,Ui.Muted));
    }
    private bool Mutate(Action<VaultDocument> change,bool render=true)
    {
        try{if(_store.IsReadOnly)throw new InvalidOperationException("仓库处于只读恢复模式，请先导出并使用新的数据目录。");var candidate=VaultStore.Clone(_document);change(candidate);_store.Save(candidate);_document=candidate;if(render)Render();return true;}
        catch(Exception error){MessageBox.Show(_capture is {IsVisible:true}?_capture:this,"修改未写入，原有数据仍保留。\n\n"+error.Message,"保存未完成",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}
    }
    public void Capture(IEnumerable<string>? resources=null,bool background=false)
    {
        if(_capture!=null){if(resources!=null)_capture.AddResources(resources);_capture.Activate();return;}
        if(!background)ShowMain();_capture=new CaptureWindow(null,resources);
        if(!background&&IsVisible)_capture.Owner=this;else _capture.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _capture.SaveHandler=card=>Mutate(d=>d.Cards.Insert(0,card),false);
        try{if(_capture.ShowDialog()==true&&_capture.ResultCard is RelayCard card){_filter="ready";_selected=card.Id;Render();Notice("下一步已经写入磁盘。随时可以离开，回来接着做。");}}finally{_capture=null;}
    }
    private void Edit(string id)
    {
        if(_capture!=null)return;var card=_document.Cards.First(c=>c.Id==id);var original=System.Text.Json.JsonSerializer.Serialize(card);_capture=new CaptureWindow(card){Owner=this};
        _capture.SaveHandler=replacement=>{var current=_document.Cards.FirstOrDefault(c=>c.Id==id);if(current==null||System.Text.Json.JsonSerializer.Serialize(current)!=original){MessageBox.Show(_capture,"这张续页在编辑期间有了新进展。为避免覆盖，草稿尚未保存。请复制草稿后取消并重新打开编辑。","编辑冲突",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}return Mutate(d=>{var index=d.Cards.FindIndex(c=>c.Id==id);d.Cards[index]=replacement;},false);};
        try{if(_capture.ShowDialog()==true&&_capture.ResultCard is RelayCard replacement){Render();if(_strip!=null&&replacement.Status=="active")_strip.UpdateCard(replacement);}}finally{_capture=null;}
    }
    private void Resume(string id)
    {
        if(!Mutate(d=>{foreach(var other in d.Cards.Where(c=>c.Status=="active"&&c.Id!=id))other.Status="parked";var c=d.Cards.First(c=>c.Id==id);c.Status="active";c.SnoozedUntil=null;c.CompletedAt=null;c.LastResumedAt=DateTimeOffset.Now;c.ResumeCount++;c.UpdatedAt=DateTimeOffset.Now;},false))return;
        _selected=id;_filter="active";Render();ShowStrip(_document.Cards.First(c=>c.Id==id));_integration?.SetTaskbarState(Selected?.Title,0,false);
    }
    private void ShowStrip(RelayCard card)
    {
        _strip?.Close();_strip=new ResumeStrip(card,ShowMain,()=>{ShowMain();Checkpoint(card.Id);},()=>{ShowMain();Complete(card.Id);});_strip.Closed+=(_,_)=>_strip=null;_strip.Topmost=_document.Settings.AlwaysOnTopStrip;_strip.Show();
    }
    public void ResumeLatest()
    {
        ShowMain();var card=_document.Cards.Where(c=>c.Status!="done"&&(!c.SnoozedUntil.HasValue||c.SnoozedUntil<=DateTimeOffset.Now)).OrderByDescending(c=>c.Status=="active").ThenByDescending(c=>c.Pinned).ThenByDescending(c=>c.UpdatedAt).FirstOrDefault();if(card==null){Capture();return;}Resume(card.Id);
    }
    private void Checkpoint(string id)
    {
        var text=Ui.Prompt(this,"留个进展","刚完成了什么？哪条线索值得下次看到？",multiline:true,accept:value=>Mutate(d=>{var c=d.Cards.First(c=>c.Id==id);c.Checkpoints.Add(new Checkpoint{At=DateTimeOffset.Now,Text=value});c.UpdatedAt=DateTimeOffset.Now;}));
        if(text!=null)Notice("这一小步，记下来了。");
    }
    private void Complete(string id)
    {
        if(!Mutate(d=>{var c=d.Cards.First(c=>c.Id==id);c.Status="done";c.CompletedAt=DateTimeOffset.Now;c.SnoozedUntil=null;c.UpdatedAt=DateTimeOffset.Now;}))return;
        if(_focusCard==id)StopFocus();_strip?.Close();_integration?.SetTaskbarState(null,0,false);Notice("完成了。不是为了填满清单，是为了腾出空间。");
    }
    private void Snooze(string id)
    {
        var menu=new ContextMenu();foreach(var (label,until) in new[]{("一小时后",DateTimeOffset.Now.AddHours(1)),("今天晚些时候",DateTimeOffset.Now.AddHours(3)),("明天上午 9 点",new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)))}){var item=new MenuItem{Header=label};item.Click+=(_,_)=>{if(Mutate(d=>{var c=d.Cards.First(c=>c.Id==id);c.SnoozedUntil=until;c.Status="parked";c.UpdatedAt=DateTimeOffset.Now;})){_strip?.Close();if(_focusCard==id)StopFocus();Notice("会留在“稍后回来”，到时重新出现在恢复队列。仅应用运行时提醒。");}};menu.Items.Add(item);}menu.IsOpen=true;
    }
    private void Delete(string id)
    {
        if(MessageBox.Show(this,"这张续页及其进展将永久删除，关联的真实文件不会删除。\n\n可先导出卡片。确认删除？","永久删除续页",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        if(Mutate(d=>d.Cards.RemoveAll(c=>c.Id==id))){if(_focusCard==id)StopFocus();_strip?.Close();}
    }
    private void ExportCard(RelayCard card)
    {
        var dialog=new SaveFileDialog{Title="导出续页为 Markdown",Filter="Markdown 文件|*.md",FileName="续页-"+SafeFilename(card.Title)+".md"};if(dialog.ShowDialog(this)!=true)return;
        try{var sb=new StringBuilder("# "+card.Title+"\n\n## 下一步\n"+card.NextAction+"\n\n## 当时的思路\n"+card.Context+"\n\n## 资源\n");foreach(var r in card.Resources)sb.AppendLine("- "+r.Label+" — "+r.Target);sb.AppendLine("\n## 进展");foreach(var c in card.Checkpoints)sb.AppendLine("- "+c.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+" "+c.Text);AtomicExport.WriteText(dialog.FileName,sb.ToString(),_store.DirectoryPath);Notice("已导出："+dialog.FileName);}catch(Exception error){MessageBox.Show(this,error.Message,"导出失败");}
    }
    private static string SafeFilename(string text)=>string.Concat(text.Take(70).Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c)).TrimEnd('.',' ');
    private void Notice(string text){_status.Text=text;}
    public void ShowMain(){if(!IsVisible)Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();}
    public void HandleArguments(string[] args)
    {
        if(args.Contains("--resume")){ResumeLatest();return;}var resources=new List<string>();for(var i=0;i<args.Length-1;i++)if(args[i]=="--resource")resources.Add(args[++i]);
        if(args.Contains("--capture")||resources.Count>0)Dispatcher.BeginInvoke(new Action(()=>Capture(resources,true)));else ShowMain();
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(!_quitting&&_document.Settings.CloseToTray&&_integration?.HasTray==true){e.Cancel=true;Hide();if(!_trayHint){_trayHint=true;_integration.ShowNotification("续页还在托盘","Ctrl+Alt+Space 留下一步；托盘右键可完全退出。");}}
    }
    private void Quit(){if(_capture!=null){_capture.Close();if(_capture!=null&&_capture.IsVisible)return;}_quitting=true;_strip?.Close();Close();}
    private string FocusText(string cardId)
    {
        if(_focusCard!=cardId)return _document.Settings.FocusMinutes.ToString("00")+":00";
        var left=_focusStore.Remaining();return ((int)left.TotalMinutes).ToString("00")+":"+left.Seconds.ToString("00")+(_focusPaused?" · 暂停":_focusStore.Current?.CompletionPending==true?" · 待记录":"");
    }
    private void ToggleFocus(string cardId)
    {
        try
        {
            SettleFocus();
            if(_focusCard!=cardId)
            {
                if(_focusCard!=null&&MessageBox.Show(this,"已有一段计时，切换后将结束它（不计完成）。","切换专注",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
                _focusStore.Start(cardId,_document.Settings.FocusMinutes);
            }
            else if(_focusPaused)_focusStore.Resume();else _focusStore.Pause();
            _focusErrorId=null;RenderDetail();
        }
        catch(Exception error){MessageBox.Show(this,"专注状态未改变：\n"+error.Message,"专注未保存",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void StopFocus()
    {
        try{_focusStore.Cancel();_focusErrorId=null;_integration?.SetTaskbarState(Selected?.Title,0,false);RenderDetail();}
        catch(Exception error){Notice("计时未能结束，请重试："+error.Message);}
    }
    private void SettleFocus()
    {
        var session=_focusStore.Current;if(session==null||_focusErrorId==session.Id||!_focusStore.IsDue())return;
        try
        {
            if(!session.CompletionPending)_focusStore.MarkCompletionPending();
            session=_focusStore.Current!;var marker="[focus:"+session.Id+"]";
            var card=_document.Cards.FirstOrDefault(c=>c.Id==session.CardId);
            if(card!=null&&!card.Checkpoints.Any(c=>c.Text.Contains(marker,StringComparison.Ordinal)))
            {
                var candidate=VaultStore.Clone(_document);var updated=candidate.Cards.First(c=>c.Id==session.CardId);
                updated.Checkpoints.Add(new Checkpoint{At=session.Deadline??DateTimeOffset.Now,Text="完成一段 "+session.Minutes+" 分钟专注。 "+marker});updated.UpdatedAt=DateTimeOffset.Now;
                _store.Save(candidate);_document=candidate;
            }
            _focusStore.AcknowledgeCompletion();_focusErrorId=null;
            _integration?.SetTaskbarState(card?.Title,0,false);_integration?.ShowNotification("这一段专注完成了","进展已写入磁盘。起来走走，再继续。");
            if(_capture==null)Render();
        }
        catch(Exception error){_focusErrorId=session.Id;Notice("专注完成但尚未记录，状态已保留；下次启动重试。"+error.Message);}
    }
    private void Tick()
    {
        if(_focusStore.Current is { } session)
        {
            if(_focusLabel!=null)_focusLabel.Text=FocusText(session.CardId);
            _integration?.SetTaskbarState(_document.Cards.FirstOrDefault(c=>c.Id==session.CardId)?.Title,Math.Clamp(1-_focusStore.Remaining().TotalMinutes/session.Minutes,0,1),session.Paused);
            SettleFocus();
        }
        if(DateTimeOffset.Now-_lastDueCheck>TimeSpan.FromSeconds(30)){_lastDueCheck=DateTimeOffset.Now;var due=_document.Cards.Where(c=>c.Status!="done"&&c.SnoozedUntil<=DateTimeOffset.Now&&c.SnoozedUntil.HasValue&&!_notifiedDue.Contains(c.Id)).ToList();foreach(var c in due)_notifiedDue.Add(c.Id);if(due.Count>0){_integration?.ShowNotification("有 "+due.Count+" 张续页等你回来",due[0].NextAction);if(_capture==null)RenderList();}}
    }
    private void ShowSettings()
    {
        ShowMain();var window=new Window{Owner=this,Title="续页 · 空间与系统",Width=650,Height=690,MinHeight=520,Background=Ui.Paper,Foreground=Ui.Ink,FontFamily=FontFamily,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var stack=Ui.Stack(Ui.Text("小工具，应该住在你顺手的地方。",25,Ui.Ink,true),new Border{Height=10},Ui.Text("所有系统改动都由你主动启用，也可以在这里撤销。",12,Ui.Muted),new Border{Height=25});
        stack.Children.Add(Ui.Text("01 / 磁盘仓库",11,Ui.Accent));Ui.Gap(stack,7);var path=Ui.Input(_store.DirectoryPath);path.IsReadOnly=true;stack.Children.Add(path);Ui.Gap(stack,8);stack.Children.Add(Ui.Text("主文件 vault.json；每次成功更新保留上一版 .bak。文件未加密。建议定期导出到另一块磁盘。",11,Ui.Muted));Ui.Gap(stack,12);
        var data=new WrapPanel();data.Children.Add(Ui.Id(Ui.Button("打开数据文件夹",()=>ResourceLauncher.Open(new ResourceRef{Kind="folder",Target=_store.DirectoryPath,Label="数据"},window)),"settings.folder"));
        data.Children.Add(Ui.Id(Ui.Button("导出完整备份",()=>{var dialog=new SaveFileDialog{Filter="续页备份|*.json",FileName="续页备份-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".json"};if(dialog.ShowDialog(window)==true){try{_store.Export(dialog.FileName,_document);MessageBox.Show(window,"备份已保存。","续页");}catch(Exception error){MessageBox.Show(window,error.Message,"备份失败");}}}),"settings.export"));
        data.Children.Add(Ui.Id(Ui.Button("从备份恢复",()=>{var dialog=new OpenFileDialog{Filter="续页备份|*.json"};if(dialog.ShowDialog(window)!=true)return;try{var imported=_store.Import(dialog.FileName);if(MessageBox.Show(window,"备份包含 "+imported.Cards.Count+" 张续页，将替换当前内容。建议先导出当前仓库。\n\n确认恢复？","恢复备份",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;imported.Revision=_document.Revision;_store.Save(imported);_document=imported;_strip?.Close();StopFocus();Render();window.Close();}catch(Exception error){MessageBox.Show(window,"未恢复，现有数据未改变。\n"+error.Message,"恢复失败");}}),"settings.import"));stack.Children.Add(data);Ui.Gap(stack,22);
        stack.Children.Add(Ui.Text("02 / Windows 入口",11,Ui.Accent));Ui.Gap(stack,8);stack.Children.Add(Ui.Id(Ui.Text(_integration?.Status??"系统入口尚未初始化",12,Ui.Muted),"settings.integration"));Ui.Gap(stack,10);
        var shell=new WrapPanel();shell.Children.Add(Ui.Id(Ui.Button("启用文件右键入口",()=>{try{WindowsIntegration.InstallExplorerMenu(Environment.ProcessPath??throw new InvalidOperationException("无法找到程序路径"),_store.DirectoryPath);MessageBox.Show(window,"已为当前用户添加“留到续页”。Windows 11 可能位于“显示更多选项”。\n移动程序后请先撤销再重新启用。","右键入口");}catch(Exception error){MessageBox.Show(window,error.Message,"未启用");}}),"settings.installShell"));shell.Children.Add(Ui.Id(Ui.Button("撤销右键入口",()=>{try{WindowsIntegration.RemoveExplorerMenu();MessageBox.Show(window,"续页拥有的右键入口已撤销。","右键入口");}catch(Exception error){MessageBox.Show(window,error.Message,"未撤销");}}),"settings.removeShell"));stack.Children.Add(shell);Ui.Gap(stack,15);
        var tray=new CheckBox{Content="关闭主窗口时留在托盘（用托盘“退出”完全结束）",IsChecked=_document.Settings.CloseToTray,Margin=new Thickness(0,0,0,10)};tray.Click+=(_,_)=>Mutate(d=>d.Settings.CloseToTray=tray.IsChecked==true);stack.Children.Add(tray);
        var top=new CheckBox{Content="续做条保持在其他窗口上方",IsChecked=_document.Settings.AlwaysOnTopStrip,Margin=new Thickness(0,0,0,10)};top.Click+=(_,_)=>{if(Mutate(d=>d.Settings.AlwaysOnTopStrip=top.IsChecked==true)&&_strip!=null)_strip.Topmost=top.IsChecked==true;};stack.Children.Add(top);
        Ui.Gap(stack,15);stack.Children.Add(Ui.Text("03 / 自己的节奏",11,Ui.Accent));Ui.Gap(stack,8);var minutes=Ui.Input(_document.Settings.FocusMinutes.ToString());minutes.Width=80;minutes.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(Ui.Text("单次专注分钟（1–180）。暂停与计时状态写入磁盘，重启后继续；关闭期间不会弹通知。",11,Ui.Muted));Ui.Gap(stack,5);stack.Children.Add(minutes);Ui.Gap(stack,8);stack.Children.Add(Ui.Button("保存时长",()=>{if(!int.TryParse(minutes.Text,out var m)||m<1||m>180){MessageBox.Show(window,"请输入 1–180 的整数。");return;}Mutate(d=>d.Settings.FocusMinutes=m);}));Ui.Gap(stack,23);
        stack.Children.Add(Ui.Text("边界与隐私",13,Ui.Ink,true));Ui.Gap(stack,5);stack.Children.Add(Ui.Text("不记录屏幕、键盘或剪贴板历史；只有你明确粘贴的内容才保存。不扫描文件。资源是路径引用，不恢复未保存的应用状态。没有云端与手机同步。系统通知可能受勿扰模式影响。",12,Ui.Muted));Ui.Gap(stack,16);stack.Children.Add(Ui.Id(Ui.Button("关闭设置",()=>window.Close()),"settings.close"));
        window.Content=new ScrollViewer{Content=new Border{Padding=new Thickness(28),Child=stack},VerticalScrollBarVisibility=ScrollBarVisibility.Auto};window.ShowDialog();
    }
}
