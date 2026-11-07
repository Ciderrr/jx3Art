using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Int1;

/// <summary>
/// 监控核心（R1 加固版）。新增/强化的安全契约：
///  1. 帧新鲜度双检：传递 WGC 原始来源 QPC 时间；桥返回后复查帧龄，>3000ms → STALE，确定性结果作废；
///  2. 桥任何失败（ok=false/结构非法/id 错配/超时失同步/死亡）→ CaptureStatus=BRIDGE_*，
///     确定性输出立即清除（LastResult 置 null，显示 "(no valid state)"，不显示 held 的确定性结论）；
///  3. 帧源受损事件（尺寸不符/无新帧→STALE/桥故障/身份失效）→ gate.InvalidateForFrameSourceEvent：
///     在途检测结果因 Revision 变化被废弃，绝不继承旧内容状态继续判定；
///  4. 人工 n/u 切换门禁 → 立即失效当前确定性显示（Render 输出 invalidated 标记）。
/// </summary>
internal sealed class MonitorCore
{
    public const int ExpectedW = 3840, ExpectedH = 2088;
    public const int StaleAfterMs = 3000;

    public string CaptureStatus = "INIT";
    public string PauseReason = "";
    public string LastCaptureStatus => CaptureStatus;
    public DateTime LastValidFrameUtc;
    public string LastFrameInfo = "-";
    public JsonDocument LastResult;
    public long LastResultGateRevision = -1;   // 结果所属的门禁+帧源版本（不匹配即作废）

    private readonly PythonBridge _bridge;
    private readonly ContentGate _gate;
    private readonly string _tmpDir;
    private int _frameId;
    private readonly BitmapFrameTransport _transport;
    public string LastSnapshotPath;
    private readonly Func<double> _now;
    private readonly Func<string, bool?, int, int, PythonBridge.BridgeResult> _detect;
    public FrameTiming LastTiming;
    public double? DetectionStartedQpcMs, DetectionCompletedQpcMs;
    public double? EncodingStartedQpcMs, EncodingCompletedQpcMs;
    public double? SourceAgeMs => LastTiming?.AgeMs(_now());
    public bool CanPublish => LastResult != null && CaptureStatus == "OK"
        && LastResultGateRevision == _gate.Revision && LastTiming?.IsFresh(_now()) == true;
    public bool ContentReady => _gate.Value == true;
    public bool HealthyResult => LastResult != null && new[] { "d1", "d2" }.All(k =>
        StrState(LastResult, k) is "VISIBLE" or "NOT_VISIBLE");

    public MonitorCore(PythonBridge bridge, ContentGate gate, string tmpDir,
        Func<double> now = null,
        Func<string, bool?, int, int, PythonBridge.BridgeResult> detect = null, BitmapFrameTransport transport = null)
    {
        _bridge = bridge; _gate = gate; _tmpDir = tmpDir;
        _now = now ?? FrameTiming.NowMs; _detect = detect ?? bridge.Detect;
        Directory.CreateDirectory(tmpDir);
        _transport = transport ?? new BitmapFrameTransport(tmpDir);
    }

    /// <summary>处理一帧（live=WGC 字节；offline=解码字节）。返回 null=本拍无有效确定性输出。</summary>
    public string ProcessFrame(byte[] bgra, int w, int h, bool fresh, StreamWriter jsonl, out JsonDocument result, FrameTiming timing = null, bool render = true)
    {
        result = null;
        _frameId++;
        long t0 = Stopwatch.GetTimestamp();
        // Only decoded offline images omit timing. Live supplies WGC provenance.
        timing ??= FrameTiming.OfflineNow();
        LastTiming = timing;
        DetectionStartedQpcMs = DetectionCompletedQpcMs = null;
        EncodingStartedQpcMs = EncodingCompletedQpcMs = null;
        LastSnapshotPath = null;
        DateTime capturedUtc = timing.ReceivedUtc;
        long gateRevAtDispatch = _gate.Revision;

        if (!fresh || !timing.IsFresh(_now()))
        {
            CaptureStatus = "STALE";
            PauseReason = "frame source timestamp stale/invalid or not fresh";
            InvalidateResult("frame-source-event:STALE-before-detect");
            jsonl?.WriteLine(JsonSerializer.Serialize(MakeRecord(_frameId, null, null, Ms(t0), gateRevAtDispatch), JsonOpts()));
            if (render) Render();
            return null;
        }

        if (w != ExpectedW || h != ExpectedH)
        {
            CaptureStatus = "SIZE_MISMATCH";
            PauseReason = $"frame {w}x{h} ≠ expected {ExpectedW}x{ExpectedH}";
            LastFrameInfo = $"{w}x{h} contentMismatch";
            InvalidateResult($"frame-source-event:{CaptureStatus}");           // 帧源受损：在途/旧结果作废
            var rec1 = MakeRecord(_frameId, null, null, Ms(t0), gateRevAtDispatch);
            jsonl?.WriteLine(JsonSerializer.Serialize(rec1, JsonOpts()));
            if (render) Render();
            return null;
        }

        PythonBridge.BridgeResult br;
        try
        {
            EncodingStartedQpcMs = _now();
            using var frameFile = _transport.Stage(bgra,w,h,_frameId);
            EncodingCompletedQpcMs = _now();
            LastValidFrameUtc = capturedUtc;
            LastFrameInfo = $"{w}x{h} id={_frameId}";
            // Encoding/storage time also consumes the original source freshness budget.
            if (!timing.IsFresh(_now()))
            {
                CaptureStatus = "STALE"; PauseReason = "frame aged during image transfer before bridge dispatch";
                InvalidateResult("frame-source-event:STALE-before-dispatch");
                jsonl?.WriteLine(JsonSerializer.Serialize(MakeRecord(_frameId,null,null,Ms(t0),gateRevAtDispatch),JsonOpts()));
                if (render) Render();
                return null;
            }
            DetectionStartedQpcMs = _now();
            br = _detect(frameFile.Path, _gate.Value, _frameId, 15000);
            DetectionCompletedQpcMs = _now();
            LastSnapshotPath = frameFile.KeepLatest();
        }
        catch (Exception ex)
        {
            CaptureStatus = "FRAME_TRANSFER_FAILED";
            PauseReason = $"{ex.GetType().Name}: {ex.Message}";
            InvalidateResult("frame-source-event:FRAME_TRANSFER_FAILED");
            jsonl?.WriteLine(JsonSerializer.Serialize(MakeRecord(_frameId,null,null,Ms(t0),gateRevAtDispatch),JsonOpts()));
            if (render) Render();
            return null;
        }
        if (!br.Ok)
        {
            CaptureStatus = br.Error.StartsWith("bridge-timeout") ? "BRIDGE_TIMEOUT" : "BRIDGE_FAILED";
            PauseReason = br.Error;
            InvalidateResult($"frame-source-event:{CaptureStatus}");           // 桥故障=帧源受损
        }
        else
        {
            // ② 桥返回后复查帧龄（检测在途期间可能已过 3s）
            double ageMs = timing.AgeMs(_now());
            if (!timing.IsFresh(_now()))
            {
                CaptureStatus = "STALE";
                PauseReason = $"frame aged {ageMs:F0}ms during detection (> {StaleAfterMs}ms)";
                InvalidateResult($"frame-source-event:STALE-inflight");
            }
            else if (_gate.Revision != gateRevAtDispatch)
            {
                // ③ 检测在途期间门禁/帧源版本变化 → 结果作废（本拍无确定性输出，状态标 GATE_CHANGED）
                CaptureStatus = "GATE_CHANGED";
                PauseReason = $"content gate revision changed during detection ({gateRevAtDispatch}->{_gate.Revision}; {_gate.LastInvalidation ?? _gate.Source})";
                InvalidateResult("gate-changed-inflight");
            }
            else
            {
                CaptureStatus = "OK";
                PauseReason = "";
                result = br.Document;
                LastResult = result;
                LastResultGateRevision = gateRevAtDispatch;
            }
        }
        double ms = Ms(t0);

        object d1s = null, d2s = null;
        if (br.Document != null) { d1s = Extract(br.Document, "d1"); d2s = Extract(br.Document, "d2"); }
        var rec = MakeRecord(_frameId, d1s, d2s, ms, gateRevAtDispatch);
        jsonl?.WriteLine(JsonSerializer.Serialize(rec, JsonOpts()));
        if (render) Render();
        return result is null ? null : JsonSerializer.Serialize(result, JsonOpts());
    }

    /// <summary>无新帧拍：检查陈旧并更新状态。</summary>
    public void ProcessNoFrame(StreamWriter jsonl, bool render = true)
    {
        var age = LastTiming == null ? (LastValidFrameUtc == default ? -1 : (DateTime.UtcNow - LastValidFrameUtc).TotalMilliseconds) : LastTiming.AgeMs(_now());
        if (age > StaleAfterMs && CaptureStatus is not "BRIDGE_FAILED" and not "TARGET_LOST" and not "BRIDGE_TIMEOUT" and not "GATE_CHANGED")
        {
            CaptureStatus = "STALE";
            PauseReason = $"no fresh frame for {age:F0}ms (prev: {PauseReason})";
            InvalidateResult("frame-source-event:STALE");
        }
        else if (CaptureStatus == "OK")
        {
            CaptureStatus = "NO_FRAME";
            PauseReason = "no new frame this tick (minimized or paused presenter?)";
            InvalidateResult("frame-source-event:NO_FRAME");
        }
        jsonl?.WriteLine(JsonSerializer.Serialize(new
        {
            id = _frameId,
            ts = DateTime.Now.ToString("o"),
            captureStatus = CaptureStatus,
            pauseReason = PauseReason,
            lastValidFrameAgeMs = age < 0 ? (double?)null : Math.Round(age, 0),
        }, JsonOpts()));
        if (render) Render();
    }

    /// <summary>身份失效（live）：立即暂停一切输出确定性状态（首拍即生效）。</summary>
    public void MarkTargetLost(string reason, bool render = true)
    {
        CaptureStatus = "TARGET_LOST";
        PauseReason = reason;
        InvalidateResult("frame-source-event:TARGET_LOST");
        if (render) Render();
    }

    /// <summary>人工 n/u 切换门禁后：旧确定性结果立即失效。</summary>
    public void InvalidateResult(string reason)
    {
        if (LastResult != null)
        {
            LastResult = null;
            LastResultGateRevision = -1;
            LastInvalidationNote = reason;
        }
        else LastInvalidationNote = reason;
        _gate.InvalidateForFrameSourceEvent(reason);
    }
    public string LastInvalidationNote;

    /// <summary>R2：live 人工 y/n/u 的完整入口——更新上游值（Revision++）并立即失效旧确定性显示。</summary>
    public void SetGateValue(bool? v, string source)
    {
        _gate.Set(v, source);
        LastResult = null;
        LastResultGateRevision = -1;
        LastInvalidationNote = $"gate-set:{source}";
        // 注意：此处不再重复 InvalidateForFrameSourceEvent——Set 已递增 Revision，
        // 避免 live 双计数；旧结果清除语义与 InvalidateResult 一致。
    }

    private object MakeRecord(int id, object d1, object d2, double ms, long gateRev) => new
    {
        id,
        ts = DateTime.Now.ToString("o"),
        captureStatus = CaptureStatus,
        pauseReason = PauseReason,
        contentReady = _gate.Value switch { true => "True", false => "False", _ => "None" },
        contentSource = _gate.Source,
        gateRevisionAtDispatch = gateRev,
        gateRevisionNow = _gate.Revision,
        resultAccepted = CanPublish,
        sourceQpcMs = LastTiming?.SourceQpcMs,
        receivedQpcMs = LastTiming?.ReceivedQpcMs,
        receivedUtc = LastTiming?.ReceivedUtc,
        imageTransport = "lossless-bmp-bounded",
        latestSnapshotPath = LastSnapshotPath,
        encodingStartedQpcMs = EncodingStartedQpcMs,
        encodingCompletedQpcMs = EncodingCompletedQpcMs,
        sourceAgeAtRecordMs = SourceAgeMs,
        detectionStartedQpcMs = DetectionStartedQpcMs,
        detectionCompletedQpcMs = DetectionCompletedQpcMs,
        detectTotalMs = Math.Round(ms, 1),
        d1, d2,
    };

    private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency * 1000;

    private static object Extract(JsonDocument doc, string which)
    {
        if (!doc.RootElement.TryGetProperty(which, out var n)) return null;
        var d = new Dictionary<string, object>();
        foreach (var p in n.EnumerateObject())
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => p.Value.ToString(),
            };
        return d;
    }

    public void PauseRecovery(string reason)
    {
        CaptureStatus = "RECOVERY_PENDING";
        PauseReason = reason;
        InvalidateResult(reason);
    }

    public void Render()
    {
        var fg = Console.ForegroundColor;
        Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
        Console.ForegroundColor = CaptureStatus == "OK" ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write($"WGC:{CaptureStatus}");
        Console.ForegroundColor = fg;
        Console.Write($" | CONTENT_READY:{(_gate.Value switch { true => "True", false => "False", _ => "None" })}");

        string d1 = "-", d2 = "-", detMs = "-";
        if (CanPublish)
        {
            d1 = StrState(LastResult, "d1");
            d2 = StrState(LastResult, "d2");
            detMs = StrMs(LastResult);
        }
        else if (LastInvalidationNote is { Length: > 0 } && CaptureStatus is "BRIDGE_FAILED" or "BRIDGE_TIMEOUT" or "SIZE_MISMATCH" or "STALE" or "NO_FRAME" or "TARGET_LOST" or "GATE_CHANGED" or "FRAME_TRANSFER_FAILED" or "RECOVERY_PENDING" or "INIT")
        {
            d1 = "(no valid state)"; d2 = "(no valid state)";
        }
        Console.Write($" | D1:{d1} | D2:{d2} | det:{detMs}ms");
        Console.WriteLine($" | frame:{LastFrameInfo} sourceAge:{(SourceAgeMs is double age ? Math.Round(age / 1000, 2) + "s" : "-")}");
        if (PauseReason is { Length: > 0 })
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    ↳ reason: {PauseReason}");
            Console.ForegroundColor = fg;
        }
    }

    private static string StrState(JsonDocument d, string k) =>
        d.RootElement.TryGetProperty(k, out var n) && n.TryGetProperty("state", out var s) ? s.GetString() : "-";
    private static string StrMs(JsonDocument d)
    {
        double t = 0;
        if (d.RootElement.TryGetProperty("d1", out var a) && a.TryGetProperty("detect_ms", out var m1)) t += m1.GetDouble();
        if (d.RootElement.TryGetProperty("d2", out var b) && b.TryGetProperty("detect_ms", out var m2)) t += m2.GetDouble();
        return t.ToString("F0");
    }

    public static void SavePng(byte[] bgra, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }

    public static JsonSerializerOptions JsonOpts() => new() { WriteIndented = false };
}
