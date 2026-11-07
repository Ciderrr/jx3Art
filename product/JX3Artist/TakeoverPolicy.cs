namespace JX3Artist;
// Explicit Start authorizes the first transaction. Later transactions follow the selected mode.
internal sealed class TakeoverPolicy(Func<bool> unattended)
{
    internal TakeoverPolicy(bool unattended):this(()=>unattended){}
    bool attempted;
    internal bool HasInitialPermission=>!attempted;
    internal bool CanProceed(bool approved)=>!attempted || unattended() || approved;
    internal void ExistingPerformanceObserved()=>attempted=true;
    internal void Attempted()=>attempted=true;
}
