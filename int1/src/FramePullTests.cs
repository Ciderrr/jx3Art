using System.IO;
using System.Text.Json;
namespace Int1;

internal static class FramePullTests
{
    private sealed class FakeFrame : IDisposable
    {
        public readonly double Source;
        public int Disposals;
        public bool FailDispose;
        public FakeFrame(double source) => Source = source;
        public void Dispose()
        {
            if (++Disposals > 1) throw new InvalidOperationException("double dispose");
            if (FailDispose) throw new IOException("injected-dispose-failure");
        }
    }

    public static List<Program.CaseResult> Run()
    {
        var cases = new List<Program.CaseResult>();
        void Check(string name, string expected, string actual, bool pass) => cases.Add(new() {
            Name = "capture: " + name, Expect = expected, Actual = actual, Pass = pass });

        // Keep the original failing regression assertion intact.
        double clock = 10000; var old = new FakeFrame(8100); var fresh = new FakeFrame(10020);
        bool gaveOld = false, gaveFresh = false;
        FakeFrame Next()
        {
            if (!gaveOld) { gaveOld = true; return old; }
            if (clock >= 10020 && !gaveFresh) { gaveFresh = true; return fresh; }
            return null;
        }
        using var selected = FrameSelector.Select(Next, f => f.Source, 400, () => clock, ms => clock += ms);
        cases.Add(new() {
            Name = "capture: queued-old + empty-poll + new-frame-after-20ms",
            Expect = "source=10020; original source retained; old frame released",
            Actual = $"source={selected.SourceQpcMs}; wait={clock-10000}; oldDisposed={old.Disposals}",
            Pass = selected.SourceQpcMs == 10020 && old.Disposals == 1 && ReferenceEquals(selected.Frame, fresh)
        });
        selected.Dispose(); selected.Dispose();
        Check("ownership-release-idempotent", "each frame disposed once", $"old={old.Disposals};new={fresh.Disposals}", old.Disposals == 1 && fresh.Disposals == 1);

        {
            double t = 10000;
            using var s = FrameSelector.Select<FakeFrame>(() => null, f => f.Source, 65, () => t, ms => t += ms);
            Check("no-frame-bounded-wait", "empty at 65ms, no busy loop", $"elapsed={t-10000};polls={s.EmptyPolls}",
                s.Frame == null && t == 10065 && s.DeadlineReached && s.EmptyPolls == 4);
        }
        {
            double t = 10000; var frame = new FakeFrame(6000); int n = 0;
            DateTime received = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            using var s = FrameSelector.Select(() => ++n == 1 ? frame : null, f => f.Source, 40, () => t, ms => t += ms, () => received);
            var timing = new FrameTiming(s.SourceQpcMs, s.ReceivedQpcMs, s.ReceivedUtc);
            Check("old-fallback-not-restamped", "source=6000 and receive=10000 despite 40ms wait", $"source={s.SourceQpcMs};receive={s.ReceivedQpcMs};now={t}",
                s.SourceQpcMs == 6000 && s.ReceivedQpcMs == 10000 && s.ReceivedUtc == received && t == 10040 && !timing.IsFresh(t));
            Check("old-fallback-wait-diagnostics", "refresh wait reaches deadline", $"waited={s.WaitedForRefresh};deadline={s.DeadlineReached}",
                s.WaitedForRefresh && s.DeadlineReached && !s.SourceAtOrAfterPullStart);
            int detects = 0;
            var gate = new ContentGate(); gate.Set(true, "offline-capture:true");
            var core = new MonitorCore(null, gate, Path.Combine(Path.GetTempPath(), "int1_capture_contract"), () => t,
                (_, _, _, _) => { detects++; throw new InvalidOperationException("stale frame reached bridge"); });
            var loop = new LiveLoopCore(() => t); loop.Start();
            var outcome = loop.Step(() => true, () => new WgcEngine.PullResult {
                Ok = true, FreshFrame = true, Bgra = Array.Empty<byte>(), Timing = timing,
                SurfaceW = 3840, ContentW = 3840, SurfaceH = 2088, ContentH = 2088 }, core, null, 1, false);
            Check("selected-old-reaches-production-stale-gate", "STALE; bridge not called; no result", $"{core.CaptureStatus};detects={detects}",
                core.CaptureStatus == "STALE" && detects == 0 && !outcome.Deterministic && core.LastResult == null && loop.LastResult == null);
        }
        {
            double t = 10000; int calls = 0; var frame = new FakeFrame(9999);
            using var s = FrameSelector.Select(() => { calls++; return frame; }, f => f.Source, 0, () => t, _ => throw new Exception("unexpected wait"));
            Check("zero-budget-one-poll", "one poll; no sleep; source unchanged", $"calls={calls};source={s.SourceQpcMs}", calls == 1 && s.SourceQpcMs == 9999 && s.DeadlineReached);
        }
        {
            double t = 10000; var a = new FakeFrame(10000); var b = new FakeFrame(9990); var sameTime = new FakeFrame(10000); var newest = new FakeFrame(10002);
            var frames = new[] { a, b, sameTime, newest }; int n = 0;
            using var s = FrameSelector.Select(() => { if (n >= frames.Length) return null; t++; return frames[n++]; }, f => f.Source, 40, () => t, ms => t += ms);
            Check("greatest-source-not-last-arrival", "keep 10002, discard older/tie", $"source={s.SourceQpcMs};discard={s.DiscardedFrames}",
                ReferenceEquals(s.Frame, newest) && s.SourceQpcMs == 10002 && s.DiscardedFrames == 3 && a.Disposals == 1 && b.Disposals == 1 && sameTime.Disposals == 1);
            Check("fresh-queue-needs-no-extra-wait", "source >= pull start, end at empty poll", $"now={t};waited={s.WaitedForRefresh}", t == 10004 && !s.WaitedForRefresh && s.SourceAtOrAfterPullStart);
        }
        {
            double t = 10000; var frames = new[] { new FakeFrame(double.NaN), new FakeFrame(double.PositiveInfinity), new FakeFrame(-1), new FakeFrame(0), new FakeFrame(11000) }; int n = 0;
            using var s = FrameSelector.Select(() => n < frames.Length ? frames[n++] : null, f => f.Source, 40, () => t, ms => t += ms);
            Check("invalid-future-timestamps-rejected", "all five rejected/released, no frame", $"invalid={s.InvalidTimestamps};now={t}",
                s.Frame == null && s.InvalidTimestamps == 5 && frames.All(f => f.Disposals == 1) && t == 10040);
        }
        {
            double t = 10000; var good = new FakeFrame(10000); var future = new FakeFrame(12000); int n = 0;
            using var s = FrameSelector.Select(() => ++n == 1 ? good : n == 2 ? future : null, f => f.Source, 40, () => t, ms => t += ms);
            Check("invalid-new-arrival-does-not-replace-valid", "keep valid 10000", $"source={s.SourceQpcMs};futureDisposed={future.Disposals}",
                ReferenceEquals(s.Frame, good) && s.SourceQpcMs == 10000 && future.Disposals == 1 && s.InvalidTimestamps == 1);
        }
        {
            double t = 10000; var frames = new List<FakeFrame>();
            using var s = FrameSelector.Select(() => { var f = new FakeFrame(9000 + frames.Count); frames.Add(f); return f; }, f => f.Source, 400, () => t, ms => t += ms);
            Check("continuous-producer-work-bound", "64 dequeues then return, no infinite drain", $"count={frames.Count};limit={s.HitReadLimit}",
                frames.Count == FrameSelector.MaxDequeuedFrames && s.HitReadLimit && s.SourceQpcMs == 9063 && frames.Take(63).All(f => f.Disposals == 1));
            s.Dispose();
            Check("read-limit-all-frames-released", "all resources released once", $"released={frames.Count(f=>f.Disposals==1)}", frames.All(f => f.Disposals == 1));
        }
        {
            double t = 10000; var f = new FakeFrame(8000); int n = 0; bool threw = false;
            try { using var s = FrameSelector.Select<FakeFrame>(() => ++n == 1 ? f : throw new IOException("injected-read-failure"), x => x.Source, 40, () => t, ms => t += ms); }
            catch (IOException) { threw = true; }
            Check("read-exception-releases-held-frame", "throw and release once", $"threw={threw};disposed={f.Disposals}", threw && f.Disposals == 1);
        }
        {
            double t = 10000; var a = new FakeFrame(8000); var b = new FakeFrame(9000); int n = 0; bool threw = false;
            try { using var s = FrameSelector.Select(() => ++n == 1 ? a : b, f => ReferenceEquals(f,b) ? throw new IOException("injected-time-failure") : f.Source, 40, () => t, ms => t += ms); }
            catch (IOException) { threw = true; }
            Check("timestamp-exception-releases-both", "both frames disposed once", $"threw={threw};a={a.Disposals};b={b.Disposals}", threw && a.Disposals == 1 && b.Disposals == 1);
        }
        {
            double t = 10000; var frame = new FakeFrame(8000); int n = 0; bool threw = false;
            try { using var s = FrameSelector.Select(() => ++n == 1 ? frame : null, f => f.Source, 40, () => t, _ => throw new IOException("injected-wait-failure")); }
            catch (IOException) { threw = true; }
            Check("wait-exception-releases-frame", "frame disposed once", $"threw={threw};disposed={frame.Disposals}", threw && frame.Disposals == 1);
        }
        {
            var frame = new FakeFrame(8000); int clockCalls = 0, frameCalls = 0; bool threw = false;
            try { using var s = FrameSelector.Select(() => ++frameCalls == 1 ? frame : null, f => f.Source, 40, () => ++clockCalls >= 5 ? 9900 : 10000, _ => { }); }
            catch (InvalidOperationException) { threw = true; }
            Check("clock-regression-releases-frame", "reject regressing clock", $"threw={threw};disposed={frame.Disposals}", threw && frame.Disposals == 1);
        }
        {
            double t = 10000; var frame = new FakeFrame(8000); bool threw = false;
            try { using var s = FrameSelector.Select(() => frame, f => f.Source, 40, () => t, ms => t += ms); }
            catch (InvalidOperationException) { threw = true; }
            Check("same-owned-object-rejected", "no double disposal or disposed frame returned", $"threw={threw};disposed={frame.Disposals}", threw && frame.Disposals == 1);
        }
        {
            double t = 10000; var first = new FakeFrame(8000) { FailDispose = true }; var second = new FakeFrame(10000); int n = 0; bool threw = false;
            try { using var s = FrameSelector.Select(() => ++n == 1 ? first : second, f => f.Source, 40, () => t, ms => t += ms); }
            catch (IOException) { threw = true; }
            Check("replacement-dispose-failure-releases-new-owner", "new frame released too", $"threw={threw};first={first.Disposals};second={second.Disposals}", threw && first.Disposals == 1 && second.Disposals == 1);
        }
        {
            var frame = new FakeFrame(10000); bool threw = false;
            try { using var s = FrameSelector.Select(() => frame, f => f.Source, 40, () => 10000, _ => { }, () => throw new IOException("injected-utc-failure")); }
            catch (IOException) { threw = true; }
            Check("utc-metadata-failure-releases-candidate", "candidate released once", $"threw={threw};disposed={frame.Disposals}", threw && frame.Disposals == 1);
        }
        {
            int calls = 0; bool threw = false;
            try { using var s = FrameSelector.Select<FakeFrame>(() => { calls++; return null; }, f => f.Source, -1); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Check("invalid-budget-rejected-before-source", "no source call", $"threw={threw};calls={calls}", threw && calls == 0);
        }
        {
            var rng = new Random(20261027); bool all = true; int iterations = 0;
            for (int i = 0; i < 20; i++)
            {
                double t = 10000; var frames = Enumerable.Range(0, 20).Select(_ => new FakeFrame(rng.Next(1, 10000))).ToArray(); int n = 0;
                using var s = FrameSelector.Select(() => n < frames.Length ? frames[n++] : null, f => f.Source, 20, () => t, ms => t += ms);
                all &= s.SourceQpcMs == frames.Max(f => f.Source) && s.ReceivedQpcMs == 10000;
                s.Dispose(); all &= frames.All(f => f.Disposals == 1); iterations++;
            }
            Check("deterministic-out-of-order-queues", "20 queue permutations keep max source, release all", $"iterations={iterations};all={all}", all && iterations == 20);
        }
        {
            var result = new WgcEngine.PullResult { Ok = true, FreshFrame = true, Bgra = new byte[4],
                Timing = new FrameTiming(10000, 10000, DateTime.UtcNow) };
            result.MarkFailed("injected-post-copy-dispose-failure");
            Check("post-copy-failure-clears-success-and-payload", "error cannot retain Ok/fresh/payload/time", result.Error,
                !result.Ok && !result.FreshFrame && result.Bgra == null && result.Timing == null && result.Error == "injected-post-copy-dispose-failure");
        }
        return cases;
    }

    public static int Command(string output)
    {
        Directory.CreateDirectory(output);
        var cases = Run(); int passed = cases.Count(c => c.Pass);
        foreach (var c in cases) Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Name}: {c.Actual}");
        File.WriteAllText(Path.Combine(output, "capture_tests.json"), JsonSerializer.Serialize(new { mode = "offline; production selector with fake frames/clock; no WGC start", passed, total = cases.Count, cases }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[offline-capture] {passed}/{cases.Count} PASS");
        return passed == cases.Count ? 0 : 9;
    }
}
