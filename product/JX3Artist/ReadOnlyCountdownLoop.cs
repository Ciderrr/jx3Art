namespace JX3Artist;

// No desktop/input dependency: both tests and live observation use this exact loop.
internal static class ReadOnlyCountdownLoop
{
    internal static int Run(AutomationController core,double deadline,Func<double> now,Func<Observation> capture,
        Action<int> wait,Action<int,Observation,AutomationController> sample,CancellationToken token,int? intervalMs=null)
    {
        int count=0;
        while(now()<deadline && !token.IsCancellationRequested && !core.Done)
        {
            token.ThrowIfCancellationRequested();double started=now();var observation=capture();core.Observe(observation);sample(++count,observation,core);
            if(core.Done)break;
            double completed=now();int interval=intervalMs??core.NextObservationDelayMs(completed);
            int delay=ObservationSchedule.Plan(started,completed,interval).WaitMs;
            int remaining=(int)Math.Clamp(deadline-now(),0,int.MaxValue);if(remaining<=0)break;
            wait(Math.Min(delay,remaining));
        }
        return count; // WantsStart is diagnostic only; there is no action dispatch here.
    }
}
