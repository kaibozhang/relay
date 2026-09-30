using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace Relay.Desktop.Services;
// Opt-in test evidence: only this process's WPF visual trees, never desktop or other applications.
public static class DiagnosticSnapshots
{
    private static string? _directory;
    private static DispatcherTimer? _timer;
    public static void Enable(string directory)
    {
        _directory=Path.GetFullPath(directory);Directory.CreateDirectory(_directory);
        _timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};_timer.Tick+=(_,_)=>CaptureAll();_timer.Start();
    }
    public static void CaptureAll()
    {
        if(_directory==null)return;
        foreach(Window window in System.Windows.Application.Current.Windows)
        {
            if(!window.IsVisible||window.Content is not FrameworkElement root||root.ActualWidth<1||root.ActualHeight<1)continue;
            try
            {
                var width=(int)Math.Ceiling(root.ActualWidth);var height=(int)Math.Ceiling(root.ActualHeight);if(width>4096||height>4096)continue;
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(window.Background??Brushes.White,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name=window.GetType().Name;if(name=="Window")name="Dialog-"+string.Concat(window.Title.Split(Path.GetInvalidFileNameChars()));
                var path=Path.Combine(_directory,name+".png");var temp=path+".tmp";using(var stream=File.Create(temp))encoder.Save(stream);File.Move(temp,path,true);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException){ }
        }
    }
}
