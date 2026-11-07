using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Int1;

// Read-only token diagnostics for THIS process, not the external UI tool backend.
internal static class InputPreflight
{
    internal sealed record TokenInfo(int? IntegrityRid, bool? Elevated, int Error, string Operation);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr buffer,int size,out int needed);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid,uint sub);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    internal static string Compare(int? self,int? target)
    {
        if(self is null || target is null || self<0 || target<0 || self>0x5000 || target>0x5000) return "PRIVILEGE_UNKNOWN";
        return self<target?"INTEGRITY_MISMATCH":"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED";
    }
    internal static TokenInfo Read(uint pid)
    {
        IntPtr process=OpenProcess(0x1000,false,pid),token=IntPtr.Zero,buffer=IntPtr.Zero;
        if(process==IntPtr.Zero)return new(null,null,Marshal.GetLastWin32Error(),"OpenProcess");
        try
        {
            if(!OpenProcessToken(process,8,out token))return new(null,null,Marshal.GetLastWin32Error(),"OpenProcessToken");
            GetTokenInformation(token,25,IntPtr.Zero,0,out int size);
            if(size<=0 || size>65536)return new(null,null,Marshal.GetLastWin32Error(),"TokenIntegrityLevel.size");
            buffer=Marshal.AllocHGlobal(size);
            if(!GetTokenInformation(token,25,buffer,size,out _))return new(null,null,Marshal.GetLastWin32Error(),"TokenIntegrityLevel");
            IntPtr sid=Marshal.ReadIntPtr(buffer);byte count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if(count==0)return new(null,null,0,"TokenIntegrityLevel.invalidSid");
            int rid=Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)(count-1)));
            Marshal.FreeHGlobal(buffer);buffer=Marshal.AllocHGlobal(4);
            if(!GetTokenInformation(token,20,buffer,4,out _))return new(rid,null,Marshal.GetLastWin32Error(),"TokenElevation");
            return new(rid,Marshal.ReadInt32(buffer)!=0,0,"OK");
        }
        finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);if(token!=IntPtr.Zero)CloseHandle(token);CloseHandle(process);}
    }
    public static int Command(string[] args)
    {
        int i=Array.IndexOf(args,"--out");string path=Path.GetFullPath(i>=0 && i+1<args.Length?args[i+1]:$"output/input_preflight_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var (window,pid,error)=Win32RO.LocateGame();
        var self=Read((uint)Environment.ProcessId);
        var target=window==IntPtr.Zero?new TokenInfo(null,null,0,"TARGET_NOT_FOUND: "+error):Read(pid);
        bool identityValid=window!=IntPtr.Zero && Win32RO.IsWindow(window);
        if(identityValid){Win32RO.GetWindowThreadProcessId(window,out uint current);identityValid=current==pid;}
        string status=identityValid?Compare(self.IntegrityRid,target.IntegrityRid):"TARGET_IDENTITY_INVALID";
        var report=new{schema="jx3-input-preflight-v1",utc=DateTime.UtcNow,selfPid=Environment.ProcessId,targetPid=pid,self,target,status,
            inputVerified=false,scope="THIS int1 process only; not external Computer Use backend; no input sent; no elevation; token compatibility is not click availability"};
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});
        Console.WriteLine($"[input-preflight] {status}; self={self.IntegrityRid}; target={target.IntegrityRid}; query={target.Operation}/Win32={target.Error}; {path}");
        return status=="INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED"?0:13;
    }
    internal static List<Program.CaseResult> Tests()
    {
        var cases=new List<Program.CaseResult>();
        foreach(var row in new (int? Self,int? Target,string Expect)[]{
            (null,12288,"PRIVILEGE_UNKNOWN"),(8192,null,"PRIVILEGE_UNKNOWN"),(null,null,"PRIVILEGE_UNKNOWN"),
            (4096,8192,"INTEGRITY_MISMATCH"),(8192,12288,"INTEGRITY_MISMATCH"),
            (8192,8192,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED"),(12288,8192,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED"),
            (12288,12288,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED"),(-1,8192,"PRIVILEGE_UNKNOWN"),(8192,int.MaxValue,"PRIVILEGE_UNKNOWN")})
        {string actual=Compare(row.Self,row.Target);cases.Add(new(){Name=$"input-preflight: {row.Self}/{row.Target}",Expect=row.Expect,Actual=actual,Pass=actual==row.Expect});}
        return cases;
    }
}
