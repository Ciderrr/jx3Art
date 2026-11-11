using System.Diagnostics;
using System.IO;
using System.Text;
using Int1;

namespace JX3Artist;

internal sealed record WujieTarget(int Pid, nint Window, DateTime Started)
{
    internal const string Image = "JX3ClientX3DX64";
    internal const string WindowClass = "KGWin32App";
    internal const string MissingProcess = "未找到正在运行的极速端客户端。";
    internal const string MultipleProcesses = "检测到多个极速端客户端，单开模式只支持一个。";
    internal const string WindowUnavailable = "极速端游戏窗口未确认，请恢复游戏窗口后重试。";
    internal const string MultipleWindows = "极速端有多个可见窗口，无法确认唯一游戏窗口。";

    internal static void RequireSingleProcess(int count)
    {
        if(count==0)throw new IOException(MissingProcess);
        if(count!=1)throw new IOException(MultipleProcesses);
    }

    internal static WujieTarget Bind(bool allowMinimized=false)
    {
        var processes = Process.GetProcessesByName(Image);
        try
        {
            RequireSingleProcess(processes.Length);
            var process = processes[0];
            var target = new WujieTarget(process.Id, process.MainWindowHandle, process.StartTime.ToUniversalTime());
            target.EnsureCurrent(allowMinimized);
            return target;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal void EnsureCurrent(bool allowMinimized=false)
    {
        var matching = Process.GetProcessesByName(Image);
        try
        {
            RequireSingleProcess(matching.Length);
            if(matching[0].Id!=Pid)throw new IOException(WindowUnavailable);
        }
        finally { foreach (var candidate in matching) candidate.Dispose(); }
        using var process = Process.GetProcessById(Pid);
        var name = new StringBuilder(256);
        Win32RO.GetWindowThreadProcessId(Window, out uint owner);
        if (Window == 0 || owner != (uint)Pid || !Win32RO.IsWindow(Window) || !process.ProcessName.Equals(Image, StringComparison.OrdinalIgnoreCase)
            || process.StartTime.ToUniversalTime() != Started || Win32RO.GetClassNameW(Window, name, name.Capacity) == 0
            || name.ToString() != WindowClass)
            throw new IOException(WindowUnavailable);
        // Minimized is a capture condition, not a change of process or HWND.
        if(Win32RO.IsIconic(Window))
        {
            if(allowMinimized)return;
            throw new IOException(CaptureWindowMinimized);
        }
        if(!Win32RO.IsWindowVisible(Window)||process.MainWindowHandle!=Window)
            throw new IOException(WindowUnavailable);
        int visibleWindows = 0;
        Win32RO.EnumWindows((candidate, _) =>
        {
            Win32RO.GetWindowThreadProcessId(candidate, out uint pid);
            if (pid == (uint)Pid && Win32RO.IsWindowVisible(candidate)) visibleWindows++;
            return true;
        }, 0);
        if (visibleWindows != 1) throw new IOException(visibleWindows>1?MultipleWindows:WindowUnavailable);
    }
    internal const string CaptureWindowMinimized="游戏已最小化，请恢复窗口";
}
