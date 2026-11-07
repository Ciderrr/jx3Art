namespace JX3Artist;
internal static class StartupCountdownTests
{
    const string Good="DUAL_PASS_AGREEMENT";
    static void Check(bool ok){if(!ok)throw new Exception("startup countdown expectation failed");}
    static AutomationController Started(bool success=true)
    {
        var core=new AutomationController(3,1000);
        core.Observe(new(2000,2100,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE"));core.Observe(new(3000,3100,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE"));
        core.BeginAction();core.ActionFinished(success,true,10000,8500,null);return core;
    }
    static Observation Panel(double source,double now,int? seconds,string quality=Good)=>new(source,now,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownSeconds:seconds,CountdownQuality:quality);
    internal static void Add(Action<string,Action> test)
    {
        test("startup uses fresh feedback OCR before first returned view without another capture",()=>{
            var core=Started();Check(core.AdoptStartCountdown(new[]{Panel(5000,7000,1799),Panel(8500,9900,1795)},4000,10000));
            Check(core.Countdown.EndSourceMs.HasValue&&core.Countdown.DisplayDeadline.HasValue&&core.Starts==1&&core.Returns==0&&!core.WantsStart);
            Check(core.NextObservationDelayMs(10000)==120000); // Stable cadence resumes immediately.
        });
        test("startup unknown readings use bounded fast acquisition and then original fallback",()=>{
            var core=Started();for(int i=0;i<8;i++)core.Countdown.Unavailable();
            Check(core.NextObservationDelayMs(10000)==1000&&core.NextObservationDelayMs(29999)==1000&&core.NextObservationDelayMs(30000)==30000);
            Check(!core.WantsStart&&core.Starts==1&&core.Countdown.EndSourceMs==null);
            core=Started();core.Observe(Panel(9500,10100,null,"UNKNOWN") with{D2="UNKNOWN"});Check(core.State==Phase.WaitingVisibility&&core.NextObservationDelayMs(10100)==1000&&core.NextObservationDelayMs(30000)==5000);
        });
        test("existing performance also acquires early countdown without counting an automatic start",()=>{
            var core=new AutomationController(3,100);core.Observe(Panel(1000,1100,null,"UNKNOWN"));core.Observe(Panel(2000,2100,null,"UNKNOWN"));
            Check(core.State==Phase.Performing&&core.NextObservationDelayMs(2100)==1000&&core.NextObservationDelayMs(22100)==10000&&core.Starts==0);
        });
        test("startup preserves unknown and cannot use a single reading as confirmed time",()=>{
            var core=Started();Check(!core.AdoptStartCountdown(new[]{Panel(8500,9900,1795)},4000,10000)&&core.Countdown.EndSourceMs==null);
            core=Started();Check(!core.AdoptStartCountdown(new[]{Panel(5000,7000,1799),Panel(6500,8000,1797),Panel(8500,9900,null,"UNKNOWN")},4000,10000)&&core.Countdown.EndSourceMs==null);
        });
        test("startup rejects stale future duplicate pre-input and conflicting feedback",()=>{
            var valid=new[]{Panel(5000,7000,1799),Panel(8500,9900,1795)};
            foreach(var pair in new[]{
                new[]{valid[0] with{Source=4000},valid[1]},
                new[]{valid[0] with{Now=9000},valid[1]},
                new[]{valid[0] with{Now=11000},valid[1]},
                new[]{valid[1],valid[1]},
                new[]{valid[0] with{Identity=false},valid[1]},
                new[]{valid[0] with{Geometry=false},valid[1]},
                new[]{valid[0] with{Location="UNIQUE_CANDIDATE",Count=1},valid[1]},
                new[]{valid[0] with{CountdownSeconds=1500},valid[1]}})
            {var core=Started();Check(!core.AdoptStartCountdown(pair,4000,10000)&&core.Countdown.EndSourceMs==null);}
            Check(!Started().AdoptStartCountdown(valid,4000,12000));
            Check(!Started(false).AdoptStartCountdown(valid,4000,10000));
        });
    }
}
