using System.Diagnostics;
namespace Int1;
// Source = WGC compositor-render QPC, received = dequeue QPC before ReadPixels.
// UTC is audit metadata only. Both elapsed timestamps are in QPC milliseconds.
internal sealed record FrameTiming(double SourceQpcMs, double ReceivedQpcMs, DateTime ReceivedUtc)
{
    public static double NowMs() => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
    public double AgeMs(double now) => now - SourceQpcMs;
    public bool IsFresh(double now) => double.IsFinite(SourceQpcMs) && double.IsFinite(ReceivedQpcMs)
        && SourceQpcMs > 0 && SourceQpcMs <= ReceivedQpcMs && ReceivedQpcMs <= now
        && AgeMs(now) <= MonitorCore.StaleAfterMs;
    public static FrameTiming OfflineNow() { double t = NowMs(); return new(t, t, DateTime.UtcNow); }
}
