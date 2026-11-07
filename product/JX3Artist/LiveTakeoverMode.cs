namespace JX3Artist;
// Revoking automatic permission invalidates an in-flight pre-click permit.
// Explicit Start/Allow permission remains scoped to its existing transaction.
internal sealed class LiveTakeoverMode
{
    readonly object gate=new();bool enabled;long generation;
    internal readonly AutoResetEvent Changed=new(false);
    internal bool Enabled{get{lock(gate)return enabled;}}
    internal void Set(bool value){lock(gate){if(enabled==value)return;enabled=value;generation++;}Changed.Set();}
    internal Func<bool> Permit(bool explicitPermission)
    {
        if(explicitPermission)return ()=>true;
        long at;lock(gate)at=generation;
        return ()=>{lock(gate)return enabled&&at==generation;};
    }
    internal T Dispatch<T>(Func<bool> permitted,Func<T> submit)
    {
        lock(gate){if(!permitted())throw new OperationCanceledException("自动接管已取消，未点击");return submit();}
    }
    internal static void AddTests(Action<string,Action> test)
    {
        test("live mode revokes automatic permits and re-enabling cannot revive them",()=>{
            var mode=new LiveTakeoverMode();mode.Set(true);var old=mode.Permit(false);if(!old())throw new Exception("automatic permission missing");
            mode.Set(false);if(old())throw new Exception("automatic permission retained");mode.Set(true);if(old()||!mode.Permit(false)())throw new Exception("revoked transaction revived");
            int inputs=0;try{mode.Dispatch(old,()=>++inputs);throw new Exception("revoked dispatch accepted");}catch(OperationCanceledException){}if(inputs!=0)throw new Exception("revoked input sent");
        });
        test("live mode never revokes explicit Start or Allow and wakes observation wait",()=>{
            var mode=new LiveTakeoverMode();var explicitPermit=mode.Permit(true);mode.Set(true);mode.Set(false);
            if(!explicitPermit()||!mode.Changed.WaitOne(0)||mode.Permit(false)())throw new Exception("mode permission semantics");
            var policy=new TakeoverPolicy(()=>mode.Enabled);policy.ExistingPerformanceObserved();if(policy.CanProceed(false))throw new Exception("unapproved renewal");mode.Set(true);if(!policy.CanProceed(false))throw new Exception("live enabling ignored");mode.Set(false);if(policy.CanProceed(false)||!policy.CanProceed(true))throw new Exception("live disabling ignored");
        });
    }
}
