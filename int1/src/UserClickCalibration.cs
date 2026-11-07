using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Int1;

// Explicit one-shot USER input observation. No hooks, injected input or window writes.
internal static class UserClickCalibration
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attr, out Win32RO.RECT rect, int size);

    internal record Snapshot(byte[] Pixels, int Width, int Height, double Source, Win32RO.RECT Bounds);
    internal record Selection(string Error, int X, int Y);
    internal sealed class ButtonEdge(bool initialDown)
    {
        bool previous=initialDown;
        internal bool Sample(bool down) {bool edge=down&&!previous;previous=down;return edge;}
    }
    internal static Selection Map(Snapshot frame, Win32RO.RECT bounds, int x, int y, double now)
    {
        if (frame == null) return new("no_pre_click_frame", 0, 0);
        if (!double.IsFinite(now) || !double.IsFinite(frame.Source) || frame.Source <= 0 || now < frame.Source || now-frame.Source > 500)
            return new("pre_click_frame_not_fresh", 0, 0);
        if (bounds.Left != frame.Bounds.Left || bounds.Top != frame.Bounds.Top || bounds.Right != frame.Bounds.Right || bounds.Bottom != frame.Bounds.Bottom)
            return new("window_geometry_changed", 0, 0);
        if (frame.Width != bounds.Width || frame.Height != bounds.Height || frame.Width != 3840 || frame.Height != 2088)
            return new("capture_bounds_unverified_or_unsupported", 0, 0);
        long px = (long)x-bounds.Left, py = (long)y-bounds.Top;
        if (px < 0 || py < 0 || px >= frame.Width || py >= frame.Height) return new("click_outside_capture",0,0);
        return new(null,(int)px,(int)py);
    }
    static Win32RO.RECT Bounds(IntPtr window)
    {
        if (DwmGetWindowAttribute(window,9,out var bounds,Marshal.SizeOf<Win32RO.RECT>()) != 0)
            throw new IOException("DWM capture bounds unavailable; no guessed scaling");
        return bounds;
    }
    static string Arg(string[] args,string name,string fallback)
    { int i=Array.IndexOf(args,name);return i>=0 && i+1<args.Length?args[i+1]:fallback; }

    public static int Command(string[] args)
    {
        if (!args.Contains("--i-authorize-live")) { Console.WriteLine("learn-click requires explicit live authorization");return 4; }
        if (!int.TryParse(Arg(args,"--seconds","60"),out int seconds) || seconds<10 || seconds>120)
            throw new ArgumentException("seconds must be 10..120");
        string output=Path.GetFullPath(Arg(args,"--out","output/user-click-"+DateTime.Now.ToString("yyyyMMdd_HHmmss")));
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("choose a new output directory");
        var (window,pid,error)=Win32RO.LocateGame();
        if (window==IntPtr.Zero) throw new IOException("game not found: "+error);
        using var engine=new WgcEngine();
        string startError=engine.Start(window);
        if(startError!=null) throw new IOException(startError);
        Directory.CreateDirectory(output);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Snapshot latest=null;
        int polls=0,downSamples=0,edges=0,foregroundEdges=0,targetEdges=0,capturedFrames=0,failedFrames=0;
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};
        Console.CancelKeyPress+=cancel;
        Console.WriteLine($"[learn-click] {seconds}s: 手动在游戏中单击紫色舞者一次。仅记录这次左键与点击前画面，不发送输入、不监听键盘、不保存配置。Ctrl+C 停止。输出 {output}");
        // Poll separately so pixel readback cannot hide a short mouse press.
        var observer=Task.Run(()=>
        {
            var button=new ButtonEdge((GetAsyncKeyState(1)&0x8000)!=0);
            double lastPoll=FrameTiming.NowMs();
            while(!stop.IsCancellationRequested)
            {
                double now=FrameTiming.NowMs();bool down=(GetAsyncKeyState(1)&0x8000)!=0;
                polls++;if(down)downSamples++;
                bool edge=button.Sample(down);if(edge)edges++;
                double gap=now-lastPoll;lastPoll=now;
                bool foreground=edge && GetForegroundWindow()==window;if(foreground)foregroundEdges++;
                if(foreground && GetCursorPos(out var point) && GetAncestor(WindowFromPoint(point),2)==window)
                {
                    targetEdges++;
                    Win32RO.GetWindowThreadProcessId(window,out uint currentPid);
                    var frame=Volatile.Read(ref latest);
                    var selected=currentPid!=pid ? new Selection("target_identity_changed",0,0) :
                        gap>50 ? new Selection("poll_gap_position_uncertain",0,0) : Map(frame,Bounds(window),point.X,point.Y,now);
                    return (Frame:frame,Selected:selected,Point:point,Time:now,Gap:gap,Utc:DateTime.UtcNow);
                }
                Thread.Sleep(5);
            }
            return (Frame:(Snapshot)null,Selected:new Selection("timeout_or_cancelled",0,0),Point:new Point(),Time:FrameTiming.NowMs(),Gap:0d,Utc:DateTime.UtcNow);
        });
        try
        {
            while(!observer.IsCompleted && !stop.IsCancellationRequested)
            {
                if(!Win32RO.IsWindow(window)) throw new IOException("target_lost");
                Win32RO.GetWindowThreadProcessId(window,out uint currentPid);
                if(currentPid!=pid) throw new IOException("target_identity_changed");
                var before=Bounds(window);
                var frame=engine.PullLatest(80);
                var after=Bounds(window);
                if(frame.Ok && !frame.RecreatedThisPull && frame.ContentW==frame.SurfaceW && frame.ContentH==frame.SurfaceH &&
                    before.Left==after.Left && before.Top==after.Top && before.Right==after.Right && before.Bottom==after.Bottom)
                { capturedFrames++;Volatile.Write(ref latest,new Snapshot(frame.Bgra,frame.SurfaceW,frame.SurfaceH,frame.FrameQpcMs,after)); }
                else {failedFrames++;Volatile.Write(ref latest,null);}
                Thread.Sleep(25);
            }
            var result=observer.GetAwaiter().GetResult();
            string imagePath=Path.Combine(output,"before_click.bmp");
            if(result.Selected.Error==null) BitmapFrameTransport.SaveBmp(result.Frame.Pixels,result.Frame.Width,result.Frame.Height,imagePath);
            var record=new {schema="jx3-user-click-observation-v1",origin="user_actual_left_button_poll",recordedUtc=result.Utc,
                targetPid=pid,windowHandle=window.ToInt64(),status=result.Selected.Error??"RECORDED_UNCONFIRMED",
                screenPoint=new[]{result.Point.X,result.Point.Y},framePoint=result.Selected.Error==null?new[]{result.Selected.X,result.Selected.Y}:null,
                frameSize=result.Frame==null?null:new[]{result.Frame.Width,result.Frame.Height},sourceQpcMs=result.Frame?.Source,clickSampleQpcMs=result.Time,
                pollGapMs=result.Gap,framePath=result.Selected.Error==null?imagePath:null,
                diagnostics=new{polls,downSamples,edges,foregroundEdges,targetEdges,capturedFrames,failedFrames},
                frameSha256=result.Selected.Error==null?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(imagePath))).ToLowerInvariant():null,confirmed_by_user=false,
                caveat="Polled left-button state, not OS event timestamp; manual target identity must be confirmed; no automatic input or profile binding"};
            File.WriteAllText(Path.Combine(output,"click.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"[learn-click] {record.status}; {output}");
            return result.Selected.Error==null?0:12;
        }
        finally {stop.Cancel();try{observer.GetAwaiter().GetResult();}finally{Console.CancelKeyPress-=cancel;}}
    }
}
