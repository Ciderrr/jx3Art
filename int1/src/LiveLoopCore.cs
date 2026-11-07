using System.IO;
using System.Diagnostics;

namespace Int1;

/// <summary>
/// R3: Step coordinates identity, candidate reads, validation and final publication.
/// Tick is the shared state transition function. Its DateTime-only adapter is retained
/// for historical offline tests; the live Step always supplies source QPC provenance.
/// GateRevision is a loop-local diagnostic counter; ContentGate.Revision guards actual results.
/// </summary>
internal sealed class LiveLoopCore
{
    public enum LoopState { NotStarted, Running, Suspended, SuspendedRecovery, Terminated }

    public int IdentityLossLimit = 3;
    public int StaleAfterMs = 3000;

    public LoopState State = LoopState.NotStarted;
    public string Reason = "";
    public int Ticks;                       // 总拍数
    public int PullCalls;                   // 实际发起取帧的拍数（测试断言宽限期为 0）
    public int IdentityLossStreak;
    public long GateRevision = 0;           // Loop-local diagnostic revision
    public string LastInvalidation;
    public JsonDocHolder LastResult;        // 最近一次"被接受"的检测结果；受损事件后置 null
    public DateTime LastAcceptedCaptureUtc = DateTime.MinValue;
    public string LastCaptureStatus = "INIT";
    public List<string> EventLog = new();

    public sealed class JsonDocHolder { public string State = "-"; public int Id; }

    private readonly Func<double> _now;
    private double _recoveryAfter;
    public LiveLoopCore(Func<double> now = null) => _now = now ?? FrameTiming.NowMs;

    public void Start()
    {
        State = LoopState.Running;

        EventLog.Add($"start@tick{Ticks}");
    }

    /// <summary>
    /// 每拍推进。identityValid=HWND+PID 核验结果；frameAvailable=本拍 PullLatest 是否取到帧；
    /// captureTickUtc=帧的捕获到达时间（无帧拍传 DateTime.MinValue）；surfaceOk=尺寸校验结果；
    /// bridgeOk=桥响应校验结果（含 ok/id/结构四态）。
    /// 返回本拍记录（测试与 JSONL 共用）。
    /// </summary>
    public TickOutcome Tick(bool identityValid, bool frameAvailable, DateTime captureTickUtc, bool surfaceOk, bool bridgeOk, int frameId, FrameTiming timing = null, bool contentReady = true, bool healthy = true)
    {
        Ticks++;

        var o = new TickOutcome { Tick = Ticks, FrameId = frameId };

        if (State == LoopState.Terminated)
        {
            o.LoopState = State.ToString(); o.Suspended = true;
            return o;
        }
        // 1) 身份门禁：首拍失效立即暂停，不取帧
        if (!identityValid)
        {
            IdentityLossStreak++;
            Invalidate("identity-lost");
            State = LoopState.Suspended;
            Reason = $"identity-invalid(streak={IdentityLossStreak})";
            if (IdentityLossStreak >= IdentityLossLimit)
            {
                State = LoopState.Terminated;
                Reason = $"identity-lost-{IdentityLossStreak}-ticks->terminate";
            }
            o.LoopState = State.ToString();
            o.Suspended = true;
            o.Pulled = false;
            EventLog.Add($"tick{Ticks}:IDENTITY_LOST(state={State})");
            LastCaptureStatus = State == LoopState.Terminated ? "TERMINATED" : "TARGET_LOST";
            return o;
        }

        IdentityLossStreak = 0;
        // 2) 身份恢复：绝不直接复用；须重新建立（再等一帧健康检查确认）
        if (State == LoopState.Suspended)
        {
            IdentityLossStreak = 0;
            State = LoopState.SuspendedRecovery;
            Reason = "identity-recovered-awaiting-fresh-healthy-frame";
            o.LoopState = State.ToString();
            o.Suspended = true;
            o.Pulled = false;
            EventLog.Add($"tick{Ticks}:IDENTITY_RECOVERED->SUSPENDED_RECOVERY");
            LastCaptureStatus = "TARGET_LOST";
            return o;
        }
        if (State == LoopState.SuspendedRecovery)
        {
            // 恢复期：允许取帧观察，但只有"新鲜+健康+桥 OK"才回 RUNNING
            if (!frameAvailable || !surfaceOk || !bridgeOk || !contentReady || !healthy)
            {
                Reason = $"recovery-pending(frame={frameAvailable},surface={surfaceOk},bridge={bridgeOk})";
                o.LoopState = State.ToString(); o.Suspended = true; o.Pulled = frameAvailable;
                if (frameAvailable) PullCalls++;
                EventLog.Add($"tick{Ticks}:RECOVERY_PENDING");
                LastCaptureStatus = "RECOVERY_PENDING";
                return o;
            }
            double age = (timing == null ? (DateTime.UtcNow - captureTickUtc).TotalMilliseconds : timing.AgeMs(_now()));
            if (age > StaleAfterMs || (timing != null && !timing.IsFresh(_now())))
            {
                Reason = $"recovery-frame-stale({age:F0}ms)";
                o.LoopState = State.ToString(); o.Suspended = true; o.Pulled = true; PullCalls++;
                EventLog.Add($"tick{Ticks}:RECOVERY_STALE");
                return o;
            }
            State = LoopState.Running;
            Reason = "recovery-confirmed(fresh+healthy)";
            EventLog.Add($"tick{Ticks}:RECOVERY_CONFIRMED");
            // 继续走正常帧处理（落到下方 Running 分支）
        }

        if (State == LoopState.Terminated)
        {
            o.LoopState = State.ToString(); o.Suspended = true; o.Pulled = false;
            return o;
        }

        // 3) RUNNING 正常拍
        if (!frameAvailable)
        {
            GateRevision++;
            LastInvalidation = "no-frame";
            LastResult = null;
            LastCaptureStatus = "NO_FRAME";
            Reason = "no new frame this tick";
            o.LoopState = State.ToString(); o.Pulled = false; o.Suspended = false; o.Deterministic = false;
            EventLog.Add($"tick{Ticks}:NO_FRAME(gateRev={GateRevision})");
            return o;
        }
        PullCalls++;

        // 帧时间以有效捕获时间为准：处理旧帧→STALE，不视为新鲜
        double ageMs = (timing == null ? (DateTime.UtcNow - captureTickUtc).TotalMilliseconds : timing.AgeMs(_now()));
        if (ageMs > StaleAfterMs || (timing != null && !timing.IsFresh(_now())))
        {
            GateRevision++;
            LastInvalidation = "stale-capture-time";
            LastResult = null;
            LastCaptureStatus = "STALE";
            Reason = $"capture time aged {ageMs:F0}ms";
            o.LoopState = State.ToString(); o.Pulled = true; o.Suspended = false; o.Deterministic = false;
            EventLog.Add($"tick{Ticks}:STALE(gateRev={GateRevision})");
            return o;
        }
        if (!surfaceOk)
        {
            GateRevision++;
            LastInvalidation = "size-mismatch";
            LastResult = null;
            LastCaptureStatus = "SIZE_MISMATCH";
            Reason = "surface size mismatch";
            o.LoopState = State.ToString(); o.Pulled = true; o.Deterministic = false;
            EventLog.Add($"tick{Ticks}:SIZE_MISMATCH(gateRev={GateRevision})");
            return o;
        }
        if (!bridgeOk)
        {
            GateRevision++;
            LastInvalidation = "bridge-failed";
            LastResult = null;
            LastCaptureStatus = "BRIDGE_FAILED";
            Reason = "bridge response rejected";
            o.LoopState = State.ToString(); o.Pulled = true; o.Deterministic = false;
            EventLog.Add($"tick{Ticks}:BRIDGE_FAILED(gateRev={GateRevision})");
            return o;
        }

        if (!contentReady || !healthy)
        {
            LastResult = null;
            LastCaptureStatus = "OK";
            Reason = "observation-only(content unknown/not ready or non-deterministic detector state)";
            o.LoopState = State.ToString(); o.Pulled = true;
            return o;
        }
        Reason = "validated-fresh-frame";
        LastAcceptedCaptureUtc = captureTickUtc;
        LastResult = new JsonDocHolder { State = "DETERMINISTIC", Id = frameId };
        LastCaptureStatus = "OK";
        o.LoopState = State.ToString(); o.Pulled = true; o.Deterministic = true;
        EventLog.Add($"tick{Ticks}:OK(id={frameId})");
        return o;
    }

    // Shared live orchestration. Tests inject sources, never duplicate this decision path.
    public TickOutcome Step(Func<bool> identity, Func<WgcEngine.PullResult> pull,
        MonitorCore core, StreamWriter log, int frameId, bool render = true)
    {
        WgcEngine.PullResult candidate = null;
        if (State == LoopState.Terminated) return Tick(true, false, DateTime.MinValue, false, false, frameId);
        if (!identity())
        {
            var lost = Tick(false, false, DateTime.MinValue, false, false, frameId);
            core.MarkTargetLost(Reason, render: false);
            Write(lost); return lost;
        }
        if (State == LoopState.Suspended)
        {
            State = LoopState.SuspendedRecovery;
            IdentityLossStreak = 0;
            _recoveryAfter = _now();
            core.PauseRecovery("identity-recovered-awaiting-new-source-frame");
        }
        bool recovering = State == LoopState.SuspendedRecovery;
        var f = candidate = pull(); // Recovery permits reading candidates, independently of publication.
        bool available = f.Ok && f.FreshFrame && f.Bgra != null && f.Timing != null;
        bool sizeOk = f.SurfaceW == MonitorCore.ExpectedW && f.SurfaceH == MonitorCore.ExpectedH
            && f.ContentW == MonitorCore.ExpectedW && f.ContentH == MonitorCore.ExpectedH;
        bool accepted = false;
        if (available && (!recovering || f.Timing.SourceQpcMs > _recoveryAfter))
        {
            core.ProcessFrame(f.Bgra, sizeOk ? f.SurfaceW : 0, f.SurfaceH, true, null, out _, f.Timing, render: false);
            accepted = core.CaptureStatus == "OK";
        }
        else if (recovering) core.PauseRecovery("awaiting-valid-new-source-frame");
        else core.ProcessNoFrame(null, render: false);

        // Revalidate after pixel read / encoding / bridge latency, before rendering.
        if (!identity())
        {
            var lost = Tick(false, false, DateTime.MinValue, false, false, frameId);
            core.MarkTargetLost(Reason, render: false); Write(lost); return lost;
        }
        var outcome = Tick(true, available, f.Timing?.ReceivedUtc ?? DateTime.MinValue,
            sizeOk, accepted, frameId, f.Timing, core.ContentReady, core.HealthyResult);
        if (recovering && (!accepted || !outcome.Deterministic))
            core.PauseRecovery("recovery-requires-new-fresh-healthy-frame-and-content-true");
        else if (core.LastResult != null && !core.CanPublish)
            core.InvalidateResult("loop-rejected-result");
        Write(outcome);
        return outcome;

        void Write(TickOutcome o)
        {
            log?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                tick = o.Tick, loopState = o.LoopState, suspended = o.Suspended,
                pulled = o.Pulled, deterministic = o.Deterministic,
                captureStatus = core.CaptureStatus, reason = core.PauseReason,
                loopReason = Reason,
                resultAccepted = core.CanPublish,
                result = core.CanPublish ? core.LastResult.RootElement.Clone() : (System.Text.Json.JsonElement?)null,
                imageTransport = "lossless-bmp-bounded",
                latestSnapshotPath = core.LastSnapshotPath,
                sourceAgeAtRecordMs = core.SourceAgeMs,
                pullStartedQpcMs = candidate?.PullStartedQpcMs,
                selectionStartedQpcMs = candidate?.SelectionStartedQpcMs,
                selectionCompletedQpcMs = candidate?.SelectionCompletedQpcMs,
                pixelsReadCompletedQpcMs = candidate?.PixelsReadCompletedQpcMs,
                pullCompletedQpcMs = candidate?.PullCompletedQpcMs,
                drainedFrames = candidate?.DrainedFrames,
                noFramePolls = candidate?.NoFramePolls,
                discardedFrames = candidate?.DiscardedFrames,
                invalidFrameTimestamps = candidate?.InvalidFrameTimestamps,
                waitedForRefresh = candidate?.WaitedForRefresh,
                refreshDeadlineReached = candidate?.RefreshDeadlineReached,
                frameReadLimitReached = candidate?.FrameReadLimitReached,
                sourceAtOrAfterPullStart = candidate?.SourceAtOrAfterPullStart,
                pullError = candidate?.Error,
                encodingStartedQpcMs = core.EncodingStartedQpcMs,
                encodingCompletedQpcMs = core.EncodingCompletedQpcMs,
                sourceQpcMs = core.LastTiming?.SourceQpcMs,
                receivedQpcMs = core.LastTiming?.ReceivedQpcMs,
                receivedUtc = core.LastTiming?.ReceivedUtc,
                detectionStartedQpcMs = core.DetectionStartedQpcMs,
                detectionCompletedQpcMs = core.DetectionCompletedQpcMs }));
            if (render) core.Render();
        }
    }

    /// <summary>人工 y/n/u：门禁切换立即清空旧结果（旧结果不得重新显示）。</summary>
    public void OnGateChanged(string source)
    {
        GateRevision++;
        LastInvalidation = $"gate-changed:{source}";
        LastResult = null;

        EventLog.Add($"gate-changed({source})(gateRev={GateRevision})");
    }

    private void Invalidate(string why)
    {
        GateRevision++;
        LastInvalidation = why;
        LastResult = null;
    }

    public sealed class TickOutcome
    {
        public int Tick, FrameId;
        public string LoopState = "";
        public bool Suspended, Pulled, Deterministic;
    }
}
