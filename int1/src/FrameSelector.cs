namespace Int1;

// Shared by real WGC and offline injected sources. Only selection/ownership logic lives here.
internal static class FrameSelector
{
    public const int MaxDequeuedFrames = 64;
    internal sealed class Selection<T> : IDisposable where T : class, IDisposable
    {
        public T Frame;
        public double SourceQpcMs, ReceivedQpcMs, StartedQpcMs, CompletedQpcMs;
        public DateTime ReceivedUtc;
        public int DequeuedFrames, EmptyPolls, DiscardedFrames, InvalidTimestamps;
        public bool WaitedForRefresh, DeadlineReached, HitReadLimit;
        public bool SourceAtOrAfterPullStart => Frame != null && SourceQpcMs >= StartedQpcMs;
        public void Dispose() { var f = Frame; Frame = null; f?.Dispose(); }
    }

    public static Selection<T> Select<T>(Func<T> next, Func<T, double> source, int maxWaitMs,
        Func<double> now = null, Action<int> wait = null, Func<DateTime> utc = null) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(next); ArgumentNullException.ThrowIfNull(source);
        if (maxWaitMs < 0 || maxWaitMs > 10000) throw new ArgumentOutOfRangeException(nameof(maxWaitMs));
        now ??= FrameTiming.NowMs; wait ??= Thread.Sleep; utc ??= () => DateTime.UtcNow;
        double last = now();
        if (!double.IsFinite(last)) throw new InvalidOperationException("invalid monotonic clock");
        double started = last, deadline = started + maxWaitMs;
        var result = new Selection<T> { StartedQpcMs = started };
        double Clock()
        {
            double current = now();
            if (!double.IsFinite(current) || current < last) throw new InvalidOperationException("monotonic clock regressed/invalid");
            last = current; return current;
        }
        int polls = 0;
        try
        {
            while (true)
            {
                double tick = Clock();
                if (polls > 0 && tick >= deadline) { result.DeadlineReached = true; break; }
                if (result.DequeuedFrames >= MaxDequeuedFrames) { result.HitReadLimit = true; break; }
                polls++;
                T candidate = next();
                if (candidate != null)
                {
                    result.DequeuedFrames++;
                    try
                    {
                        if (ReferenceEquals(candidate, result.Frame))
                        {
                            candidate = null; // result owns it; outer catch releases it exactly once.
                            throw new InvalidOperationException("source returned an already owned frame");
                        }
                        double received = Clock(), timestamp = source(candidate);
                        if (!double.IsFinite(timestamp) || timestamp <= 0 || timestamp > received)
                        { result.InvalidTimestamps++; continue; }
                        if (result.Frame != null && timestamp <= result.SourceQpcMs) continue;
                        DateTime receivedUtc = utc();
                        var previous = result.Frame;
                        result.Frame = candidate; candidate = null;
                        result.SourceQpcMs = timestamp;
                        result.ReceivedQpcMs = received; result.ReceivedUtc = receivedUtc;
                        if (previous != null) { result.DiscardedFrames++; previous.Dispose(); }
                    }
                    finally
                    {
                        if (candidate != null) { result.DiscardedFrames++; candidate.Dispose(); }
                    }
                    continue; // Drain currently queued frames, keeping the greatest SOURCE time.
                }
                result.EmptyPolls++;
                if (result.SourceAtOrAfterPullStart) break;
                // An empty queue after OLD frames does not prove we saw a current frame.
                // Return buffers as candidates are replaced; poll within the same total wait budget.
                double remaining = deadline - Clock();
                if (remaining <= 0) { result.DeadlineReached = true; break; }
                if (result.Frame != null) result.WaitedForRefresh = true;
                wait((int)Math.Min(20, Math.Ceiling(remaining)));
            }
            result.CompletedQpcMs = Clock();
            // If no newer source arrived, return the unchanged old source for the existing
            // MonitorCore freshness checks. Never stamp a cached frame with pull/processing time.
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
