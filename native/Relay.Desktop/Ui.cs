using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace Relay.Desktop;
public static class Ui
{
    public static readonly Brush Ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(35,54,47));
    public static readonly Brush Muted = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(103,112,103));
    public static readonly Brush Paper = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(244,243,236));
    public static readonly Brush Surface = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(253,252,247));
    public static readonly Brush Green = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(28,65,51));
    public static readonly Brush Accent = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(150,81,34));
    public static readonly Brush Line = SystemParameters.HighContrast ? SystemColors.ActiveBorderBrush : new SolidColorBrush(Color.FromRgb(217,222,208));
    public static TextBlock Text(string text,double size=14,Brush? color=null,bool bold=false) => new TextBlock {Text=text,FontSize=size,Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:color??Ink,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.5};
    public static Button Button(string label,Action click,bool primary=false)
    {
        var b=new Button{Content=label,Padding=new Thickness(15,9,15,9),Margin=new Thickness(0,0,8,0),MinHeight=36,Background=primary?Green:Surface,Foreground=primary?(SystemParameters.HighContrast?SystemColors.HighlightTextBrush:Brushes.White):Ink,BorderBrush=primary?Green:Line,BorderThickness=new Thickness(1),Cursor=System.Windows.Input.Cursors.Hand};
        b.Click+=(_,_)=>click();return b;
    }
    public static TextBox Input(string text="",bool multiline=false)=>new TextBox{Text=text,Padding=new Thickness(10),Background=Surface,Foreground=Ink,BorderBrush=Line,BorderThickness=new Thickness(1),AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden,MinHeight=38,FontSize=14};
    public static Border Panel(UIElement child,Thickness? padding=null)=>new Border{Child=child,Padding=padding??new Thickness(20),Background=Surface,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9)};
    public static StackPanel Stack(params UIElement[] children){var s=new StackPanel();foreach(var c in children)s.Children.Add(c);return s;}
    public static T Id<T>(T element,string id) where T:DependencyObject {AutomationProperties.SetAutomationId(element,id);return element;}
    public static void Gap(Panel parent,double height=14)=>parent.Children.Add(new Border{Height=height});
    public static string? Prompt(Window owner,string title,string instruction,string initial="",bool multiline=false,Func<string,bool>? accept=null)
    {
        var window=new Window{Title=title,Owner=owner,Width=520,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Paper,Foreground=Ink,FontFamily=new FontFamily("Microsoft YaHei UI"),ShowInTaskbar=false};
        var field=Id(Input(initial,multiline),"prompt.input");field.MaxLength=2000;if(multiline)field.Height=120;
        var stack=Stack(Text(title,23,null,true),Text(instruction,12,Muted),new Border{Height=15},field,new Border{Height=18});
        var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};string? result=null;
        row.Children.Add(Button("取消",()=>window.DialogResult=false));row.Children.Add(Id(Button("确定",()=>{if(string.IsNullOrWhiteSpace(field.Text)){field.Focus();return;}var value=field.Text.Trim();try{if(accept!=null&&!accept(value))return;}catch(Exception error){MessageBox.Show(window,error.Message,"尚未保存",MessageBoxButton.OK,MessageBoxImage.Warning);return;}result=value;window.DialogResult=true;},true),"prompt.confirm"));stack.Children.Add(row);window.Content=new Border{Padding=new Thickness(26),Child=stack};window.Loaded+=(_,_)=>{field.Focus();field.SelectAll();};window.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){window.DialogResult=false;e.Handled=true;}};
        window.ShowDialog();return result;
    }
}
