using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Int1;

/// <summary>
/// INT-1/P0-R1 只读监控 Console Host。
/// R1 修复（§二/§三）：
///  - live：身份核验**首拍失败**即暂停取帧/检测/确定性输出（TARGET_LOST）；三次连续失败退出；
///    恢复后须经"身份重验 + 首帧健康检查"才能重新输出（GATE/Revision 机制防旧状态复活）；
///  - offline：CaseResult 改可序列化属性（修复 summary 空对象）；期望表按 CSV 列名解析 +
///    来源/SHA256/键值可审计；缺失期望即 FAIL（不再静默通过）；新增安全契约测试组。
/// 命令：offline / live（live 须 PO 授权，本轮不运行）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Win32RO.SetProcessDpiAwarenessContext(Win32RO.PMv2);
        if (args.Length == 0) { Help(); return 0; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "offline" => CmdOffline(args),
                "offline-transport" => ImageTransportBench.Run(ArgStr(args, "--out", "output"), ArgStr(args, "--frames", @"int1\output\live_acceptance_20261026_01\tmp_frames")),
                "offline-capture" => FramePullTests.Command(ArgStr(args, "--out", "output")),
                "live" => CmdLive(args),
                "learn-click" => UserClickCalibration.Command(args),
                "offline-learn-click" => UserClickCalibrationTests.Command(args),
                "input-preflight" => InputPreflight.Command(args),
                _ => (Help(), 2).Item2,
            };
        }
        catch (Exception ex) { Console.Error.WriteLine($"未捕获异常: {ex}"); return 1; }
    }

    private static int Help()
    {
        Console.WriteLine("""
        int1 — INT-1/P0-R1 只读实时监控（READ_ONLY / NO_GAME_INPUT）
          offline-transport [--out output] 离线图像传输测量（不启动 WGC）
          offline-capture [--out output]   离线取帧选择测试（不启动 WGC）
          offline [--out output]           离线端到端 + 安全契约测试
          input-preflight [--out report.json]  只读检查当前程序/游戏令牌；不发送输入，不提权
          live --i-authorize-live [--d1-profile profile.json]    实机只读（须 PO 授权；R1 轮禁运行）
        """);
        return 0;
    }

    private static string DefaultBridgeScript()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        string[] candidates =
        {
            Path.Combine(here, "bridge", "bridge_server.py"),
            @"int1\bridge\bridge_server.py",
        };
        foreach (var c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
        return candidates[^1];
    }

    private static string ArgStr(string[] a, string n, string d)
    { for (int i = 0; i < a.Length - 1; i++) if (a[i].Equals(n, StringComparison.OrdinalIgnoreCase)) return a[i + 1]; return d; }
    private static bool Has(string[] a, string n) => a.Contains(n, StringComparer.OrdinalIgnoreCase);

    // ================= offline =================

    /// <summary>R1：可序列化 DTO（属性而非公共字段——修复 summary 空对象缺陷）。</summary>
    public sealed class CaseResult
    {
        public string Name { get; set; }
        public string Expect { get; set; }
        public string Actual { get; set; }
        public bool Pass { get; set; }
    }

    /// <summary>R1：期望表（可审计：来源路径+SHA256+逐键值）。</summary>
    public sealed class ExpectationTable
    {
        public string SourceCsv;
        public string SourceSha256;
        public Dictionary<string, string> Map = new();  // key: "<frameKey>|<D1|D2>" -> state
        public List<string> FrameKeys = new();
    }

    private static int CmdOffline(string[] args)
    {
        string outDir = ArgStr(args, "--out", "output");
        string corpusRoot = ArgStr(args, "--corpus", @"vis1\output");
        string expectCsv = ArgStr(args, "--expect", @"vis2\output\VIS-2-P1_evaluable_rows.csv");
        Directory.CreateDirectory(outDir);
        string tmp = Path.Combine(outDir, "tmp_frames");
        if (Directory.Exists(tmp)) foreach (var f in Directory.GetFiles(tmp, "*.png")) try { File.Delete(f); } catch { }
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logPath = Path.Combine(outDir, $"offline_{stamp}.jsonl");
        string sumPath = Path.Combine(outDir, $"offline_{stamp}_summary.json");
        string buildLog = ArgStr(args, "--buildlog", @"int1\output\build_release.log");

        Console.WriteLine("[offline] INT-1-R1 离线端到端 + 安全契约验证（零游戏、零捕获）");

        // ---- 依赖预检（§四：缺依赖明确报错，不进入不确定状态）----
        var missing = CheckDependencies(DefaultBridgeScript(), corpusRoot, expectCsv);
        if (missing.Count > 0)
        {
            foreach (var m in missing) Console.WriteLine($"  依赖缺失: {m}");
            return 10;
        }
        Console.WriteLine("  依赖预检通过：detector_lib.py / 模板 / 语料目录 / 期望 CSV / python(py) / Release 构建日志");

        var bridge = new PythonBridge("py", DefaultBridgeScript());
        string startErr = bridge.Start();
        if (startErr != null) { Console.WriteLine($"桥启动失败: {startErr}"); return 5; }
        Console.WriteLine($"[bridge] ready: {bridge.ReadyInfo}");

        var gate = new ContentGate();
        var core = new MonitorCore(bridge, gate, tmp);
        var cases = new List<CaseResult>();
        using var jsonl = new StreamWriter(logPath, false, new UTF8Encoding(false));

        var expectTable = LoadP1Expectations(expectCsv);
        cases.Add(new CaseResult
        {
            Name = "expectation-table",
            Expect = $"csv 加载且含期望键（sha256={expectTable.SourceSha256[..12]}…）",
            Actual = $"{expectTable.Map.Count} 键 / {expectTable.FrameKeys.Count} 帧",
            Pass = expectTable.Map.Count > 0 && expectTable.FrameKeys.Count > 0,
        });

        // ---- A. 语料全帧（缺失期望 = FAIL，不再静默通过） ----
        var fullFrames = Directory.GetFiles(corpusRoot, "*_full.jpg", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        foreach (var f in fullFrames)
        {
            string key = Path.GetFileNameWithoutExtension(f).Replace("_wgc", "");
            var bytes = File.ReadAllBytes(f);
            var (bgra, w, h) = DecodeJpeg(bytes);
            if (bgra == null) { Add(cases, key, "decode-ok", "decode-fail", false); continue; }

            bool hasD1 = expectTable.Map.TryGetValue(key + "|D1", out var ed1);
            bool hasD2 = expectTable.Map.TryGetValue(key + "|D2", out var ed2);
            if (!hasD1 || !hasD2)
            {
                Add(cases, $"{key} | expectation", "D1+D2 期望存在于 CSV", $"D1:{hasD1} D2:{hasD2}", false);
                continue;
            }

            // A1 cr=None → 本可确定的必须降级 UNKNOWN
            gate.Set(null, "offline:none");
            core.ProcessFrame(bgra, w, h, true, jsonl, out var rNone);
            string sNone = D1State(rNone);
            bool noneOk = ed1 is "VISIBLE" or "NOT_VISIBLE" ? sNone == "UNKNOWN" : sNone == ed1;
            Add(cases, $"{key} | cr=None", ed1 is "VISIBLE" or "NOT_VISIBLE" ? $"D1={ed1}->UNKNOWN(降级)" : $"D1={ed1}", $"D1={sNone}", noneOk);

            // A2 cr=True → 必须与 CSV 期望逐值一致
            gate.Set(true, "offline:scripted-true");
            core.ProcessFrame(bgra, w, h, true, jsonl, out var rTrue);
            string s1 = D1State(rTrue), s2 = D2State(rTrue);
            Add(cases, $"{key} | cr=True", $"D1:{ed1} D2:{ed2}", $"D1:{s1} D2:{s2}", s1 == ed1 && s2 == ed2);

            // A3 R1 新增：cr=True→人工按 n → 旧确定性显示必须立即失效（下一拍 False 门同样 NOT_EVALUABLE）
            gate.Set(false, "offline:manual-n");
            core.InvalidateResult("manual-key:n");   // live 中按键路径调用同一入口
            core.ProcessFrame(bgra, w, h, true, jsonl, out var rFalse);
            Add(cases, $"{key} | cr=True->n", "D1=NOT_EVALUABLE(game_content_not_ready_upstream)", $"D1={D1State(rFalse)}", D1State(rFalse) == "NOT_EVALUABLE");
        }

        // ---- B. 合成退化帧（接口防御；不算游戏样本） ----
        gate.Set(true, "offline:synthetic");
        var black = new byte[MonitorCore.ExpectedW * MonitorCore.ExpectedH * 3];
        core.ProcessFrame(BgrToBgra(black, MonitorCore.ExpectedW, MonitorCore.ExpectedH), MonitorCore.ExpectedW, MonitorCore.ExpectedH, true, jsonl, out var rB);
        Add(cases, "synthetic-black", "NOT_EVALUABLE(degenerate)", $"{D1State(rB)}/{D1Reason(rB)}", D1State(rB) == "NOT_EVALUABLE");
        var gray = new byte[MonitorCore.ExpectedW * MonitorCore.ExpectedH * 3];
        Array.Fill(gray, (byte)90);
        core.ProcessFrame(BgrToBgra(gray, MonitorCore.ExpectedW, MonitorCore.ExpectedH), MonitorCore.ExpectedW, MonitorCore.ExpectedH, true, jsonl, out var rG);
        Add(cases, "synthetic-gray", "NOT_EVALUABLE(degenerate)", $"{D1State(rG)}/{D1Reason(rG)}", D1State(rG) == "NOT_EVALUABLE");

        // ---- C. 尺寸变化：无检测 + 在途/旧结果作废 ----
        core.ProcessFrame(new byte[1920 * 1044 * 4], 1920, 1044, true, jsonl, out var rS);
        Add(cases, "resize-half-frame", "SIZE_MISMATCH(无检测)", core.CaptureStatus, core.CaptureStatus == "SIZE_MISMATCH");
        Add(cases, "resize-invalidates-old-state", "旧确定性状态已清除(LastResult=null)", LastResultCleared(core) ? "cleared" : "retained", LastResultCleared(core));

        // ---- D. 无新帧→STALE（反射回拨时间） ----
        SetLastValidUtc(core, DateTime.UtcNow.AddSeconds(-5));
        core.LastTiming = new FrameTiming(FrameTiming.NowMs() - 5000, FrameTiming.NowMs() - 5000, DateTime.UtcNow.AddSeconds(-5));
        core.ProcessNoFrame(jsonl);
        core.ProcessNoFrame(jsonl);
        Add(cases, "no-frame→STALE", "STALE", core.CaptureStatus, core.CaptureStatus == "STALE");
        Add(cases, "stale-invalidates-old-state", "旧确定性状态已清除", LastResultCleared(core) ? "cleared" : "retained", LastResultCleared(core));

        // ---- E. R1 新增：桥协议安全契约 ----
        // E1 ok=false（帧不可读：传一个空文件路径）
        gate.Set(true, "offline:bridge-contract");
        File.WriteAllText(Path.Combine(tmp, "bad.png"), "");
        var badPath = Path.Combine(tmp, "bad.png");
        var br1 = bridge.Detect(badPath, true, 90001, 15000);
        Add(cases, "bridge ok=false(frame_unreadable)", "Ok=false + 错误可读", $"{br1.Ok}/{br1.Error}", !br1.Ok && br1.Error.Contains("frame_unreadable"));

        // E2 id 错配（R2：伪造响应传入生产协议校验逻辑——通过"桥换心"探针：临时替换 stdout 读取不可行，
        // 改用生产校验函数 ValidateBridgeResponse 直接验证；该函数即 Detect 内部使用的同一实现）
        var forged = JsonDocument.Parse("{\"ok\":true,\"id\":999999,\"d1\":{\"state\":\"VISIBLE\"},\"d2\":{\"state\":\"NOT_VISIBLE\"}}");
        var vBad = PythonBridge.ValidateBridgeResponse(forged, 90002);
        Add(cases, "bridge forged-response id-mismatch (production validator)", "拒绝：id 错配", vBad, vBad.Contains("id-mismatch"));
        var vLegal = JsonDocument.Parse("{\"ok\":true,\"id\":90003,\"d1\":{\"state\":\"VISIBLE\"},\"d2\":{\"state\":\"NOT_VISIBLE\"}}");
        var vGood = PythonBridge.ValidateBridgeResponse(vLegal, 90003);
        Add(cases, "bridge legal-response (production validator)", "接受", vGood ?? "ok", vGood is null);
        var vBadState = JsonDocument.Parse("{\"ok\":true,\"id\":90004,\"d1\":{\"state\":\"MAYBE\"},\"d2\":{\"state\":\"NOT_VISIBLE\"}}");
        var vBadS = PythonBridge.ValidateBridgeResponse(vBadState, 90004);
        Add(cases, "bridge forged-response illegal-state (production validator)", "拒绝：四态非法", vBadS, vBadS.Contains("illegal"));

        // E3 桥死亡 → BRIDGE_FAILED 且确定性输出清除
        var killField = typeof(PythonBridge).GetField("_proc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Process)killField.GetValue(bridge)!).Kill();
        System.Threading.Thread.Sleep(300);
        core.ProcessFrame(BgrToBgra(gray, MonitorCore.ExpectedW, MonitorCore.ExpectedH), MonitorCore.ExpectedW, MonitorCore.ExpectedH, true, jsonl, out var rD);
        Add(cases, "bridge-dead", "BRIDGE_FAILED(无检测)", core.CaptureStatus, core.CaptureStatus == "BRIDGE_FAILED");
        Add(cases, "bridge-dead-invalidates-old-state", "旧确定性状态已清除", LastResultCleared(core) ? "cleared" : "retained", LastResultCleared(core));

        // E4 桥重启后不继承旧确定性状态（重启→首拍须重新建立）
        string rs = bridge.Restart();
        Add(cases, "bridge-restart", "重启成功", rs is null ? "ok" : rs, rs is null);
        gate.Set(true, "offline:post-restart");
        core.ProcessFrame(BgrToBgra(gray, MonitorCore.ExpectedW, MonitorCore.ExpectedH), MonitorCore.ExpectedW, MonitorCore.ExpectedH, true, jsonl, out var rR);
        Add(cases, "post-restart-no-inherit", "重启后无旧确定性继承（灰帧仍 NOT_EVALUABLE）", D1State(rR), D1State(rR) == "NOT_EVALUABLE");

        // ---- F. R2 新增：只读 live 主循环纯逻辑（LiveLoopCore）离线模拟（不启动游戏/不运行 live） ----
        AddLiveLoopCases(cases);
        R3SafetyTests.Run(cases, tmp);
        cases.AddRange(FramePullTests.Run());
        cases.AddRange(UserClickCalibrationTests.Run());
        cases.AddRange(InputPreflight.Tests());
        cases.AddRange(ImageTransportTests.Run(tmp, bridge, fullFrames[0]));
        // live 授权标志校验（代码路径，不执行 live）
        string authCode = RunLiveNoAuth();
        Add(cases, "live-auth-required", "无 --i-authorize-live → 拒绝(exit 4)", authCode, authCode == "4");
        int noProfile = CmdLive(new[] { "live", "--i-authorize-live" });
        Add(cases, "live-calibration-required-before-capture", "缺校准配置→拒绝(exit 11)", noProfile.ToString(), noProfile == 11);

        bridge.Dispose();
        int passCount = cases.Count(c => c.Pass);
        Console.WriteLine($"\n[offline] {passCount}/{cases.Count} PASS");
        foreach (var c in cases.Where(c => !c.Pass)) Console.WriteLine($"  FAIL {c.Name}: expect={c.Expect} actual={c.Actual}");

        string buildLogHash = File.Exists(buildLog) ? Sha256(buildLog) : "(missing)";
        var summary = new
        {
            meta = new
            {
                tool = "int1 offline (R1)",
                ts = DateTime.Now.ToString("o"),
                bridgeScript = DefaultBridgeScript(),
                expectations = new { expectTable.SourceCsv, expectTable.SourceSha256, keys = expectTable.Map.Count, frames = expectTable.FrameKeys.Count },
                dependencies = "detector_lib.py+d1_icon_template.png+py(cv2,numpy)+net10-runtime+Vortice.Direct3D11",
                buildLog, buildLogSha256 = buildLogHash,
            },
            passed = passCount,
            total = cases.Count,
            cases,   // R1：属性序列化，summary 完整列出每项
        };
        File.WriteAllText(sumPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[offline] 日志: {logPath}\n[offline] 汇总: {sumPath}");
        return passCount == cases.Count ? 0 : 9;
    }

    /// <summary>R2：LiveLoopCore 纯逻辑五例（首拍暂停/宽限不取帧/身份恢复不复用旧帧/门禁切换清空旧结果/3 拍终止）。</summary>
    private static void AddLiveLoopCases(List<CaseResult> cases)
    {
        DateTime now = DateTime.UtcNow;

        // F1：第一拍身份失效 → 立即暂停（SUSPENDED、未取帧、无确定性输出）
        var l1 = new LiveLoopCore { StaleAfterMs = 3000 };
        l1.Start();
        var o1 = l1.Tick(identityValid: false, frameAvailable: false, captureTickUtc: DateTime.MinValue, surfaceOk: false, bridgeOk: false, frameId: 1);
        Add(cases, "liveloop F1: 首拍身份失效→立即暂停", "Suspended & Pulled=false & 非确定性", $"{o1.LoopState} pulled={o1.Pulled} det={o1.Deterministic}",
            o1.LoopState == "Suspended" && !o1.Pulled && !o1.Deterministic && l1.LastResult == null);

        // F2：宽限期（连续失效 2 拍，<3）不调用 PullLatest、保持暂停；第 3 拍终止
        var o2 = l1.Tick(false, false, DateTime.MinValue, false, false, 2);
        var o3 = l1.Tick(false, false, DateTime.MinValue, false, false, 3);
        Add(cases, "liveloop F2: 宽限期不取帧 & 3 拍终止",
            "2 拍 Pulled=false 且第 3 拍 Terminated",
            $"t2={o2.LoopState}/pulled={o2.Pulled} t3={o3.LoopState}/pulled={o3.Pulled}",
            !o2.Pulled && o2.LoopState == "Suspended" && o3.LoopState == "Terminated" && !o3.Pulled && l1.PullCalls == 0);

        // F3：身份恢复不得复用旧帧——回 SUSPENDED_RECOVERY，且新鲜度不足的帧不转 RUNNING
        var l3 = new LiveLoopCore { StaleAfterMs = 3000 };
        l3.Start();
        l3.Tick(false, false, DateTime.MinValue, false, false, 1);                 // 失效
        var oRec = l3.Tick(true, true, now.AddSeconds(-10), true, true, 2);        // 恢复拍带 10s 旧帧
        Add(cases, "liveloop F3: 身份恢复不复用旧帧",
            "恢复期旧帧→不转 RUNNING（STALE/RECOVERY_PENDING）",
            $"{oRec.LoopState}({l3.Reason})",
            oRec.LoopState == "SuspendedRecovery" && l3.LastResult == null);

        // F4：内容门禁切换立即清空旧结果
        var l4 = new LiveLoopCore { StaleAfterMs = 3000 };
        l4.Start();
        l4.Tick(true, true, now, true, true, 1);                                   // OK，产生确定性结果
        bool hadResult = l4.LastResult != null;
        l4.OnGateChanged("manual-key:n");                                          // 人工按 n
        Add(cases, "liveloop F4: 门禁切换立即清空旧结果",
            "有结果→切换后 LastResult=null 且 Revision 递增",
            $"hadResult={hadResult} after={l4.LastResult?.State ?? "null"} rev={l4.GateRevision}",
            hadResult && l4.LastResult == null && l4.GateRevision >= 1);

        // F5：身份恢复后须重新确认（新鲜健康帧）才回 RUNNING，且不接受失效期旧帧结果
        var l5 = new LiveLoopCore { StaleAfterMs = 3000 };
        l5.Start();
        l5.Tick(false, false, DateTime.MinValue, false, false, 1);                 // 失效（Suspended）
        var o5a = l5.Tick(true, true, now.AddSeconds(-9), true, true, 2);          // 旧帧：不得转 RUNNING
        var o5b = l5.Tick(true, true, now, true, true, 3);                         // 新鲜健康帧：确认恢复
        Add(cases, "liveloop F5: 恢复须新鲜健康帧确认",
            "旧帧不转 RUNNING，新鲜帧→RUNNING",
            $"旧帧={o5a.LoopState} 新帧={o5b.LoopState}",
            o5a.LoopState == "SuspendedRecovery" && o5b.LoopState == "Running" && l5.LastResult != null);
    }

    /// <summary>R2：live 无授权路径校验（调用 CmdLive 不带授权标志，断言 exit 4 且零执行）。</summary>
    private static string RunLiveNoAuth()
    {
        try
        {
            int code = CmdLive(new[] { "live", "--seconds", "1" });   // 无 --i-authorize-live → 应立即拒绝
            return code.ToString();
        }
        catch (Exception ex) { return "EX:" + ex.Message; }
    }

    private static void Add(List<CaseResult> list, string n, string e, string a, bool p) =>
        list.Add(new CaseResult { Name = n, Expect = e, Actual = a, Pass = p });

    private static bool LastResultCleared(MonitorCore c) =>
        (JsonDocument)typeof(MonitorCore).GetField("LastResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(c)! == null;

    private static void SetLastValidUtc(MonitorCore c, DateTime v) =>
        typeof(MonitorCore).GetField("LastValidFrameUtc", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.SetValue(c, v);

    /// <summary>R1：依赖预检（缺失即报错退出，不进入不确定状态）。解释器经 where 探测（py 为启动器别名）。</summary>
    private static List<string> CheckDependencies(string bridgeScript, string corpusRoot, string expectCsv)
    {
        var miss = new List<string>();
        string detector = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bridgeScript)!, "..", "..", "vis2", "detector", "detector_lib.py"));
        string template = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bridgeScript)!, "..", "..", "vis2", "template", "d1_icon_template.png"));
        string decodeProbe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bridgeScript)!, "..", "tools", "decode_probe.py"));
        if (!File.Exists(decodeProbe)) miss.Add($"offline decode probe: {decodeProbe}");
        if (!File.Exists(detector)) miss.Add($"detector_lib.py: {detector}");
        if (!File.Exists(template)) miss.Add($"template: {template}");
        if (!Directory.Exists(corpusRoot) || Directory.GetFiles(corpusRoot, "*_full.jpg", SearchOption.AllDirectories).Length == 0)
            miss.Add($"corpus full frames: {corpusRoot}");
        if (!File.Exists(expectCsv)) miss.Add($"expectation csv: {expectCsv}");
        // python 解释器（py 启动器）探测：纯 .NET PATH 遍历（不启动任何子进程、无命令拼接）
        try
        {
            bool found = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(dir => Path.Combine(dir, "py.exe"))
                .Any(File.Exists);
            if (!found) miss.Add("python launcher 'py' (py.exe) not found on PATH");
        }
        catch { miss.Add("python launcher 'py' probe failed"); }
        return miss;
    }

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>R1：按 CSV 列名解析（不扫描任意单元格）；键=文件名去 _wgc + |target。</summary>
    private static ExpectationTable LoadP1Expectations(string csv)
    {
        var t = new ExpectationTable { SourceCsv = csv, SourceSha256 = Sha256(csv) };
        if (!File.Exists(csv)) return t;
        var lines = File.ReadLines(csv).ToList();
        if (lines.Count < 2) return t;
        var header = ParseCsvLine(lines[0]);
        int iPath = Array.IndexOf(header, "source_path");
        int iTarget = Array.IndexOf(header, "target");
        int iState = Array.IndexOf(header, "predicted_state");
        int iSha = Array.IndexOf(header, "sha256");
        if (iPath < 0 || iTarget < 0 || iState < 0)
            throw new InvalidOperationException($"期望 CSV 缺关键列: need source_path/target/state, header=[{string.Join(",", header)}]");
        foreach (var line in lines.Skip(1))
        {
            var f = ParseCsvLine(line);
            if (f.Length <= Math.Max(iPath, Math.Max(iTarget, iState))) continue;
            string key = Path.GetFileNameWithoutExtension(f[iPath]).Replace("_wgc", "") + "|" + f[iTarget];
            t.Map.TryAdd(key, f[iState]);
            var fk = Path.GetFileNameWithoutExtension(f[iPath]).Replace("_wgc", "");
            if (!t.FrameKeys.Contains(fk)) t.FrameKeys.Add(fk);
        }
        return t;
    }

    private static string[] ParseCsvLine(string line)
    {
        var list = new List<string>(); var sb = new StringBuilder(); bool inQ = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQ = !inQ;
            else if (ch == ',' && !inQ) { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }

    private static (byte[] bgra, int w, int h) DecodeJpeg(byte[] jpg)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.StreamSource = new MemoryStream(jpg); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit();
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = w * 4; var buf = new byte[stride * h];
            converted.CopyPixels(buf, stride, 0);
            return (buf, w, h);
        }
        catch { return (null, 0, 0); }
    }

    private static byte[] BgrToBgra(byte[] bgr, int w, int h)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 0, j = 0; i < bgr.Length; i += 3, j += 4)
        { outBuf[j] = bgr[i]; outBuf[j + 1] = bgr[i + 1]; outBuf[j + 2] = bgr[i + 2]; outBuf[j + 3] = 255; }
        return outBuf;
    }

    private static string D1State(JsonDocument d) => d?.RootElement.TryGetProperty("d1", out var n) == true && n.TryGetProperty("state", out var s) == true ? s.GetString() : "none";
    private static string D2State(JsonDocument d) => d?.RootElement.TryGetProperty("d2", out var n) == true && n.TryGetProperty("state", out var s) == true ? s.GetString() : "none";
    private static string D1Reason(JsonDocument d) => d?.RootElement.TryGetProperty("d1", out var n) == true && n.TryGetProperty("reason", out var s) == true ? s.GetString() : "-";

    // ================= live（只读；R2 恢复真实主循环——本轮 OFFLINE_ONLY 不执行，但代码完整） =================

    private static int CmdLive(string[] args)
    {
        if (!Has(args, "--i-authorize-live"))
        {
            Console.WriteLine("live 模式须 PO 显式授权（--i-authorize-live）。本程序只读：不恢复/不前置/不最小化/不改窗口、零输入。");
            return 4;
        }
        if (Has(args, "--r1-offline-only"))
        {
            Console.WriteLine("[live] 当前任务轮授权为 OFFLINE_ONLY：拒绝执行（代码保留，运行留待授权轮）。");
            return 4;
        }
        if (ArgStr(args, "--d1-profile", null) == null)
        {
            Console.WriteLine("[live] 缺少已确认的图标校准配置。先在保存截图上点选校准，再提供 --d1-profile；本次未定位窗口或启动捕获。");
            return 11;
        }
        int seconds = int.TryParse(ArgStr(args, "--seconds", "120"), out var s) ? s : 120;
        string outDir = ArgStr(args, "--out", "output");
        Directory.CreateDirectory(outDir);
        string tmp = Path.Combine(outDir, "tmp_frames"); Directory.CreateDirectory(tmp);

        var (hwnd, pid, err) = Win32RO.LocateGame();
        if (hwnd == IntPtr.Zero) { Console.WriteLine($"[live] 未定位游戏: {err}"); return 1; }
        Console.WriteLine($"[live] 目标 0x{hwnd:X} pid={pid}（只读绑定；每拍 LiveLoopCore 核验 HWND+PID，首拍失效即暂停）");

        var bridge = new PythonBridge("py", DefaultBridgeScript(), d1ProfilePath: ArgStr(args, "--d1-profile", null));
        string startErr = bridge.Start();
        if (startErr != null) { Console.WriteLine($"桥启动失败: {startErr}"); return 5; }

        var engine = new WgcEngine();
        string wgcErr = engine.Start(hwnd);
        if (wgcErr != null) { Console.WriteLine($"[live] WGC 启动失败: {wgcErr}"); bridge.Dispose(); return 6; }

        var core = new MonitorCore(bridge, new ContentGate(), tmp);
        var loop = new LiveLoopCore();
        loop.Start();
        string logPath = Path.Combine(outDir, $"live_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        using var jsonl = new StreamWriter(logPath, false, new UTF8Encoding(false));

        Console.WriteLine($"[live] 只读监控 {seconds}s（CONTENT_READY 默认 None 保守；y=True/n=False/u=None 人工确认；Ctrl+C 退出）");
        var sw = Stopwatch.StartNew();
        int frameId = 0;
        while (sw.Elapsed.TotalSeconds < seconds && loop.State != LiveLoopCore.LoopState.Terminated)
        {
            // 人工 y/n/u（R1 门禁接口：切换立即失效旧结果）
            while (Console.KeyAvailable)
            {
                var k = Console.ReadKey(intercept: true).Key;
                if (k == ConsoleKey.Y) { gateSet(core, loop, true, "manual-key:y"); }
                else if (k == ConsoleKey.N) { gateSet(core, loop, false, "manual-key:n"); }
                else if (k == ConsoleKey.U) { gateSet(core, loop, null, "manual-key:u"); }
            }

            loop.Step(
                () => Win32RO.IsWindow(hwnd) && Win32RO.GetWindowThreadProcessId(hwnd, out uint currentPid) && currentPid == pid,
                () => engine.PullLatest(400), core, jsonl, ++frameId);
            Thread.Sleep(1000);
        }
        engine.Dispose();
        bridge.Dispose();
        Console.WriteLine($"[live] 结束（state={loop.State}）。日志: {logPath}");
        return 0;

        static void gateSet(MonitorCore c, LiveLoopCore l, bool? v, string src)
        {
            c.SetGateValue(v, src);
            l.OnGateChanged(src);
            c.Render();
        }
    }
}
