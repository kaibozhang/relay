using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Relay.Core;
namespace Relay.Desktop.Services;
public static class ResourceLauncher
{
    private static readonly HashSet<string> DocumentExtensions=new(StringComparer.OrdinalIgnoreCase){".txt",".md",".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".csv",".png",".jpg",".jpeg",".gif",".webp",".bmp",".log",".json",".xml",".yaml",".yml",".cs",".ts",".css"};
    public static bool Exists(ResourceRef resource)=>resource.Kind=="url"||ResourceSafety.IsLocalNonRedirected(resource.Target,out _);
    public static string Explain(ResourceRef r)=>r.Kind=="url"?"在默认浏览器打开":r.Kind=="folder"?"在资源管理器打开":DocumentExtensions.Contains(Path.GetExtension(r.Target))?"使用默认应用打开；不会恢复未保存编辑":"为安全起见，只定位文件，不执行";
    public static void Open(ResourceRef r,Window owner,bool reveal=false)
    {
        try
        {
            if(r.Kind=="url")
            {
                if(!Uri.TryCreate(r.Target,UriKind.Absolute,out var uri)||!(uri.Scheme=="http"||uri.Scheme=="https")||!string.IsNullOrEmpty(uri.UserInfo))throw new InvalidDataException("只允许不含登录凭据的 HTTP/HTTPS 地址。");
                if(MessageBox.Show(owner,"将使用默认浏览器打开：\n\n"+uri.AbsoluteUri,"打开网页",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});return;
            }
            if(!ResourceSafety.IsLocalNonRedirected(r.Target,out var reason))throw new InvalidDataException(reason);
            var full=Path.GetFullPath(r.Target);
            if(full.StartsWith(@"\")||full.StartsWith("//")||full.Length<3||full[1]!=':'||full[2]!=Path.DirectorySeparatorChar||full.IndexOf(':',2)>=0)throw new InvalidDataException("只允许本地绝对文件路径。");
            if(!File.Exists(full)&&!Directory.Exists(full))throw new FileNotFoundException("资源已移动或不存在。请编辑续做卡，更新路径。",full);
            // Resolve link-like/reparse targets conservatively. Do not follow them into arbitrary executables.
            var attr=File.GetAttributes(full);
            bool unsafeFile=!Directory.Exists(full)&&(!DocumentExtensions.Contains(Path.GetExtension(full))||(attr&FileAttributes.ReparsePoint)!=0);
            if(reveal||unsafeFile){var info=new ProcessStartInfo("explorer.exe"){UseShellExecute=false};info.ArgumentList.Add("/select,"+full);Process.Start(info);return;}
            if(!Directory.Exists(full)&&MessageBox.Show(owner,"将交给系统默认应用打开：\n\n"+full+"\n\n请只打开你信任的文件。","打开文件",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
            Process.Start(new ProcessStartInfo(full){UseShellExecute=true});
        }
        catch(Exception error){MessageBox.Show(owner,error.Message,"无法打开资源",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
