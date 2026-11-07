namespace JX3Artist;

internal static class IdlePolicy
{
    internal static bool CanProceed(bool explicitPermission,uint? required,uint elapsed,bool held)=>!held && (explicitPermission || required.HasValue && elapsed>=required.Value);
    // Read-only conservative eligibility hint, not a promise Windows will activate.
    internal static uint? RequiredMs(bool ownOrGameForeground,uint? foregroundLockMs)=>ownOrGameForeground?1200u:
        foregroundLockMs.HasValue?Math.Max(1200u,foregroundLockMs.Value):null;
}
