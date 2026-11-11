using System.Runtime.InteropServices;
namespace JX3Artist;

// Local notification-area balloon; works with the current elevated Win32 app.
// Opening it only raises the assistant, never approves a takeover.
internal sealed class WindowsNotice(nint window,Func<uint,WindowsNotice.Data,bool>? emitter=null):IDisposable
{
    internal const int Callback=0x8000+0x4A59;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct Data
    {
        public uint Size;public nint Window;public uint Id,Flags,Message;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;
        public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="Shell_NotifyIconW")]static extern bool Send(uint operation,ref Data data);
    bool Emit(uint operation,Data data)=>emitter?.Invoke(operation,data)??Send(operation,ref data);
    bool added,disposed,version4,menuOpen;nint icon;
    internal bool CleanupSucceeded{get;private set;}=true;
    internal static bool OpensAssistant(int message,nint detail)=>message==Callback && ((long)detail&0xffff) is 0x400 or 0x401 or 0x405 or 0x202;
    internal static bool RequestsMenu(int message,nint detail)=>message==Callback && ((long)detail&0xffff) is 0x205 or 0x7b;
    Data Payload()=>new(){Size=(uint)Marshal.SizeOf<Data>(),Window=window,Id=1,Message=Callback,Tip="剑网3 艺人助手",Info="",Title=""};
    internal bool EnsureIcon()
    {
        if(disposed)return false;
        if(added)return true;
        if(icon==0)icon=AppIcon.LoadNative(32);
        var data=Payload();data.Flags=1|2|4|0x80;data.Icon=icon;
        if(!Emit(0,data))return false;
        added=true;var version=Payload();version.Timeout=4;version4=Emit(4,version);
        return true;
    }
    internal TrayAction OpenMenu(nint coordinates,nint detail)
    {
        if(disposed||!added||menuOpen)return TrayAction.None;
        menuOpen=true;
        try
        {
            var action=TrayMenu.Show(window,coordinates,version4&&((long)detail&0xffff)==0x7b);
            if(!disposed&&action==TrayAction.None)Emit(3,Payload());
            return disposed?TrayAction.None:action;
        }
        finally{menuOpen=false;}
    }
    internal bool Show(string? title=null,string? text=null,bool warning=false)
    {
        bool existing=added;
        if(!EnsureIcon())return false;
        if(existing)Clear(); // Replace obsolete status rather than leave an older balloon queued.
        var data=Payload();
        data.Flags=0x10|0x40;data.InfoFlags=(warning?2u:1u)|0x80;data.Title=title??"艺人表演需要续演";
        data.Info=text??"请点击通知打开艺人助手，再点击“允许本次接管”。确认前不会切换游戏或点击。";
        data.Timeout=15000;return Emit(1,data);
    }
    internal void RefreshIcon(){if(disposed)return;nint next=AppIcon.LoadNative(32);if(added){var data=Payload();data.Flags=2;data.Icon=next;if(!Emit(1,data)){AppIcon.Release(next);throw new InvalidOperationException("通知区图标未更新");}}AppIcon.Release(icon);icon=next;}
    bool Clear(){var data=Payload();data.Flags=0x10;return Emit(1,data);}
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(!added){AppIcon.Release(icon);icon=0;return;}
        bool cleared=false,removed=false;
        try{cleared=Clear();}catch{} // Still attempt icon removal if withdrawal fails.
        try{removed=Emit(2,Payload());}catch{}
        added=false;CleanupSucceeded=cleared&&removed;AppIcon.Release(icon);icon=0;
    }
    internal static void AddTests(Action<string,Action> test)
    {
        test("notification activation only maps to opening assistant",()=>{
            if(!OpensAssistant(Callback,(nint)0x405)||!OpensAssistant(Callback,(nint)0x202)||OpensAssistant(Callback,(nint)0x404)||OpensAssistant(0,(nint)0x405)
                ||!RequestsMenu(Callback,(nint)0x205)||!RequestsMenu(Callback,(nint)0x1007b)||RequestsMenu(Callback,(nint)0x202)||RequestsMenu(0,(nint)0x205))
                throw new Exception("notice callback routing");
            var d=new WindowsNotice(123).Payload();if(d.Window!=123||d.Id!=1||d.Size!=976||Marshal.OffsetOf<Data>(nameof(Data.Info)).ToInt32()!=304||Marshal.OffsetOf<Data>(nameof(Data.BalloonIcon)).ToInt32()!=968||d.Message!=Callback)throw new Exception("notice native payload");
        });
        test("system notice submits balloon once and removes owned icon",()=>{
            var calls=new List<(uint operation,Data data)>();var notice=new WindowsNotice(123,(op,d)=>{calls.Add((op,d));return true;});
            if(!notice.EnsureIcon()||!notice.EnsureIcon()||calls.Count!=2||calls[0].operation!=0||calls[1].operation!=4||calls[1].data.Timeout!=4)
                throw new Exception("persistent tray icon registration");
            if(!notice.Show()||calls.Count!=4||calls[3].operation!=1||calls[3].data.Flags!=0x50||!calls[3].data.Info.Contains("确认前不会"))throw new Exception("notice submission");
            if(!notice.Show("辅助已暂停","原因：测试",true)||calls.Count!=6||calls[4].operation!=1||calls[4].data.Flags!=0x10||calls[4].data.Info!=""||calls[5].data.Title!="辅助已暂停"||calls[5].data.Info!="原因：测试"||(calls[5].data.InfoFlags&0xf)!=2)throw new Exception("pause warning payload");
            notice.Dispose();notice.Dispose();if(calls.Count!=8||calls[6].operation!=1||calls[6].data.Info!=""||calls[7].operation!=2||!notice.CleanupSucceeded)throw new Exception("notice cleanup");
        });
        test("notification cleanup attempts deletion after withdrawal failure and stays terminal",()=>{
            bool removed=false;var notice=new WindowsNotice(123,(op,d)=>{if(op==1&&d.Info=="")throw new InvalidOperationException("withdrawal failed");if(op==2)removed=true;return true;});
            notice.Show();notice.Dispose();if(!removed||notice.CleanupSucceeded||notice.Show())throw new Exception("failed withdrawal prevented terminal cleanup");
        });
    }
}
