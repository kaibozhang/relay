using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Relay.Desktop.Services;

namespace Relay.Desktop;
public sealed class DraftRecoveryWindow : Window
{
    public CaptureDraft? SelectedDraft {get;private set;}
    public DraftRecoveryWindow(Window owner,IReadOnlyList<CaptureDraft> drafts,string? warning,Func<string,bool> discard)
    {
        Owner=owner;Title="未提交草稿 · 续页";Width=640;Height=520;MinWidth=500;MinHeight=380;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Ui.Paper;Foreground=Ui.Ink;FontFamily=owner.FontFamily;
        var root=new DockPanel{Margin=new Thickness(24)};
        var header=Ui.Stack(Ui.Text("没来得及保存的，留在这里。",24,null,true),Ui.Text("仅恢复你主动写过的新卡草稿；不会覆盖已有续页，也不会打开资源。",12,Ui.Muted),new Border{Height=14});DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=Ui.Stack(Ui.Text("草稿是明文本地文件，不随卡片 JSON 导出。关闭本窗口不会删除草稿。",11,Ui.Muted),Ui.Id(Ui.Button("关闭",Close),"drafts.close"));DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var list=new StackPanel();if(!string.IsNullOrEmpty(warning)){list.Children.Add(Ui.Text(warning,12,Ui.Accent));Ui.Gap(list);}
        foreach(var draft in drafts)
        {
            var card=draft.Card;var label=!string.IsNullOrWhiteSpace(card.Title)?card.Title:!string.IsNullOrWhiteSpace(card.NextAction)?card.NextAction:"未命名草稿";
            var block=Ui.Stack(Ui.Text(label,16,null,true),Ui.Text("暂存于 "+draft.SavedAt.ToLocalTime().ToString("MM/dd HH:mm")+" · "+card.Resources.Count+" 个资源",11,Ui.Muted));Ui.Gap(block,10);
            var row=new WrapPanel();row.Children.Add(Ui.Id(Ui.Button("恢复编辑",()=>{SelectedDraft=draft;DialogResult=true;},true),"drafts.restore."+card.Id));
            var remove=Ui.Id(Ui.Button("放弃草稿",()=>{if(MessageBox.Show(this,"将删除这份未提交草稿，不能撤销。正式续页不受影响。","放弃草稿",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes&&discard(card.Id)){block.Visibility=Visibility.Collapsed;}}),"drafts.discard."+card.Id);row.Children.Add(remove);block.Children.Add(row);list.Children.Add(Ui.Panel(block));Ui.Gap(list,10);
        }
        if(drafts.Count==0)list.Children.Add(Ui.Text("没有可恢复的草稿。",15,Ui.Muted));
        root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=root;
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){Close();e.Handled=true;}};
    }
}
