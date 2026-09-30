using System;
using System.IO;
using System.Text;
namespace Relay.Desktop.Services;
public static class AtomicExport
{
    public static void WriteText(string destination,string text,string vaultDirectory)
    {
        var full=Path.GetFullPath(destination);var vault=Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultDirectory));
        if(!ResourceSafety.IsLocalNonRedirected(vault,out var vaultReason))throw new InvalidOperationException("数据目录无法安全验证："+vaultReason);
        var parent=Path.GetDirectoryName(full)??throw new InvalidOperationException("无效导出目录。");
        if(!ResourceSafety.IsLocalNonRedirected(parent,out var parentReason))throw new InvalidOperationException("导出目录无法安全验证："+parentReason);
        if(File.Exists(full)&&!ResourceSafety.IsLocalNonRedirected(full,out var targetReason))throw new InvalidOperationException("导出目标无法安全验证："+targetReason);
        // Reserve backing-data basenames everywhere, including short-path and subst aliases.
        {
            var name=Path.GetFileName(full);
            if(name.Contains('~'))throw new InvalidOperationException("导出文件名不能使用带 ~ 的短路径别名，请选择明确的新文件名。");
            if(name.Equals(".relay.lock",StringComparison.OrdinalIgnoreCase)||name.Equals("vault.json",StringComparison.OrdinalIgnoreCase)||name.StartsWith("vault.json.",StringComparison.OrdinalIgnoreCase)||name.Equals("focus.json",StringComparison.OrdinalIgnoreCase)||name.StartsWith("focus.json.",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("不能用导出覆盖续页的数据、备份或恢复证据文件。请选择其他文件名或目录。");
        }
        var directory=Path.GetDirectoryName(full)??throw new InvalidOperationException("无效导出目录。");
        if(!Directory.Exists(directory))throw new DirectoryNotFoundException("导出目录不存在。");
        var temporary=Path.Combine(directory,".relay-export-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {var bytes=new UTF8Encoding(false,true).GetBytes(text);file.Write(bytes);file.Flush(true);}
            if(File.Exists(full))File.Replace(temporary,full,null,true);else File.Move(temporary,full);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
