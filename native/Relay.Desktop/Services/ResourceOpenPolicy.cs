using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Relay.Core;
namespace Relay.Desktop.Services;
public sealed record ResourceOpenPlan(string FileName,IReadOnlyList<string> Arguments,bool UseShellExecute,bool RequiresConfirmation,string Description,string OpenKind);
public static class ResourceOpenPolicy
{
    private static readonly HashSet<string> Documents=new(StringComparer.OrdinalIgnoreCase){".txt",".md",".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".csv",".png",".jpg",".jpeg",".gif",".webp",".bmp",".log",".json",".xml",".yaml",".yml",".cs",".ts",".css"};
    public static string Explain(ResourceRef resource)=>resource.Kind=="url"?"在默认浏览器打开":resource.Kind=="folder"?"在资源管理器打开":Documents.Contains(Path.GetExtension(resource.Target))?"使用默认应用打开；不会恢复未保存编辑":"为安全起见，只定位文件，不执行";
    public static ResourceOpenPlan Plan(ResourceRef resource,bool reveal=false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if(resource.Target==null||resource.Target.Length>32767||resource.Target.Any(char.IsControl))throw new InvalidDataException("资源地址无效。");
        if(resource.Kind=="url")
        {
            if(!Uri.TryCreate(resource.Target,UriKind.Absolute,out var uri)||!(uri.Scheme=="http"||uri.Scheme=="https")||string.IsNullOrEmpty(uri.Host)||!string.IsNullOrEmpty(uri.UserInfo))throw new InvalidDataException("只允许不含登录凭据的 HTTP/HTTPS 地址。");
            return new ResourceOpenPlan(uri.AbsoluteUri,Array.Empty<string>(),true,true,"将使用默认浏览器打开网址", "url");
        }
        if(resource.Kind!="file"&&resource.Kind!="folder")throw new InvalidDataException("不支持的资源类型。");
        if(!ResourceSafety.IsLocalNonRedirected(resource.Target,out var reason))throw new InvalidDataException(reason);
        var full=Path.GetFullPath(resource.Target);var folder=Directory.Exists(full);
        if(!folder&&!File.Exists(full))throw new FileNotFoundException("资源已移动或不存在。",full);
        if(resource.Kind=="folder"&&!folder)throw new InvalidDataException("原文件夹已经变成文件，请重新确认资源。");
        if(resource.Kind=="file"&&folder)throw new InvalidDataException("原文件已经变成文件夹，请重新确认资源。");
        if(reveal||!folder&&!Documents.Contains(Path.GetExtension(full)))
            return new ResourceOpenPlan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),new[]{"/select,"+full},false,false,"只定位文件，不执行", "reveal");
        return new ResourceOpenPlan(full,Array.Empty<string>(),true,!folder,folder?"在资源管理器打开文件夹":"交给系统默认应用打开；请只打开可信文档",folder?"folder":"document");
    }
}
