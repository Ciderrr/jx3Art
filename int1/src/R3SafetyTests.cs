using System.IO;
using System.Text.Json;
namespace Int1;

// Offline simulations of the SAME Step used by CmdLive. No WGC start / OS identity calls.
internal static class R3SafetyTests
{
    public static void Run(List<Program.CaseResult> cases, string tmp)
    {
        double clock = 10000;
        bool identity = true;
        int pulls = 0, detections = 0;
        double delay = 0;
        bool unhealthy = false, failBridge = false;
        Action inDetect = null;
        var gate = new ContentGate(); gate.Set(true, "test:true");
        var buffer = new byte[MonitorCore.ExpectedW * MonitorCore.ExpectedH * 4];
        var core = new MonitorCore(null, gate, tmp, () => clock, (path, ready, id, timeout) =>
        {
            detections++;
            clock += delay;
            inDetect?.Invoke();
            string state = unhealthy ? "NOT_EVALUABLE" : ready == true ? "VISIBLE" : ready == false ? "NOT_EVALUABLE" : "UNKNOWN";
            return new PythonBridge.BridgeResult {
                Ok = !failBridge, Error = failBridge ? "injected-bridge-failure" : null,
                Document = JsonDocument.Parse($"{{\"ok\":true,\"id\":{id},\"d1\":{{\"state\":\"{state}\"}},\"d2\":{{\"state\":\"{state}\"}}}}") };
        });
        var loop = new LiveLoopCore(() => clock); loop.Start();
        WgcEngine.PullResult frame = null;
        int id = 0;
        LiveLoopCore.TickOutcome Step() => loop.Step(() => identity, () => { pulls++; return frame; }, core, null, ++id, false);
        WgcEngine.PullResult NewFrame(double age = 0) => new() {
            Ok = true, FreshFrame = true, Bgra = buffer,
            SurfaceW = MonitorCore.ExpectedW, SurfaceH = MonitorCore.ExpectedH,
            ContentW = MonitorCore.ExpectedW, ContentH = MonitorCore.ExpectedH,
            Timing = new FrameTiming(clock - age, clock, DateTime.UtcNow) };
        void Check(string n, bool ok, string actual) => cases.Add(new() {
            Name = "R3 " + n, Expect = "production safety contract", Actual = actual, Pass = ok });
        bool Hidden() => core.LastResult == null && loop.LastResult == null;
        string Display()
        {
            var saved = Console.Out; using var text = new StringWriter();
            try { Console.SetOut(text); core.Render(); return text.ToString(); }
            finally { Console.SetOut(saved); }
        }
        frame = NewFrame(); var seed = Step();
        Check("seed-real-state", seed.Deterministic && core.HealthyResult, core.CaptureStatus);
        identity = false; int before = pulls; var lost = Step();
        Check("identity-first-loss-clears-both-and-display", lost.Suspended && Hidden() && pulls == before
            && core.CaptureStatus == "TARGET_LOST" && !Display().Contains("D1:VISIBLE"), $"{loop.State}/{core.CaptureStatus}/pulls={pulls-before}");
        var grace = Step();
        Check("identity-grace-no-pull", grace.Suspended && Hidden() && pulls == before, loop.State.ToString());
        identity = true; frame = new(); var pending = Step();
        Check("recovery-probes-no-frame", pulls == before + 1 && loop.State == LiveLoopCore.LoopState.SuspendedRecovery && Hidden(), loop.State.ToString());
        before = detections; clock += 20; frame = NewFrame(100); Step();
        Check("recovery-pre-rebind-frame-rejected", detections == before && Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, $"detectDelta={detections-before}");
        clock += 20; frame = NewFrame(); unhealthy = true; Step();
        Check("recovery-degenerate-frame-rejected", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, core.CaptureStatus);
        unhealthy = false; gate.Set(null, "test:none"); clock += 20; frame = NewFrame(); Step();
        Check("recovery-content-none-rejected", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, core.CaptureStatus);
        gate.Set(false, "test:false"); clock += 20; frame = NewFrame(); Step();
        Check("recovery-content-false-rejected", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, core.CaptureStatus);
        gate.Set(true, "test:true"); clock += 20; frame = NewFrame(); frame.ContentW = 1; Step();
        Check("recovery-content-size-rejected", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, core.CaptureStatus);
        clock += 20; frame = NewFrame(); failBridge = true; Step();
        Check("recovery-bridge-failure-rejected", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, core.CaptureStatus);
        failBridge = false; clock += 20; frame = NewFrame(); var recovered = Step();
        Check("recovery-fresh-healthy-content-true", recovered.Deterministic && core.HealthyResult && loop.State == LiveLoopCore.LoopState.Running, loop.State.ToString());
        clock += 4000; before = detections; var stale = Step();
        Check("cached-old-frame-not-restamped", !stale.Deterministic && Hidden() && core.CaptureStatus == "STALE" && before == detections, $"{core.CaptureStatus}/age={frame.Timing.AgeMs(clock)}");
        frame = NewFrame(); delay = 3001; Step();
        Check("bridge-over-3s-rejected", Hidden() && core.CaptureStatus == "STALE" && core.DetectionCompletedQpcMs - core.DetectionStartedQpcMs == 3001,
            $"{core.CaptureStatus}/detectMs={core.DetectionCompletedQpcMs-core.DetectionStartedQpcMs}");
        delay = 0; frame = NewFrame(); Step();
        core.SetGateValue(null, "manual-key:u"); loop.OnGateChanged("manual-key:u");
        Check("true-to-none-immediate-clear", Hidden() && !Display().Contains("D1:VISIBLE"), "clear=" + Hidden());
        core.SetGateValue(true, "manual-key:y"); frame = NewFrame(); Step();
        core.SetGateValue(false, "manual-key:n"); loop.OnGateChanged("manual-key:n");
        Check("true-to-false-immediate-clear", Hidden() && !Display().Contains("D1:VISIBLE"), "clear=" + Hidden());
        core.SetGateValue(true, "manual-key:y"); frame = NewFrame(); inDetect = () => identity = false; Step();
        Check("identity-lost-during-detect", Hidden() && core.CaptureStatus == "TARGET_LOST", core.CaptureStatus);
        inDetect = null; identity = true; frame = new(); Step();
        identity = false; Step(); identity = true; frame = new(); Step();
        Check("recovery-loss-resets-and-retries", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, loop.State.ToString());
        for (int i = 0; i < 4; i++) { frame = new(); Step(); }
        Check("recovery-no-frame-bounded-steps", Hidden() && loop.State == LiveLoopCore.LoopState.SuspendedRecovery, $"pulls={pulls}");
        clock += 1; frame = NewFrame(); Step();
        inDetect = () => core.SetGateValue(null, "inflight:u"); frame = NewFrame(); Step();
        Check("gate-changed-inflight-clears", Hidden() && core.CaptureStatus == "GATE_CHANGED", core.CaptureStatus);
        inDetect = null; identity = false; Step(); Step(); var terminal = Step();
        before = pulls; identity = true; Step();
        Check("termination-latched", terminal.LoopState == "Terminated" && loop.State == LiveLoopCore.LoopState.Terminated && pulls == before && Hidden(), loop.State.ToString());
        Check("qpc-boundary-3000", new FrameTiming(10000, 10000, DateTime.UtcNow).IsFresh(13000)
            && !new FrameTiming(10000, 10000, DateTime.UtcNow).IsFresh(13000.01), "3000 accepted; >3000 rejected");
        Check("invalid-future-source-rejected", !new FrameTiming(clock+1, clock, DateTime.UtcNow).IsFresh(clock), "future source invalid");

        // Actual wall-clock delayed JSON-line bridge, still offline and never starts WGC.
        string fixture = Path.Combine(tmp, "slow_bridge.py");
        File.WriteAllText(fixture, """
import sys, json, time
print('{"ready":true}', flush=True)
for line in sys.stdin:
    req = json.loads(line)
    time.sleep(3.2)
    print(json.dumps({"ok":True,"id":req["id"],"d1":{"state":"VISIBLE"},"d2":{"state":"NOT_VISIBLE"}}), flush=True)
""");
        using var slow = new PythonBridge("py", fixture);
        string error = slow.Start();
        Check("slow-bridge-fixture-start", error == null, error ?? "ready");
        if (error == null)
        {
            var realGate = new ContentGate(); realGate.Set(true, "test:true");
            var realCore = new MonitorCore(slow, realGate, tmp);
            var realLoop = new LiveLoopCore(); realLoop.Start();
            var actualFrame = NewFrame(); actualFrame.Timing = FrameTiming.OfflineNow();
            string logPath = Path.Combine(tmp, "r3_slow_bridge.jsonl");
            using (var writer = new StreamWriter(logPath))
                realLoop.Step(() => true, () => actualFrame, realCore, writer, 1, false);
            double elapsed = (realCore.DetectionCompletedQpcMs - realCore.DetectionStartedQpcMs) ?? -1;
            using var record = JsonDocument.Parse(File.ReadAllText(logPath));
            Check("actual-bridge-over-3s-no-publish", elapsed > 3000 && realCore.CaptureStatus == "STALE"
                && realCore.LastResult == null && realLoop.LastResult == null
                && !record.RootElement.GetProperty("resultAccepted").GetBoolean()
                && record.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                $"elapsedMs={elapsed:F1};status={realCore.CaptureStatus};resultAccepted=false");
        }

        // Preserve four-state detector observations outside recovery; readiness is separate.
        identity = true; loop = new LiveLoopCore(() => clock); loop.Start();
        core.SetGateValue(null, "test:none"); frame = NewFrame(); var unknown = Step();
        Check("normal-none-preserves-unknown", !unknown.Deterministic && core.CanPublish
            && Display().Contains("D1:UNKNOWN"), core.CaptureStatus);
        core.SetGateValue(false, "test:false"); frame = NewFrame(); var notReady = Step();
        Check("normal-false-preserves-not-evaluable", !notReady.Deterministic && core.CanPublish
            && Display().Contains("D1:NOT_EVALUABLE"), core.CaptureStatus);
        core.SetGateValue(true, "test:true"); frame = NewFrame(); Step();
        frame = NewFrame(); frame.Timing = null; Step();
        Check("missing-source-time-no-reuse", Hidden() && core.CaptureStatus == "NO_FRAME", core.CaptureStatus);

        // Accepted output is written only after the second identity check, never at bridge return.
        frame = NewFrame(); int checks = 0;
        string identityLog = Path.Combine(tmp, "r3_identity_log.jsonl");
        using (var writer = new StreamWriter(identityLog))
            loop.Step(() => ++checks == 1, () => frame, core, writer, ++id, false);
        using var identityRecord = JsonDocument.Parse(File.ReadAllText(identityLog));
        Check("identity-final-log-no-result-leak", Hidden()
            && !identityRecord.RootElement.GetProperty("resultAccepted").GetBoolean()
            && identityRecord.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
            File.ReadAllText(identityLog).Trim());
        core.LastTiming = new FrameTiming(clock - 2900, clock - 100, DateTime.UtcNow.AddMilliseconds(-100));
        string ageDisplay = Display();
        Check("display-source-age-not-receive-age", core.SourceAgeMs == 2900
            && ageDisplay.Contains("sourceAge:2.9s"), ageDisplay.Trim());
        string argvFixture = Path.Combine(tmp, "bridge_argv.py");
        File.WriteAllText(argvFixture, "import sys,json; print(json.dumps({'ready':True,'argv':sys.argv}),flush=True); sys.stdin.readline()");
        string profileWithSpaces = Path.Combine(tmp, "profile with spaces.json");
        using var argumentBridge = new PythonBridge("py", argvFixture, d1ProfilePath: profileWithSpaces);
        string argumentError = argumentBridge.Start();
        bool argumentsMatch = false;
        if (argumentError == null)
        {
            using var ready = JsonDocument.Parse(argumentBridge.ReadyInfo);
            var arguments = ready.RootElement.GetProperty("argv");
            argumentsMatch = arguments.GetArrayLength() == 3 && arguments[1].GetString() == "--d1-profile"
                && arguments[2].GetString() == profileWithSpaces;
        }
        Check("bridge-profile-argument-with-spaces", argumentsMatch, argumentError ?? argumentBridge.ReadyInfo);
    }
}
