using System.Runtime.InteropServices;
using Int1;
using InputNative=Jx3.OneClickTest.Native;
namespace JX3Artist;

internal sealed class Desktop(uint gamePid=0):IDesktop
{
    [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct LastInput{public uint Size,Time;}
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW",ExactSpelling=true)]static extern bool ReadSystemParameter(uint action,uint parameter,out uint result,uint flags);
    [DllImport("user32.dll")]internal static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]internal static extern bool UnregisterHotKey(nint window,int id);
    internal const int HotkeyId=0x4A58;
    public nint Foreground=>InputNative.GetForegroundWindow();
    public Pointer Cursor=>GetCursorPos(out var p)?new(p.X,p.Y):throw new InvalidOperationException("无法读取鼠标位置");
    public uint InputTick{get{var info=new LastInput{Size=(uint)Marshal.SizeOf<LastInput>()};if(!GetLastInputInfo(ref info))throw new InvalidOperationException("无法读取空闲状态");return info.Time;}}
    uint? RequiredIdle
    {
        get{nint foreground=Foreground;if(foreground==0)return null;uint pid=Pid(foreground);bool owned=pid!=0 && (pid==(uint)Environment.ProcessId || pid==gamePid);uint? timeout=owned?0u:ReadSystemParameter(0x2000,0,out uint value,0)?value:null;return IdlePolicy.RequiredMs(owned,timeout);}
    }
    internal bool SkipIdleWait{get;set;}
    public bool Idle=>IdlePolicy.CanProceed(SkipIdleWait,SkipIdleWait?null:RequiredIdle,unchecked((uint)Environment.TickCount-InputTick),InputNative.ButtonHeld || new[]{16,17,18,91,92}.Any(k=>(InputNative.GetAsyncKeyState(k)&0x8000)!=0));
    [StructLayout(LayoutKind.Sequential)]struct FlashInfo{public uint Size;public nint Window;public uint Flags,Count,Timeout;}
    [DllImport("user32.dll")]static extern bool FlashWindowEx(ref FlashInfo info);
    internal static void Notify(nint window){var info=new FlashInfo{Size=(uint)Marshal.SizeOf<FlashInfo>(),Window=window,Flags=2,Count=5};FlashWindowEx(ref info);}

    internal string IdleMessage
    {
        get{var required=RequiredIdle;if(!required.HasValue)return "暂无法核对前台许可，未抢占窗口；可切回助手或游戏后继续";uint elapsed=unchecked((uint)Environment.TickCount-InputTick);long left=Math.Max(0,(long)required.Value-elapsed);return left>1200?$"等待Windows允许的空闲窗口，约{(left+999)/1000}秒无操作后重新检查；仍在后台观察":"等待键鼠空闲，不抢占当前操作";}
    }
    public uint Pid(nint window){Win32RO.GetWindowThreadProcessId(window,out uint pid);return pid;}
    public bool Alive(nint window,uint pid)=>window!=0 && Win32RO.IsWindow(window) && Pid(window)==pid;
    public bool Activate(nint window)
    {
        if(Foreground==window)return true;SetForegroundWindow(window);
        var clock=System.Diagnostics.Stopwatch.StartNew();while(clock.ElapsedMilliseconds<800){if(Foreground==window)return true;Thread.Sleep(20);}return false;
    }
    public bool Move(Pointer p)
    {
        var inputs=InputNative.PressInputs(p.X,p.Y,InputNative.Desktop());
        uint sent=MoveInput(1,new[]{inputs[0]},Marshal.SizeOf<InputNative.Input>());return sent==1 && FocusTransaction.Near(Cursor,p);
    }
    [DllImport("user32.dll",EntryPoint="SendInput",SetLastError=true)]static extern uint MoveInput(uint count,InputNative.Input[] inputs,int size);
}
