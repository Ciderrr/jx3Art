using System.IO;
using System.Runtime.InteropServices;
using Int1;
using Jx3.OneClickTest;
namespace JX3Artist;

internal sealed record WindowSnapshot(Box Outer,Box Visible,Box Client,uint Dpi,bool Maximized,bool Physical);

// Read-only APIs. No game/window changes or input calls.
internal static class WindowGeometryReader
{
    [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
    [DllImport("user32.dll",SetLastError=true)]static extern bool GetClientRect(nint window,out Win32RO.RECT rect);
    [DllImport("user32.dll",SetLastError=true)]static extern bool ClientToScreen(nint window,ref Point point);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]static extern bool IsZoomed(nint window);
    internal static WindowSnapshot Read(nint window)
    {
        if(!Win32RO.GetWindowRect(window,out var outer) || !GetClientRect(window,out var client))throw new IOException("无法读取窗口及客户区范围");
        var a=new Point{X=client.Left,Y=client.Top};var b=new Point{X=client.Right,Y=client.Bottom};
        if(!ClientToScreen(window,ref a) || !ClientToScreen(window,ref b))throw new IOException("无法读取客户区物理坐标");
        return new(new(outer.Left,outer.Top,outer.Right,outer.Bottom),Jx3.OneClickTest.Native.Bounds(window),new(a.X,a.Y,b.X,b.Y),GetDpiForWindow(window),IsZoomed(window),
            Win32RO.AreDpiAwarenessContextsEqual(Win32RO.GetThreadDpiAwarenessContext(),Win32RO.PMv2));
    }
}

// WGC window pixels map 1:1 to measured DWM visible bounds; do not multiply by DPI.
// This map validates geometry only. It never grants click or recognition permission.
internal sealed class WindowFrameMap
{
    internal WindowSnapshot Snapshot{get;}
    internal int Width{get;}internal int Height{get;}
    internal Box ClientInFrame{get;}
    static bool Valid(Box b)=>(long)b.X2-b.X>0 && (long)b.Y2-b.Y>0;
    static bool Contains(Box outer,Box inner)=>Valid(inner)&&inner.X>=outer.X&&inner.Y>=outer.Y&&inner.X2<=outer.X2&&inner.Y2<=outer.Y2;
    internal WindowFrameMap(WindowSnapshot before,WindowSnapshot after,int surfaceW,int surfaceH,int contentW,int contentH,long byteLength)
    {
        if(before!=after)throw new IOException("窗口位置、客户区、DPI或模式已变化，请等待新画面");
        if(!before.Physical || before.Dpi==0)throw new IOException("窗口物理像素信息未确认");
        if(!Valid(before.Outer)||!Valid(before.Visible)||!Contains(before.Outer,before.Visible)||!Contains(before.Visible,before.Client))throw new IOException("窗口与客户区范围不一致");
        long w=(long)before.Visible.X2-before.Visible.X,h=(long)before.Visible.Y2-before.Visible.Y;
        if(contentW<=0||contentH<=0||w!=contentW||h!=contentH||surfaceW!=contentW||surfaceH!=contentH||w*h>32000000||byteLength!=w*h*4)
            throw new IOException("采集表面、内容与窗口物理尺寸不一致");
        Snapshot=before;Width=contentW;Height=contentH;
        ClientInFrame=new(before.Client.X-before.Visible.X,before.Client.Y-before.Visible.Y,before.Client.X2-before.Visible.X,before.Client.Y2-before.Visible.Y);
    }
    internal bool Matches(WindowSnapshot current)=>Snapshot==current;
    internal void EnsureCurrent(WindowSnapshot current){if(!Matches(current))throw new IOException("窗口几何信息已变化，旧画面坐标已失效");}
    internal Pointer ScreenCenter(Box box,WindowSnapshot current)
    {
        EnsureCurrent(current);
        if(!Contains(new(0,0,Width,Height),box)||!Contains(ClientInFrame,box))throw new IOException("目标超出游戏客户区，未准备点击");
        return new(checked(Snapshot.Visible.X+box.X+box.Width/2),checked(Snapshot.Visible.Y+box.Y+box.Height/2));
    }
}
