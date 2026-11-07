using System.Runtime.InteropServices;
using System.Text;

namespace Int1;

/// <summary>
/// INT-1 只读 Win32 面：窗口枚举/状态读取 + 目标定位。
/// 刻意不含任何窗口写 API（无 ShowWindow/SetForegroundWindow/SetWindowPos/SetCursorPos）
/// 与任何输入 API——本程序对游戏零操作（READ_ONLY / NO_GAME_INPUT）。
/// </summary>
internal static class Win32RO
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc f, IntPtr lp);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    // ---------- DPI（只影响本进程坐标体系） ----------
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    public static readonly IntPtr PMv2 = new(-4);

    /// <summary>仅在精确游戏进程身份范围内选择主窗口，面积不能使助手或启动器成为游戏。</summary>
    public static (IntPtr hwnd, uint pid, string err) SelectGameWindow(IEnumerable<(IntPtr Window,uint Pid,string ProcessName,long Area)> windows,uint? expectedPid=null)
    {
        IntPtr best = IntPtr.Zero; uint bestPid = 0; long bestArea = -1;
        foreach(var item in windows)
        {
            if(item.Window==IntPtr.Zero||item.Pid==0||item.Area<0||!string.Equals(item.ProcessName,"JX3ClientX64",StringComparison.OrdinalIgnoreCase)||(expectedPid.HasValue&&item.Pid!=expectedPid.Value))continue;
            if(item.Area>bestArea){best=item.Window;bestPid=item.Pid;bestArea=item.Area;}
        }
        return best == IntPtr.Zero ? (IntPtr.Zero, 0, "not-found") : (best, bestPid, null);
    }
    public static (IntPtr hwnd, uint pid, string err) LocateGame(uint? expectedPid=null)
    {
        var windows=new List<(IntPtr Window,uint Pid,string ProcessName,long Area)>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                string proc = "(?)";
                try { using var process=System.Diagnostics.Process.GetProcessById((int)pid);proc=process.ProcessName; } catch { }
                GetWindowRect(h, out var r);
                long area = (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);
                windows.Add((h,pid,proc,area));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return SelectGameWindow(windows,expectedPid);
    }
}
