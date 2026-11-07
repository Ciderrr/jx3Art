namespace JX3Artist;
// Intervals are start-to-start; recognition time consumes the interval, not an
// extra delay added after it. Work slower than the target never overlaps itself.
internal static class ObservationSchedule
{
    internal static (double NextCheck,int WaitMs) Plan(double started,double completed,int intervalMs)
    {
        if(!double.IsFinite(started)||!double.IsFinite(completed)||completed<started||intervalMs<1)throw new ArgumentException("invalid observation timing");
        double next=Math.Max(completed,started+intervalMs);
        return(next,(int)Math.Clamp(Math.Ceiling(next-completed),0,int.MaxValue));
    }
}
