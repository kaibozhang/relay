using System;
using System.IO;
using System.Linq;
namespace Relay.Desktop.Services;
public static class ResourceSafety
{
    // Check each ancestor before probing the next component; never follow network mappings or links.
    public static bool IsLocalNonRedirected(string target,out string reason,bool allowMissingLeaf=false)
    {
        reason="";
        try
        {
            if(string.IsNullOrWhiteSpace(target)||target.Length<3||!char.IsAsciiLetter(target[0])||target[1]!=':'||(target[2]!='\\'&&target[2]!='/')||target.StartsWith(@"\\")||target.IndexOf(':',2)>=0||target.Any(char.IsControl))
            {reason="不是受支持的本地绝对路径";return false;}
            var full=Path.GetFullPath(target);var root=Path.GetPathRoot(full)!;var drive=new DriveInfo(root);
            if(drive.DriveType!=DriveType.Fixed&&drive.DriveType!=DriveType.Removable){reason="网络或未验证磁盘：不会自动访问";return false;}
            var parts=full[root.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries);var current=root;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0){reason="重定向根目录：不会自动访问";return false;}
            for(var i=0;i<parts.Length;i++)
            {
                current=Path.Combine(current,parts[i]);FileAttributes attributes;
                try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){if(allowMissingLeaf&&i==parts.Length-1)return true;reason="路径已移动或不存在";return false;}catch(DirectoryNotFoundException){reason="路径已移动或不存在";return false;}
                if((attributes&FileAttributes.ReparsePoint)!=0){reason="路径包含符号链接或重定向目录：请添加真实本地路径";return false;}
            }
            return true;
        }
        catch(UnauthorizedAccessException){reason="没有读取此路径的权限";return false;}
        catch(IOException){reason="磁盘暂不可用";return false;}
        catch(ArgumentException){reason="路径格式无效";return false;}
        catch(NotSupportedException){reason="不支持此路径";return false;}
    }
}
