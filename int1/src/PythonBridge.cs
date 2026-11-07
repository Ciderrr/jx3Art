using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Int1;

/// <summary>
/// 与 bridge_server.py 的长驻桥（R1 加固版）。
/// 协议安全契约：
///  - 任何 ok=false / 缺失 ok / 非法结构 / id 错配 → 一律失败（绝不视为成功）；
///  - 成功响应必须含 d1/d2 且二者 state ∈ 四态集合，否则失败；
///  - 启动等待用限时任务（绝不使用可永久阻塞的同步 ReadLine）；
///  - 请求超时后桥即"失去同步"（可能迟到吐出旧响应）→ 桥被标记为不可用，
///    由宿主决定重启（CreateRestart）；严禁把迟到行当作下一帧结果；
///  - 桥失败时 MonitorCore 清除确定性输出（见 MonitorCore）。
/// </summary>
internal sealed class PythonBridge : IDisposable
{
    private Process _proc;
    private readonly string _exe, _script, _d1Profile;
    public bool Ready;
    public string ReadyInfo;
    public string LastError;
    public bool Desynced { get; private set; }   // 超时后置位：存在一条可能迟到的响应，桥不可再信任

    public PythonBridge(string pythonExe, string scriptPath, int startupTimeoutMs = 20000, string d1ProfilePath = null)
        => (_exe, _script, _d1Profile) = (pythonExe, scriptPath, d1ProfilePath);

    /// <summary>启动（或超时/失联后重启）桥进程并等待 ready 行。</summary>
    public string Start(int startupTimeoutMs = 20000)
    {
        KillInternal();
        Desynced = false;
        if (!File.Exists(_script)) return $"bridge-script-missing:{_script}";
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            },
        };
        _proc.StartInfo.ArgumentList.Add(_script);
        if (_d1Profile != null)
        {
            _proc.StartInfo.ArgumentList.Add("--d1-profile");
            _proc.StartInfo.ArgumentList.Add(_d1Profile);
        }
        _proc.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is { }) Console.Error.WriteLine("[py-err] " + e.Data); };
        try { _proc.Start(); _proc.BeginErrorReadLine(); }
        catch (Exception ex) { return $"bridge-start-failed:{ex.Message}"; }

        // 限时等待 ready（异步，绝不无限阻塞）
        var t = _proc.StandardOutput.ReadLineAsync();
        if (!t.Wait(startupTimeoutMs))
        {
            KillInternal();
            return $"bridge-startup-timeout({startupTimeoutMs}ms)";
        }
        string line = t.Result;
        if (line is null) { KillInternal(); return "bridge-died-before-ready"; }
        if (line.Contains("\"ready\"")) { Ready = true; ReadyInfo = line; return null; }
        KillInternal();
        return $"bridge-unexpected-ready-line:{Truncate(line)}";
    }

    public bool Alive => _proc is { HasExited: false } && !Desynced;

    public sealed class BridgeResult
    {
        public bool Ok;                 // 仅当全部校验通过才为 true
        public string Error;
        public JsonDocument Document;   // Ok=true 时有效（含 d1/d2）
        public double RoundTripMs;
    }

    private static readonly string[] LegalStates = { "VISIBLE", "NOT_VISIBLE", "UNKNOWN", "NOT_EVALUABLE" };

    /// <summary>
    /// R2：生产协议校验（Detect 内部亦使用此实现——被测代码即交付代码）。
    /// 返回 null=通过；否则返回分类错误描述（id 错配/缺 d1-d2/state 非法/结构缺失）。
    /// </summary>
    internal static string ValidateBridgeResponse(JsonDocument doc, int expectedFrameId)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
            return root.TryGetProperty("error", out var e2) && e2.ValueKind == JsonValueKind.String
                ? $"bridge-refused:{e2.GetString()}"
                : "bridge-ok-false-or-missing";
        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != expectedFrameId)
            return $"bridge-id-mismatch(expect {expectedFrameId})";
        foreach (var k in new[] { "d1", "d2" })
        {
            if (!root.TryGetProperty(k, out var n) || !n.TryGetProperty("state", out var st) || st.ValueKind != JsonValueKind.String)
                return $"bridge-missing-{k}-state";
            if (!LegalStates.Contains(st.GetString()))
                return $"bridge-illegal-{k}-state:{st.GetString()}";
        }
        return null;
    }

    /// <summary>发起一次检测并完整校验响应结构。</summary>
    public BridgeResult Detect(string framePath, bool? contentReady, int frameId, int timeoutMs)
    {
        var r = new BridgeResult();
        var sw = Stopwatch.StartNew();
        if (!Alive)
        {
            r.Error = Desynced ? "bridge-desynced(previous response may arrive late; restart required)" : "bridge-dead";
            return r;
        }
        try
        {
            var req = new { id = frameId, @frame = framePath, content_ready = contentReady switch { true => true, false => false, _ => (bool?)null } };
            _proc.StandardInput.WriteLine(JsonSerializer.Serialize(req));
            _proc.StandardInput.Flush();

            var t = _proc.StandardOutput.ReadLineAsync();
            if (!t.Wait(timeoutMs))
            {
                // 超时 = 失去同步：迟到响应可能随后到达，绝不能再读
                Desynced = true;
                r.Error = $"bridge-timeout({timeoutMs}ms)->desynced";
                return r;
            }
            string line = t.Result;
            if (line is null) { r.Error = "bridge-eof(process exited)"; return r; }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException je) { r.Error = $"bridge-malformed-json:{Truncate(line)}({je.Message})"; return r; }

            r.Error = ValidateBridgeResponse(doc, frameId);   // R2：生产校验函数（测试与实现同源）
            if (r.Error != null) return r;
            r.Ok = true;
            r.Document = doc;
            r.RoundTripMs = sw.Elapsed.TotalMilliseconds;
            return r;
        }
        catch (Exception ex)
        {
            r.Error = $"bridge-exception:{ex.Message}";
            return r;
        }
    }

    /// <summary>失联/失同步后由宿主显式重启。</summary>
    public string Restart(int startupTimeoutMs = 20000) => Start(startupTimeoutMs);

    private static string Truncate(string s) => s is null ? "(null)" : (s.Length > 160 ? s[..160] + "…" : s);

    private void KillInternal()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        Ready = false;
    }

    public void Dispose() => KillInternal();
}
