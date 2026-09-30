using System;
using System.Diagnostics;
using System.Windows;
using Relay.Core;
namespace Relay.Desktop.Services;
public static class ResourceLauncher
{
    public static bool Exists(ResourceRef resource)=>resource.Kind=="url"||ResourceSafety.IsLocalNonRedirected(resource.Target,out _);
    public static string Explain(ResourceRef resource)=>ResourceOpenPolicy.Explain(resource);
    public static bool Open(ResourceRef resource,Window owner,bool reveal=false,bool confirm=true)
    {
        try
        {
            var plan=ResourceOpenPolicy.Plan(resource,reveal);
            if(confirm&&plan.RequiresConfirmation&&MessageBox.Show(owner,plan.Description+"：\n\n"+resource.Target,"打开资源",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return false;
            // Build arguments as separate values: references never become shell commands.
            var start=new ProcessStartInfo(plan.FileName){UseShellExecute=plan.UseShellExecute};foreach(var argument in plan.Arguments)start.ArgumentList.Add(argument);
            Process.Start(start);return true;
        }
        catch(Exception error){MessageBox.Show(owner,error.Message,"无法打开资源",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}
    }
}
