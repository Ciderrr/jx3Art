namespace JX3Artist;
internal sealed record ForegroundPreparationResult(bool Requested,bool Activated,string Reason,int Attempts);
internal sealed class PreparationForeground
{
    int attempts;double next;
    internal static bool SlowEntry(Observation o)=>o.Identity&&o.Geometry&&EntryReadiness.Confirmed(o)&&double.IsFinite(o.Now)&&double.IsFinite(o.Source)&&o.Now>=o.Source&&o.Now-o.Source>=2500;
    internal ForegroundPreparationResult Request(double now,bool authorized,bool released,bool alreadyActive,Func<bool> activate,CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if(!authorized||!released||alreadyActive)return new(false,alreadyActive,"not_required_or_not_authorized",attempts);
        if(attempts>=3||now<next)return new(false,false,"bounded_foreground_requests",attempts);
        attempts++;next=now+2000;
        try{return new(true,activate(),"supported_window_activation",attempts);}catch(OperationCanceledException){throw;}catch(Exception ex){return new(true,false,ex.GetType().Name,attempts);}
    }
    internal void Clear(){attempts=0;next=0;}
    internal static void AddTests(Action<string,Action> test)
    {
        test("authorized background preparation can restore assistant without waiting for manual activation",()=>{
            var recovery=new PreparationForeground();int called=0;
            if(recovery.Request(1000,false,true,false,()=>{called++;return true;},CancellationToken.None).Requested||called!=0)throw new Exception("unapproved foreground");
            if(recovery.Request(1000,true,false,false,()=>{called++;return true;},CancellationToken.None).Requested)throw new Exception("held keys foreground");
            var r=recovery.Request(1000,true,true,false,()=>{called++;return true;},CancellationToken.None);if(!r.Activated||called!=1)throw new Exception("background activation not dispatched");
            if(recovery.Request(1500,true,true,false,()=>true,CancellationToken.None).Requested)throw new Exception("foreground spam");
            recovery.Request(4000,true,true,false,()=>false,CancellationToken.None);recovery.Request(7000,true,true,false,()=>false,CancellationToken.None);if(recovery.Request(10000,true,true,false,()=>true,CancellationToken.None).Requested)throw new Exception("unbounded foreground");
            recovery.Clear();if(!recovery.Request(10000,true,true,false,()=>true,CancellationToken.None).Activated)throw new Exception("new cycle budget not reset");
        });
        test("slow entry recovery never grants input from stale pixels",()=>{
            var o=new Observation(1000,4200,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true);
            if(!SlowEntry(o)||PreparedEntryPolicy.Ready(o,true,o.Now)||SlowEntry(o with{D2="VISIBLE"})||SlowEntry(o with{Identity=false}))throw new Exception("stale recovery authority");
        });
    }
}
