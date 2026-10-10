using System;
using System.IO;
using System.Linq;
using System.Windows;
using Relay.Desktop.Services;

namespace Relay.Desktop;
public sealed class App : System.Windows.Application
{
    private SingleInstance? _single;
    private FileStream? _vaultLock;
    [STAThread] public static void Main(string[] args)
    {
        var app=new App();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("Theme.xaml",UriKind.Relative)});app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException+=(_,e)=>{MessageBox.Show("这一步没有完成，原有磁盘数据不会因此被清空。\n\n"+e.Exception.Message,"续页 · 操作未完成",MessageBoxButton.OK,MessageBoxImage.Warning);e.Handled=true;};
        try{app.Start(args);if(app.MainWindow!=null)app.Run();}
        catch(Exception error){MessageBox.Show("续页未能启动：\n"+error.Message+"\n\n可使用 --vault 指定有写入权限的本地文件夹。","续页",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{app._vaultLock?.Dispose();app._single?.Dispose();}
    }
    private void Start(string[] args)
    {
        var diagnostics=Array.IndexOf(args,"--diagnostic-snapshots");if(diagnostics>=0){if(diagnostics+1>=args.Length)throw new ArgumentException("诊断截图需要明确的输出目录。");DiagnosticSnapshots.Enable(args[diagnostics+1]);args=args.Where((_,i)=>i!=diagnostics&&i!=diagnostics+1).ToArray();}
        var vault=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Relay","Vault");
        var portable=Path.Combine(AppContext.BaseDirectory,"portable.txt");if(File.Exists(portable))vault=Path.Combine(AppContext.BaseDirectory,"Data");
        var index=Array.IndexOf(args,"--vault");if(index>=0){if(index+1>=args.Length)throw new ArgumentException("--vault 后需要完整文件夹路径。");vault=Path.GetFullPath(args[index+1]);}
        var forwarded=args.Where((_,i)=>index<0||i!=index&&i!=index+1).ToArray();
        var sendto=Array.IndexOf(forwarded,"--sendto");
        if(sendto>=0){var normalized=forwarded.Take(sendto).ToList();foreach(var resource in forwarded.Skip(sendto+1)){if(!Path.IsPathFullyQualified(resource))throw new ArgumentException("发送到入口只接受完整文件路径。");normalized.Add("--resource");normalized.Add(resource);}forwarded=normalized.ToArray();}
        MainWindow? window=null;
        _single=new SingleInstance();
        if(!_single.Acquire(Path.TrimEndingDirectorySeparator(Path.GetFullPath(vault)).ToUpperInvariant(),forwarded,received=>Dispatcher.BeginInvoke(new Action(()=>window?.HandleArguments(received)))))return;
        Directory.CreateDirectory(vault);
        try{_vaultLock=new FileStream(Path.Combine(vault,".relay.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new InvalidOperationException("这个数据目录已被另一个续页进程占用（可能使用了不同路径别名）。请回到已有窗口，不要同时打开同一仓库。");}
        window=new MainWindow(vault);MainWindow=window;window.InitializeSystem();
        if(!forwarded.Contains("--capture")&&!forwarded.Contains("--resource")&&!forwarded.Contains("--resume"))window.Show();window.HandleArguments(forwarded);
    }
}
