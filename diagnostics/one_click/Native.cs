using System.Runtime.InteropServices;
using Int1;
namespace Jx3.OneClickTest;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]internal struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]internal struct Mouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)]internal struct Input{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public Mouse Mouse;}
    [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]internal static extern IntPtr GetAncestor(IntPtr window,uint flag);
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int metric);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,uint attr,out Win32RO.RECT bounds,int size);
    internal static Box Bounds(IntPtr window)
    {
        if(DwmGetWindowAttribute(window,9,out var r,Marshal.SizeOf<Win32RO.RECT>())!=0)throw new IOException("DWM bounds unavailable");
        return new(r.Left,r.Top,r.Right,r.Bottom);
    }
    internal static bool Cancelled=>(GetAsyncKeyState(27)&0x8000)!=0;
    internal static bool ButtonHeld=>new[]{1,2,4,5,6}.Any(k=>(GetAsyncKeyState(k)&0x8000)!=0);
    internal static Box Desktop(){int x=GetSystemMetrics(76),y=GetSystemMetrics(77);return new(x,y,x+GetSystemMetrics(78),y+GetSystemMetrics(79));}
    internal static Input[] PressInputs(int x,int y,Box desktop)
    {
        var p=ClickPolicy.Normalize(x,y,desktop);
        return new[]{new Input{Mouse=new Mouse{X=p.X,Y=p.Y,Flags=0xC001}},new Input{Mouse=new Mouse{Flags=2}}};
    }
    internal static int LastPressError,LastReleaseError;
    internal static uint? LastPressSent,LastReleaseSent;
    internal static uint Press(int x,int y)
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf<Input>()!=40)throw new InvalidOperationException("unsupported INPUT layout");
        uint sent=SendInput(2,PressInputs(x,y,Desktop()),40);LastPressError=sent==2?0:Marshal.GetLastWin32Error();LastPressSent=sent;return sent;
    }
    internal static uint Release(){uint sent=SendInput(1,new[]{new Input{Mouse=new Mouse{Flags=4}}},40);LastReleaseError=sent==1?0:Marshal.GetLastWin32Error();LastReleaseSent=sent;return sent;}
}
