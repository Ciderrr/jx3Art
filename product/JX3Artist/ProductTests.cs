using System.IO;
using System.Text.Json;
using Jx3.OneClickTest;
namespace JX3Artist;

internal static class ProductTests
{
    sealed class FakeDesktop:IDesktop
    {
        internal nint Current=10;internal Pointer Pointer=new(40,50);internal uint Tick=100;internal bool IsIdle=true,AllowGame=true,AllowReturn=true,OriginalAlive=true;
        internal Action<nint>? OnActivate;internal List<string> Calls=new();internal bool AllowMove=true,ThrowCursor=false;
        public nint Foreground=>Current;public Pointer Cursor=>ThrowCursor?throw new IOException("cursor read failed"):Pointer;public uint InputTick=>Tick;public bool Idle=>IsIdle;
        public uint Pid(nint w)=>w==10?1u:w==20?2u:0u;
        public bool Alive(nint w,uint p)=>w==20 && p==2 || w==10 && p==1 && OriginalAlive;
        public bool Activate(nint w){Calls.Add("activate:"+w);if(w==20?!AllowGame:!AllowReturn)return false;Current=w;OnActivate?.Invoke(w);return true;}
        public bool Move(Pointer p){Calls.Add("move");if(!AllowMove)return false;Pointer=p;return true;}
    }
    internal static int Run(string[] args)
    {
        var cases=new List<object>();int total=0,passed=0;
        void Assert(bool v){if(!v)throw new Exception("assertion failed");}
        void Test(string name,Action action){total++;try{action();passed++;cases.Add(new{name,passed=true});}catch(Exception e){cases.Add(new{name,passed=false,error=e.Message});}}
        ResourceRecordingTests.Add(Test,Path.Combine(Path.GetTempPath(),"JX3Artist-resource-tests"));
        CaptureDiagnosticTests.Add(Test);
        WindowGeometryTests.Add(Test);
        WindowFramePolicyTests.Add(Test);
        BitmapTransportTests.Add(Test);
        ExperienceEstimateTests.Add(Test);
        ExperienceSessionTests.Add(Test);
        ExperienceLifetimeTests.Add(Test);
        ExperienceResetTests.Add(Test);
        AppIcon.AddTests(Test);
        DistributionTests.Add(Test);
        EntryIconPolicyTests.Add(Test);
        ExperienceBoundaryTests.Add(Test);
        UiPreferencesTests.Add(Test);
        WebUiProtocol.AddTests(Test);
        EntryEvidenceTests.Add(Test);
        TimerEvidenceTests.Add(Test);
        EngineBundle.AddTests(Test);
        RecognizerWorkerTests.Add(Test);
        WindowsNotice.AddTests(Test);
        PreclickEvidence.AddTests(Test);
        TakeoverRetryBudget.AddTests(Test);
        EntryPixelProof.AddTests(Test);
        PreparationForeground.AddTests(Test);
        LiveTakeoverMode.AddTests(Test);
        LifecycleNotices.AddTests(Test);
        StartupCountdownTests.Add(Test);
        SupportReportTests.Add(Test);
        SupportHistoryTests.Add(Test);
        ObservationDiagnosticTests.Add(Test);
        Test("confirmed performance without any countdown keeps polling and two fresh entry frames request continuation",()=>{
            var core=new AutomationController(1,0);
            for(int i=1;i<=8;i++)core.Observe(new(i*1000,i*1000+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownQuality:"UNKNOWN",PanelLocated:true,PanelLocation:"UNIQUE_PANEL"));
            Assert(core.State==Phase.Performing&&core.Starts==0&&!core.WantsStart&&core.Countdown.EndSourceMs==null&&core.NextObservationDelayMs(25000)==30000);
            core.Observe(new(30000,30100,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true,PanelLocation:"NO_VERIFIED_PANEL"));Assert(!core.WantsStart);
            core.Observe(new(31000,31100,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true,PanelLocation:"NO_VERIFIED_PANEL"));Assert(core.WantsStart&&core.Starts==0&&core.Returns==1);core.Stop();Assert(!core.WantsStart);
        });
        Test("small panel evidence records independent proof and preserves old pixel rejection without authorizing input",()=>{
            string text=JsonSerializer.Serialize(new{d2=new{painted_body=new{agreement=.905},painted_shape=new{confirmed=true,radius=3,total=new{colour=.996,coverage=.998,precision=.995},lower=new{colour=.998,coverage=.992,precision=.991}}}});
            var proof=WindowFramePolicy.SmallEvidence(text);Assert(proof?.Confirmed==true&&proof.LegacyAgreement==.905&&proof.Radius==3&&!proof.ClockRecovered);
            Assert(WindowFramePolicy.SmallEvidence("{}")==null&&WindowFramePolicy.SmallEvidence(text.Replace("\"radius\":3","\"radius\":4"))==null);
            Assert(WindowFramePolicy.SmallEvidence(text.Replace("\"radius\":3","\"radius\":\"bad\""))==null);
            Assert(SupportReport.Sanitize("observation_cycle",JsonSerializer.SerializeToElement(new{Metrics=new{SmallPanelEvidence=proof,Message="SECRET"}}),out var safe)&&safe.GetRawText().Contains("LegacyAgreement")&&!safe.GetRawText().Contains("SECRET"));
        });
        Test("game locator never selects larger assistant launcher or similar names and honors expected PID",()=>{
            var windows=new[]{((nint)10,100u,"JX3ClientX64",1280L*720),((nint)20,200u,"JX3Artist",1440L*1050),((nint)30,300u,"SeasunGame",3840L*2160),((nint)40,400u,"NotJX3ClientX64",100000000L)};
            Assert(Int1.Win32RO.SelectGameWindow(windows.Take(2),100).hwnd==10);
            Assert(Int1.Win32RO.SelectGameWindow(windows,100).hwnd==10);
            Assert(Int1.Win32RO.SelectGameWindow(windows.Where(w=>w.Item2!=100),100).hwnd==0);
            Assert(Int1.Win32RO.SelectGameWindow(new[]{((nint)10,100u,"jx3clientx64",1L),((nint)50,500u,"JX3ClientX64",100L)},100).hwnd==10);
            Assert(ProblemCatalog.Identify("目标不是游戏客户端").Code=="JX3-WINDOW-001");
        });
        Test("temporary notifications are realtime and a disposed notifier cannot submit again",()=>{
            var calls=new List<(uint op,WindowsNotice.Data data)>();var notice=new WindowsNotice(123,(op,d)=>{calls.Add((op,d));return true;});
            Assert(notice.Show("开始","当前状态")&&(calls.Last().data.Flags&0x40)!=0);notice.Dispose();int count=calls.Count;Assert(!notice.Show("陈旧","不应发送")&&calls.Count==count);
        });
        Test("unread countdown explains the running fallback and respects manual and terminal states",()=>{
            var view=new ViewState(Phase.Performing,"",CountdownStatus:"未确认");
            string auto=StatusSummary.CountdownFallback(view,true)!,manual=StatusSummary.CountdownFallback(view,false)!;
            Assert(auto.Contains("不影响挂机流程")&&auto.Contains("自动续演")&&manual.Contains("提醒您继续")&&!manual.Contains("自动续演"));
            Assert(StatusSummary.CountdownFallback(view with{CountdownEnd=1000},true)==null);
            Assert(StatusSummary.CountdownFallback(view,true,active:false)==null);
            foreach(var phase in new[]{Phase.Paused,Phase.Stopped,Phase.Completed,Phase.WaitingVisibility,Phase.Watching,Phase.Starting,Phase.WaitingIdle,Phase.ConfirmingEntry})Assert(StatusSummary.CountdownFallback(view with{Phase=phase},true)==null);
        });
        Test("panel diagnostic distinguishes body failure without changing recognition authority",()=>{
            string text="{\"d2_location\":{\"rejected_clocks\":[{\"verification_stage\":\"PAINTED_BODY_UNCONFIRMED\",\"region\":[616,500,632,516],\"clock_core\":{\"score\":0.994},\"painted_body\":{\"agreement\":0.89,\"lower_agreement\":0.967,\"mask_pixels\":2148}}]}}";
            var d=WindowFramePolicy.Verification(text);Assert(d?.Stage=="PAINTED_BODY_UNCONFIRMED"&&d.BodyAgreement==.89&&d.MaskPixels==2148&&d.ClockSearchWidth==16);
            Assert(SupportReport.Sanitize("timer_diagnostic",JsonSerializer.SerializeToElement(new{Metrics=new{PanelVerification=d,Raw="SECRET"}}),out var safe)&&safe.GetRawText().Contains("PAINTED_BODY_UNCONFIRMED")&&!safe.GetRawText().Contains("SECRET"));
            Assert(WindowFramePolicy.Verification("{}")==null);
        });
        Test("bounded recovery telemetry preserves proposal scales without changing observation authority",()=>{
            var r=WindowFramePolicy.Recovery("{\"panel_scale_recovery\":{\"status\":\"CONFIRMED\",\"original_scale\":0.71,\"selected_scale\":0.7,\"attempts\":[{},{}]}}");Assert(r.Status=="CONFIRMED"&&r.Attempts==2&&r.Original==.71&&r.Selected==.7);
            Assert(WindowFramePolicy.Recovery("{}").Status==null&&WindowFramePolicy.Recovery("{\"panel_scale_recovery\":{\"status\":\"SECRET\"}}").Status==null);
            Assert(SupportReport.Sanitize("panel_scale_recovery",JsonSerializer.SerializeToElement(new{Metrics=new{RecoveryStatus="CONFIRMED",RecoveryAttempts=2,OriginalPanelScale=.71,SelectedPanelScale=.7,Raw="SECRET"}}),out var safe)&&safe.GetProperty("Metrics").GetProperty("RecoveryStatus").GetString()=="CONFIRMED"&&!safe.GetRawText().Contains("SECRET"));
        });
        AssistantHotkeys.AddTests(Test);
        Test("automatic permission revoked during foreground acquisition prevents action",()=>{
            var mode=new LiveTakeoverMode();mode.Set(true);var permit=mode.Permit(false);var desktop=new FakeDesktop();int input=0;desktop.OnActivate=w=>{if(w==20)mode.Set(false);};
            var result=FocusTransaction.Run(desktop,20,2,()=>!permit(),lease=>{input++;return new(true,1);},authorizedMotion:true);
            Assert(!result.Started&&input==0&&desktop.Current==10);
        });
        Test("mode change after input attempt allows passive confirmation and restoration",()=>{
            var mode=new LiveTakeoverMode();mode.Set(true);var permit=mode.Permit(false);var desktop=new FakeDesktop();Interaction? active=null;
            var result=FocusTransaction.Run(desktop,20,2,()=>active?.InputAttempted!=true&&!permit(),lease=>{active=lease;lease.InputAttempted=true;mode.Set(false);return new(true,1);},authorizedMotion:true);
            Assert(result.Started&&result.Restored&&!permit());
        });
        Observation Entry(double t)=>new(t,t+100,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE");
        Observation Panel(double t)=>new(t,t+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE");
        AutomationController Ready(int max=3){var c=new AutomationController(max,1000);c.Observe(Entry(2000));c.Observe(Entry(3000));return c;}
        Test("initial start needs two current entry frames",()=>{var c=new AutomationController(3,1000);c.Observe(Entry(2000));Assert(!c.WantsStart);c.Observe(Entry(3000));Assert(c.WantsStart);});
        Test("complete entry icon needs two fresh positives regardless of gold ornament visibility",()=>{var c=new AutomationController(3,1000);c.Observe(Entry(2000) with{D2="UNKNOWN",EntryContextConfirmed=false});Assert(!c.WantsStart&&c.State==Phase.ConfirmingEntry);c.Observe(Entry(3000) with{D2="UNKNOWN",EntryContextConfirmed=false});Assert(c.WantsStart);c.Observe(Entry(4000) with{D2="UNKNOWN",EntryContextConfirmed=true});Assert(c.WantsStart&&c.Starts==0);});
        Test("ambiguous entry waits without authorizing input",()=>{var c=new AutomationController(3,1000);for(int i=0;i<3;i++)c.Observe(Entry(2000+i*1000) with{Location="AMBIGUOUS",Count=2});Assert(c.State==Phase.WaitingVisibility && !c.Done && !c.WantsStart);});
        Test("unknown panel with no verified entry still waits without input",()=>{var c=new AutomationController(3,1000);for(int i=0;i<3;i++)c.Observe(Entry(2000+i*1000) with{D2="UNKNOWN",Location="NO_VERIFIED_CANDIDATE",Count=0});Assert(c.State==Phase.WaitingVisibility && !c.Done && !c.WantsStart);});
        Test("performing at startup adopted without click",()=>{var c=new AutomationController(3,1000);c.Observe(Panel(2000));c.Observe(Panel(3000));Assert(c.State==Phase.Performing && c.Starts==0 && !c.WantsStart);});
        Test("stale frame does not authorize",()=>{var c=Ready();c.Observe(Entry(4000) with{Now=8001});Assert(!c.WantsStart);});
        Test("identity loss immediately pauses",()=>{var c=Ready();c.Observe(Entry(4000) with{Identity=false});Assert(c.State==Phase.Paused && !c.WantsStart);});
        Test("transient geometry change revokes intent and recovers only after two post-change frames",()=>{var c=Ready();c.Observe(Entry(4000) with{Geometry=false});Assert(c.State==Phase.WaitingVisibility&&!c.WantsStart);c.Observe(Entry(4090));Assert(!c.WantsStart);c.Observe(Entry(4300));Assert(!c.WantsStart);c.Observe(Entry(4500));Assert(c.WantsStart);});
        Test("stop during geometry recovery remains terminal",()=>{var c=Ready();c.Observe(Entry(4000) with{Geometry=false});c.Stop();c.Observe(Entry(5000));c.Observe(Entry(6000));Assert(c.State==Phase.Stopped&&!c.WantsStart);});
        Test("three-second freshness boundary is exact and stale recovery needs two frames",()=>{var c=new AutomationController(2,0);c.Observe(Entry(1000) with{Now=4000});c.Observe(Entry(2000) with{Now=5000});Assert(c.WantsStart);c.Observe(Entry(3000) with{Now=6000.01});Assert(!c.WantsStart);c.Observe(Entry(7000));Assert(!c.WantsStart);c.Observe(Entry(8000));Assert(c.WantsStart);});
        Test("old frame after start not reused",()=>{var c=Ready();c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(3800) with{Now=4100});Assert(!c.WantsStart && c.Starts==1);});
        Test("early return requires two fresh entry confirmations before resuming",()=>{var c=Ready();c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(14000));Assert(!c.WantsStart);c.Observe(Entry(15000));Assert(!c.Done && c.WantsStart && c.Starts==1 && c.Returns==1);});
        Test("PO continued-assistance rule resumes early return without silently stopping",()=>
        {var c=Ready(2);c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(14000));Assert(!c.WantsStart);c.Observe(Entry(15000));Assert(!c.Done && c.WantsStart && c.Starts==1 && c.Returns==1);});
        Test("three simulated natural cycles with two restarts",()=>
        {
            var c=Ready();double now=4000;
            for(int n=1;n<=3;n++)
            {Assert(c.WantsStart);c.BeginAction();c.ActionFinished(true,true,now,now-10,null);c.Observe(Panel(now+1000));c.Observe(Panel(now+2000));now+=1800000;c.Observe(Entry(now));c.Observe(Entry(now+1000));now+=2000;}
            Assert(c.Starts==3 && c.Returns==3 && c.State==Phase.Completed && !c.WantsStart);
        });
        Test("manual continuous option not session capped",()=>
        {
            var c=Ready(0);double now=4000;for(int n=0;n<6;n++){c.BeginAction();c.ActionFinished(true,true,now,now-10,null);now+=1800000;c.Observe(Entry(now));c.Observe(Entry(now+1000));now+=2000;}Assert(c.WantsStart && c.Starts==6 && !c.Done);
        });
        Test("stop never revives on late action receipt",()=>{var c=Ready();c.BeginAction();c.Stop();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(1804000));Assert(c.State==Phase.Stopped && !c.WantsStart);});
        Test("failed input pauses without retry",()=>{var c=Ready();c.BeginAction();c.ActionFinished(false,true,4000,3900,"failure");c.Observe(Entry(5000));Assert(c.State==Phase.Paused && c.Starts==0);});
        Test("successful start with restore failure counted but paused",()=>{var c=Ready();c.BeginAction();c.ActionFinished(true,false,4000,3900,"restore failed");Assert(c.Starts==1 && c.State==Phase.Paused);});
        Test("duplicate action receipt refused",()=>{var c=Ready();c.BeginAction();c.ActionFinished(true,true,4000,3900,null);try{c.ActionFinished(true,true,4000,3900,null);throw new Exception("duplicate");}catch(InvalidOperationException){}});
        Test("manual interruptions keep confirmed restart available beyond four per hour",()=>{var c=Ready(0);double now=4000;for(int n=0;n<8;n++){Assert(c.WantsStart);c.BeginAction();c.ActionFinished(true,true,now,now-10,null);c.Observe(Entry(now+1000));c.Observe(Entry(now+2000));now+=3000;}Assert(c.Starts==8 && c.WantsStart && !c.Done);});
        ActionEvidence Click(FakeDesktop d,Interaction lease){d.Pointer=new(2751,1503);d.Tick=200;lease.OwnInput(d.Pointer,d.Tick);return new(true,4000);}
        Test("foreground takeover restores original window and pointer",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>Click(d,l));Assert(r.Started && r.Restored && d.Current==10 && d.Pointer==new Pointer(40,50) && d.Calls.SequenceEqual(new[]{"activate:20","activate:10","move"}));});
        Test("busy user gets no window changes",()=>{var d=new FakeDesktop{IsIdle=false};int calls=0;var r=FocusTransaction.Run(d,20,2,()=>false,_=>{calls++;return new(true,4000);});Assert(r.Deferred && calls==0 && d.Calls.Count==0);});
        Test("foreground refusal retries only after fresh entry evidence and within the same budget",()=>{
            var desktop=new FakeDesktop{AllowGame=false};var core=Ready();var budget=new TakeoverRetryBudget();budget.Begin(3500);int clicks=0;
            core.BeginAction();var refusal=FocusTransaction.Run(desktop,20,2,()=>false,l=>{clicks++;return Click(desktop,l);});
            Assert(refusal.Deferred&&refusal.Error==FocusTransaction.ActivationFailure&&clicks==0&&budget.Defer(4300));
            core.ActionDeferred("重新确认");core.Observe(Entry(5000));Assert(!core.WantsStart);core.Observe(Entry(6000));Assert(core.WantsStart&&budget.CanResume(6000));
            desktop.AllowGame=true;core.BeginAction();var success=FocusTransaction.Run(desktop,20,2,()=>false,l=>{clicks++;return Click(desktop,l);});Assert(success.Started&&success.Restored&&clicks==1);
        });
        Test("foreground refusal cannot retry after input changes, another window appears, or permission is revoked",()=>{
            foreach(int scenario in new[]{0,1,2})
            {
                var desktop=new FakeDesktop();bool cancelled=false;
                desktop.OnActivate=_=>{desktop.Current=scenario==1?30:10;if(scenario==0)desktop.Tick++;if(scenario==2)cancelled=true;};
                var result=FocusTransaction.Run(desktop,20,2,()=>cancelled,_=>throw new Exception("input path entered"));
                Assert(!result.Deferred&&!result.Started&&result.Error==FocusTransaction.ActivationFailure);
            }
        });
        Test("foreground denied means no click",()=>{var d=new FakeDesktop{AllowGame=false};int clicks=0;var r=FocusTransaction.Run(d,20,2,()=>false,_=>{clicks++;return new(true,4000);});Assert(!r.Started && clicks==0);});
        Test("cancel before activation gets no click",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>true,_=>throw new Exception("clicked"));Assert(!r.Started && d.Calls.Count==0);});
        Test("cancel during activation restores without click",()=>{var d=new FakeDesktop();bool stop=false;d.OnActivate=w=>{if(w==20)stop=true;};var r=FocusTransaction.Run(d,20,2,()=>stop,_=>throw new Exception("clicked"));Assert(!r.Started && d.Current==10);});
        Test("user focus takeover not forcibly undone",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var result=Click(d,l);d.Current=30;return result;});Assert(r.Started && !r.Restored && d.Current==30 && !d.Calls.Contains("move"));});
        Test("restore evidence normal receipt serializes without pointer handles",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>Click(d,l));Assert(r.Restored&&r.Restoration is{Reasons.Length:0,OriginalAlive:true});using var json=JsonDocument.Parse(JsonSerializer.Serialize(r));Assert(json.RootElement.GetProperty("Restoration").GetProperty("OriginalWindow").GetInt64()==10);});
        Test("restore diagnostics distinguish foreground only",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.Current=30;return a;});Assert(r.ControlTransferred&&!r.Restored&&r.Restoration!.Reasons.SequenceEqual(new[]{"FOREGROUND_CHANGED"})&&!d.Calls.Contains("activate:10"));});
        Test("restore diagnostics distinguish input tick without cursor movement",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.Tick++;return a;});Assert(r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"INPUT_TICK_CHANGED"})&&!d.Calls.Contains("move"));});
        Test("restore diagnostics distinguish cursor movement without tick",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.Pointer=new(999,999);return a;});Assert(r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"CURSOR_MOVED"})&&!d.Calls.Contains("move"));});
        Test("restore diagnostics retain simultaneous changes",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.Pointer=new(999,999);d.Tick++;d.Current=30;return a;});Assert(r.Restoration!.Reasons.Length==3&&!r.Restored&&r.ControlTransferred);});
        Test("restore diagnostics retain release failure",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);l.SafeToRestore=false;return a;});Assert(!r.Restored&&!r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"RELEASE_NOT_CONFIRMED"})&&r.Restoration.ObservedForeground==null);});
        Test("restore diagnostics retain original window identity loss",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.OriginalAlive=false;return a;});Assert(!r.Restored&&!r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"ORIGINAL_WINDOW_GONE"}));});
        Test("restore diagnostics retain activation refusal",()=>{var d=new FakeDesktop{AllowReturn=false};var r=FocusTransaction.Run(d,20,2,()=>false,l=>Click(d,l));Assert(!r.Restored&&!r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"ORIGINAL_ACTIVATION_FAILED"}));});
        Test("restore diagnostics retain failed pointer restoration",()=>{var d=new FakeDesktop{AllowMove=false};var r=FocusTransaction.Run(d,20,2,()=>false,l=>Click(d,l));Assert(!r.Restored&&!r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"POINTER_RESTORE_FAILED"}));});
        Test("diagnostic read failure after foreground refusal does not change ownership",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.Current=30;d.ThrowCursor=true;return a;});Assert(!r.Restored&&r.ControlTransferred&&r.Restoration!.Reasons.SequenceEqual(new[]{"FOREGROUND_CHANGED"})&&r.Restoration.ReadError!=null&&!d.Calls.Contains("activate:10"));});
        Test("required restore read failure never permits restoring",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var a=Click(d,l);d.ThrowCursor=true;return a;});Assert(!r.Restored&&!r.ControlTransferred&&r.Restoration!.Reasons.Contains("RESTORE_CHECK_FAILED")&&!d.Calls.Contains("activate:10"));});
        Test("user cursor movement not forcibly undone",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var result=Click(d,l);d.Pointer=new(500,500);d.Tick++;return result;});Assert(!r.Restored && d.Pointer==new Pointer(500,500));});
        Test("unconfirmed release prevents cursor/focus restore",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{Click(d,l);l.SafeToRestore=false;return new(false,0,"release failed");});Assert(!r.Restored && d.Calls.Count==1);});
        Test("original window lost never guessed",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var result=Click(d,l);d.OriginalAlive=false;return result;});Assert(!r.Restored && !d.Calls.Contains("activate:10"));});
        Test("exception before input restores original without pointer move",()=>{var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,_=>throw new IOException("injected"));Assert(!r.Started && r.Restored && d.Current==10 && !d.Calls.Contains("move"));});
        Test("last-moment input lease valid",()=>{var d=new FakeDesktop{Current=20};Assert(FocusTransaction.CanDispatch(d,new(d.Cursor,d.Tick),20,2,false));});
        Test("last-moment keyboard activity invalidates lease",()=>{var d=new FakeDesktop{Current=20};var lease=new Interaction(d.Cursor,d.Tick);d.Tick++;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));});
        Test("explicit jitter policy tolerates small movement but keeps hold stop and focus gates",()=>{
            var d=new FakeDesktop{Current=20};var lease=new Interaction(d.Cursor,d.Tick){AllowPointerJitter=true};d.Tick++;d.Pointer=new(lease.ExpectedPointer.X+24,lease.ExpectedPointer.Y-24);Assert(FocusTransaction.CanDispatch(d,lease,20,2,false));
            d.Pointer=new(lease.ExpectedPointer.X+25,lease.ExpectedPointer.Y);Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));d.Pointer=lease.ExpectedPointer;
            d.IsIdle=false;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));d.IsIdle=true;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,true));d.Current=30;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));
        });
        Test("small motion during activation still executes one guarded action",()=>{
            var d=new FakeDesktop();d.OnActivate=w=>{if(w==20){d.Pointer=new(d.Pointer.X+3,d.Pointer.Y);d.Tick++;}};int calls=0;
            var r=FocusTransaction.Run(d,20,2,()=>false,l=>{Assert(FocusTransaction.CanDispatch(d,l,20,2,false));calls++;return Click(d,l);},allowPointerJitter:true);
            Assert(calls==1&&r.Started&&r.Restored);
        });
        Test("explicit authorization ignores arbitrary pointer motion but never stop held input or lost focus",()=>{
            var d=new FakeDesktop{Current=20};var lease=new Interaction(d.Cursor,d.Tick){AuthorizedMotion=true};
            foreach(var point in new[]{new Pointer(0,0),new Pointer(-8000,12000),new Pointer(100000,-100000)}){d.Pointer=point;d.Tick++;Assert(FocusTransaction.CanDispatch(d,lease,20,2,false));}
            d.IsIdle=false;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));d.IsIdle=true;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,true));d.Current=30;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));
        });
        Test("authorized activation movement executes exactly once and keeps conservative restoration",()=>{
            var d=new FakeDesktop();d.OnActivate=w=>{if(w==20){d.Pointer=new(-2000,1500);d.Tick++;}};int calls=0;
            var r=FocusTransaction.Run(d,20,2,()=>false,l=>{Assert(FocusTransaction.CanDispatch(d,l,20,2,false));calls++;var result=Click(d,l);d.Pointer=new(6000,6000);d.Tick++;return result;},authorizedMotion:true);
            Assert(calls==1&&r.Started&&r.ControlTransferred&&!r.Restored&&!d.Calls.Contains("move"));
        });
        Test("preinput temporary refusal can wait but an attempted input never becomes retryable",()=>{
            var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,_=>new(false,1000,"STALE_FRAME",Retryable:true),authorizedMotion:true);Assert(r.Deferred&&!r.Started);
            d=new FakeDesktop();r=FocusTransaction.Run(d,20,2,()=>false,l=>{l.InputAttempted=true;return new(false,1000,"uncertain input",Retryable:true);},authorizedMotion:true);Assert(!r.Deferred);
            d=new FakeDesktop();r=FocusTransaction.Run(d,20,2,()=>false,_=>throw new InvalidOperationException("STALE_FRAME"),authorizedMotion:true);Assert(r.Deferred&&!r.Started);
        });
        Test("deferred action resets evidence without terminating or manufacturing another start",()=>{
            var c=Ready();c.BeginAction();c.ActionDeferred("temporary stale evidence");Assert(!c.Done&&!c.WantsStart&&c.Starts==0);c.Observe(Entry(5000));Assert(!c.WantsStart);c.Observe(Entry(6000));Assert(c.WantsStart);c.BeginAction();c.Stop();c.ActionDeferred("late defer");Assert(c.State==Phase.Stopped&&!c.WantsStart);
        });
        Test("last-moment foreground change invalidates lease",()=>{var d=new FakeDesktop{Current=20};var lease=new Interaction(d.Cursor,d.Tick);d.Current=30;Assert(!FocusTransaction.CanDispatch(d,lease,20,2,false));});
        Test("last-moment stop invalidates lease",()=>{var d=new FakeDesktop{Current=20};Assert(!FocusTransaction.CanDispatch(d,new(d.Cursor,d.Tick),20,2,true));});
        FeedbackTests.Add(Test);
        int index=Array.IndexOf(args,"--out");string output=Path.GetFullPath(index>=0 && index+1<args.Length?args[index+1]:"product_tests.json");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Test("cache cleanup retains unknown user files",()=>
        {string root=Path.Combine(Path.GetDirectoryName(output)!,"cache_"+Guid.NewGuid().ToString("N"));Cache.Prepare(root);string known=Path.Combine(root,"transport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(known);File.WriteAllText(Path.Combine(known,"frame-000001.bmp"),"own");File.WriteAllText(Path.Combine(known,"notes.txt"),"keep");Cache.Prepare(root);Assert(!File.Exists(Path.Combine(known,"frame-000001.bmp")) && File.ReadAllText(Path.Combine(known,"notes.txt"))=="keep");});
        Test("unowned cache never cleared",()=>
        {string root=Path.Combine(Path.GetDirectoryName(output)!,"unowned_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"photo.bmp"),"keep");try{Cache.Prepare(root);throw new Exception("accepted unowned");}catch(IOException){}Assert(File.Exists(Path.Combine(root,"photo.bmp")));});
        Test("stop idle service is not forever stopping",()=>{var s=new AutomationService(Path.GetDirectoryName(output)!,"missing",true);s.Stop();Assert(!s.IsRunning && s.Latest.Phase==Phase.Stopped && !s.Latest.Message.Contains("正在停止"));});
        Test("real demo worker start stop and restart without game",()=>
        {
            var s=new AutomationService(Path.Combine(Path.GetDirectoryName(output)!,"demo_worker"),"missing",true);
            Assert(s.Start(1));Assert(SpinWait.SpinUntil(()=>s.Latest.Phase==Phase.Performing,4000));s.Stop();Assert(SpinWait.SpinUntil(()=>!s.IsRunning,4000));Assert(s.Latest.Phase==Phase.Stopped);
            Assert(s.Start(1));s.Stop();Assert(SpinWait.SpinUntil(()=>!s.IsRunning,4000));Assert(s.Latest.Phase==Phase.Stopped);
        });
        Test("unowned logs are not overwritten",()=>
        {string root=Path.Combine(Path.GetDirectoryName(output)!,"unowned_logs_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string p=Path.Combine(root,"events.jsonl");File.WriteAllText(p,"keep");using var journal=new Journal(root);try{journal.Write("test",new{});throw new Exception("overwrote old log");}catch(IOException){}Assert(File.ReadAllText(p)=="keep");});
        Test("adopt current performance then start only two further rounds",()=>
        {
            var c=new AutomationController(2,1000);c.Observe(Panel(2000));c.Observe(Panel(3000));Assert(!c.WantsStart && c.Starts==0 && c.Message.Contains("不重复点击"));
            double time=100000;c.Observe(Entry(time));c.Observe(Entry(time+1000));
            for(int n=0;n<2;n++){Assert(c.WantsStart);c.BeginAction();c.ActionFinished(true,true,time+2000,time+1900,null);time+=1803000;c.Observe(Entry(time));c.Observe(Entry(time+1000));}
            Assert(c.State==Phase.Completed && c.Starts==2 && c.Returns==3);
        });
        Test("own foreground needs only short idle",()=>Assert(IdlePolicy.RequiredMs(true,200000)==1200));
        Test("other foreground waits for Windows timeout",()=>Assert(IdlePolicy.RequiredMs(false,200000)==200000));
        Test("unknown Windows permission does not assume idle eligibility",()=>Assert(IdlePolicy.RequiredMs(false,null)==null));
        Test("zero Windows timeout still requires physical idle",()=>Assert(IdlePolicy.RequiredMs(false,0)==1200));
        Test("cancelled session always records exactly one end marker",()=>
        {
            string path=Path.Combine(Path.GetDirectoryName(output)!,"trace_"+Guid.NewGuid().ToString("N"));using var journal=new Journal(path);var trace=new SessionTrace(journal);trace.Begin(new{maxStarts=2});
            try{throw new OperationCanceledException();}catch(OperationCanceledException){}finally{trace.End(new{cancelled=true});}
            trace.End(new{cancelled=true});using var stream=new FileStream(Path.Combine(path,"events.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);var lines=reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries);Assert(lines.Length==2);using var last=JsonDocument.Parse(lines[1]);Assert(last.RootElement.GetProperty("kind").GetString()=="session_stopped" && last.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
        });
        Test("failed binding cannot claim a started session ended",()=>
        {string path=Path.Combine(Path.GetDirectoryName(output)!,"no_trace_"+Guid.NewGuid().ToString("N"));using var journal=new Journal(path);new SessionTrace(journal).End(new{});Assert(!Directory.Exists(path));});
        Test("temporary map occlusion resumes observation instead of terminal pause",()=>
        {var c=new AutomationController(2,1000);c.Observe(Panel(2000));c.Observe(Panel(3000));for(int i=0;i<4;i++)c.Observe(Panel(4000+i*1000) with{Location="NO_VERIFIED_CANDIDATE",D2="UNKNOWN"});Assert(!c.Done && !c.WantsStart);c.Observe(Panel(9000));c.Observe(Panel(10000));Assert(c.State==Phase.Performing && !c.WantsStart && c.Starts==0);});
        Test("corroborated entry with original D2 UNKNOWN reaches foreground preflight",()=>
        {var c=new AutomationController(2,1000);c.Observe(Entry(2000) with{D2="UNKNOWN",EntryContextConfirmed=true});c.Observe(Entry(3000) with{D2="UNKNOWN",EntryContextConfirmed=true});Assert(c.WantsStart);});
        Test("independent context cannot override visible or unevaluable panel",()=>
        {foreach(string state in new[]{"VISIBLE","NOT_EVALUABLE"}){var c=new AutomationController(2,1000);for(int i=0;i<5;i++)c.Observe(Entry(2000+i*1000) with{D2=state,EntryContextConfirmed=true});Assert(!c.WantsStart && c.State==Phase.WaitingVisibility);}});
        Test("independent context cannot override ambiguity or stale identity",()=>
        {Assert(!EntryReadiness.Confirmed(Entry(2000) with{D2="UNKNOWN",EntryContextConfirmed=true,Count=2}));var c=new AutomationController(2,1000);c.Observe(Entry(2000) with{D2="UNKNOWN",EntryContextConfirmed=true});c.Observe(Entry(3000) with{D2="UNKNOWN",EntryContextConfirmed=true,Identity=false});Assert(c.Done && !c.WantsStart);});
        Test("visibility gap clears candidate confirmations",()=>
        {var c=Ready();c.Observe(Entry(4000) with{D2="UNKNOWN",Location="NO_VERIFIED_CANDIDATE",Count=0});Assert(!c.WantsStart);c.Observe(Entry(5000));Assert(!c.WantsStart);c.Observe(Entry(6000));Assert(c.WantsStart);});
        Test("occlusion recovery follows continued-assistance policy on early return",()=>
        {var c=Ready();c.BeginAction();c.ActionFinished(true,true,4000,3900,null);for(int i=0;i<5;i++)c.Observe(Panel(5000+i*1000) with{D2="UNKNOWN"});c.Observe(Panel(11000));c.Observe(Panel(12000));c.Observe(Entry(13000));Assert(!c.WantsStart);c.Observe(Entry(14000));Assert(!c.Done && c.Starts==1 && c.Returns==1 && c.WantsStart);});
        Test("natural return after occlusion still schedules exactly one start",()=>
        {var c=Ready(2);c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Panel(5000) with{D2="UNKNOWN"});c.Observe(Entry(1805000) with{D2="UNKNOWN",EntryContextConfirmed=true});Assert(!c.WantsStart);c.Observe(Entry(1806000) with{D2="UNKNOWN",EntryContextConfirmed=true});Assert(c.WantsStart && c.Returns==1 && c.Starts==1);c.BeginAction();c.Observe(Entry(1807000));Assert(!c.WantsStart);});
        Test("stop during visibility wait never recovers into action",()=>
        {var c=Ready();c.Observe(Panel(4000) with{D2="UNKNOWN"});c.Stop();c.Observe(Entry(5000));c.Observe(Entry(6000));Assert(c.State==Phase.Stopped && !c.WantsStart);});
        Test("stable and near-end sampling use production schedule",()=>
        {var c=Ready();Assert(c.NextObservationDelayMs(3500)==5000);c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Panel(5000) with{CountdownSeconds=1799,CountdownQuality="DUAL_PASS_AGREEMENT"});Assert(c.NextObservationDelayMs(5100)==1000);c.Observe(Panel(6000) with{CountdownSeconds=1798,CountdownQuality="DUAL_PASS_AGREEMENT"});Assert(c.NextObservationDelayMs(6100)==120000);Assert(c.NextObservationDelayMs(1795000)==1000);c.Observe(Panel(1796000) with{D2="UNKNOWN"});Assert(c.NextObservationDelayMs(1796100)==5000);});
        Test("adopted countdown is unknown and uses bounded low-frequency checks",()=>
        {var c=new AutomationController(2,1000);c.Observe(Panel(2000));c.Observe(Panel(3000));Assert(c.NextObservationDelayMs(3000000)==10000 && c.Countdown.EndSourceMs==null && !c.WantsStart);c.Observe(Entry(3001000));Assert(c.NextObservationDelayMs(3002000)==1000 && !c.WantsStart);});
        CountdownTests.Add(Test);
        Test("post-input feedback survives foreground changes with two fresh target frames",()=>
        {var f=new StartFeedback(FeedbackFocus.Any);f.InputCompleted(2,1,1000);f.Observe(new(1100,1200,true,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE"));f.Observe(new(1300,1400,true,false,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE"));f.Complete(1450);Assert(f.State==FeedbackState.StartFeedbackObserved);});
        Test("background confirmation still rejects lost identity and stale evidence",()=>
        {var f=new StartFeedback(FeedbackFocus.Any);f.InputCompleted(2,1,1000);f.Observe(new(900,1200,true,false,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE"));Assert(f.State!=FeedbackState.StartFeedbackObserved);f.Observe(new(1300,1400,false,false,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE"));Assert(f.State==FeedbackState.Aborted);});
        Test("external focus control does not invalidate confirmed start or steal focus",()=>
        {var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{var result=Click(d,l);d.Current=30;return result;});Assert(r.Started && !r.Restored && r.ControlTransferred && d.Current==30);var c=Ready();c.BeginAction();c.ActionFinished(r.Started,r.Restored,4000,3900,r.Error,r.ControlTransferred);Assert(c.Starts==1 && c.State==Phase.Performing && !c.WantsStart);});
        Test("unsafe release is never classified as harmless control transfer",()=>
        {var d=new FakeDesktop();var r=FocusTransaction.Run(d,20,2,()=>false,l=>{l.SafeToRestore=false;d.Current=30;return new(false,0,"release unknown");});Assert(!r.ControlTransferred && !r.Restored);});
        Test("ambiguous panel localization cannot authorize entry input",()=>
        {foreach(string state in new[]{"AMBIGUOUS","SEARCH_LIMIT","UNRESOLVED_PANEL_CANDIDATE"})Assert(!EntryReadiness.Confirmed(Entry(1000) with{D2="UNKNOWN",EntryContextConfirmed=true,PanelLocation=state}));});
        Test("old engine payload without movable-panel metadata fails closed",()=>
        {var o=GameSession.ReadObservation("{\"location\":{\"status\":\"UNIQUE_CANDIDATE\",\"candidates\":[{\"bbox\":[2715,1470,2787,1536]}]},\"d2\":{\"state\":\"NOT_VISIBLE\"},\"entry_context\":{\"confirmed\":true}}",1000,1100,true,true,"saved",out _);Assert(o.D2=="NOT_EVALUABLE" && !EntryReadiness.Confirmed(o));});
        Test("offscreen timer offsets do not read unrelated pixels",()=>
        {var r=new CountdownReader().Read(new byte[3840*2088*4],3840,2088,CancellationToken.None,int.MaxValue,0).GetAwaiter().GetResult();Assert(r.Seconds==null && r.Reason=="timer_out_of_frame");});
        Test("one-start limit still prevents restart after manual interruption",()=>
        {var c=Ready(1);c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(14000));c.Observe(Entry(15000));Assert(c.State==Phase.Completed && !c.WantsStart && c.Starts==1);});
        Test("stop between interruption confirmations remains terminal",()=>
        {var c=Ready(2);c.BeginAction();c.ActionFinished(true,true,4000,3900,null);c.Observe(Entry(14000));c.Stop();c.Observe(Entry(15000));Assert(c.State==Phase.Stopped && !c.WantsStart && c.Starts==1);});
        Test("unattended mode permits repeated confirmed takeovers without hourly quota",()=>
        {var p=new TakeoverPolicy(true);for(int n=0;n<20;n++){Assert(p.CanProceed(false));p.Attempted();}});
        Test("first start is immediate and later attended takeovers require approval",()=>
        {var p=new TakeoverPolicy(false);Assert(p.CanProceed(false));p.Attempted();Assert(!p.CanProceed(false));Assert(p.CanProceed(true));p.Attempted();Assert(!p.CanProceed(false));});
        Test("existing performance uses selected repeat mode for its first restart",()=>
        {var p=new TakeoverPolicy(false);p.ExistingPerformanceObserved();Assert(!p.CanProceed(false) && p.CanProceed(true));});
        Test("explicit takeover skips idle timer but never held inputs",()=>
        {Assert(IdlePolicy.CanProceed(true,null,0,false));Assert(IdlePolicy.CanProceed(true,200000,0,false));Assert(!IdlePolicy.CanProceed(true,null,100000,true));Assert(!IdlePolicy.CanProceed(false,1200,0,false));Assert(!IdlePolicy.CanProceed(false,null,100000,false));});
        Test("read-only production loop suppresses start intent and stops at its bound",()=>
        {
            double time=1000;var c=new AutomationController(0,0);int attempts=0;
            int count=ReadOnlyCountdownLoop.Run(c,10000,()=>time,()=>Entry(time),ms=>time+=ms,(n,o,core)=>{if(core.WantsStart)attempts++;},CancellationToken.None);
            Assert(count>=2 && attempts>0 && c.Starts==0 && time==10000 && c.State!=Phase.Starting);
        });
        Test("read-only loop honors adaptive countdown and cancellation",()=>
        {
            double time=1000;var c=new AutomationController(0,0);using var token=new CancellationTokenSource();var delays=new List<int>();
            try{ReadOnlyCountdownLoop.Run(c,200000,()=>time,()=>Panel(time) with{CountdownSeconds=1800-(int)(time/1000),CountdownQuality="DUAL_PASS_AGREEMENT"},ms=>{delays.Add(ms);if(ms==120000)token.Cancel();else time+=ms;},(n,o,core)=>{},token.Token);}catch(OperationCanceledException){}
            Assert(delays.Contains(120000) && c.Starts==0 && !c.WantsStart);
        });
        Test("recognizer child caps BLAS without changing parent environment and preserves arguments",()=>
        {
            string? parent=Environment.GetEnvironmentVariable("OPENBLAS_NUM_THREADS");
            string script=Path.Combine(Path.GetDirectoryName(output)!,"recognizer environment test.py");
            File.WriteAllText(script,"import os,sys,json\nprint(json.dumps({'blas':os.environ.get('OPENBLAS_NUM_THREADS'),'frame':sys.argv[2]}))\n");
            string frame="C:\\saved frames\\带空格 ' $ sample.bmp";
            var start=RecognizerProcess.ForFrame(script,frame);Assert(!start.UseShellExecute && start.CreateNoWindow);
            using var process=System.Diagnostics.Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(10000)){process.Kill(true);throw new Exception("child timeout");}
            Assert(process.ExitCode==0);using var result=JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            Assert(result.RootElement.GetProperty("blas").GetString()=="1" && result.RootElement.GetProperty("frame").GetString()==frame);
            Assert(Environment.GetEnvironmentVariable("OPENBLAS_NUM_THREADS")==parent);
        });
        int replayIndex=Array.IndexOf(args,"--replay-json");
        int panelReplayIndex=Array.IndexOf(args,"--panel-replay-json");
        if(panelReplayIndex>=0)Test("movable-panel production parser retains independent timer offset",()=>
        {
            var o=GameSession.ReadObservation(File.ReadAllText(args[panelReplayIndex+1]),1000,1100,true,true,"synthetic_user_crop_canvas",out _);
            Assert(o.PanelLocated && o.PanelDx==-1178 && o.PanelDy==-1267 && o.D2=="VISIBLE" && CountdownReader.Eligible(o));
            Assert(!EntryReadiness.Confirmed(o with{Location="UNIQUE_CANDIDATE",Count=1,EntryContextConfirmed=true}));
        });
        if(replayIndex>=0)Test("real saved failure frame through production JSON parser and controller (simulated fresh timestamps, zero input)",()=>
        {
            string json=File.ReadAllText(args[replayIndex+1]);var o=GameSession.ReadObservation(json,2000,2100,true,true,"offline_saved_frame",out var icon);
            Assert(o.D2=="UNKNOWN" && o.EntryContextConfirmed && icon is{Width:72,Height:66});
            var c=new AutomationController(2,1000);c.Observe(Panel(1500));c.Observe(Panel(1700));
            for(int i=0;i<4;i++)c.Observe(Panel(3000+i*1000) with{D2="UNKNOWN"});Assert(!c.Done && !c.WantsStart);
            c.Observe(o with{Source=7000,Now=7100});Assert(!c.WantsStart);
            c.Observe(o with{Source=8000,Now=8100});Assert(c.WantsStart && c.Returns==1 && c.Starts==0);
        });
        File.WriteAllText(output,JsonSerializer.Serialize(new{scope="production controller/focus policy with fake desktop; no actual game/window/input calls",passed,total,cases},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"product offline {passed}/{total}");return passed==total?0:9;
    }
}
