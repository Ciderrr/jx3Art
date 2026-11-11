namespace JX3Artist;
internal sealed record ProblemInfo(string Code,string Summary,string Advice);
internal static class ProblemCatalog
{
    internal static string UserMessage(string message,string? fallback=null)
    {
        string text=message.TrimStart();
        bool localized=(text.Length>0&&text[0] is >= '\u3400' and <= '\u9fff')||
            text.StartsWith("Windows未允许切换到游戏",StringComparison.Ordinal);
        return localized?text:fallback??Identify(message).Summary+"。";
    }
    internal static string PauseMessage(string message,string? startKey=null)
    {
        var problem=Identify(message);
        string detail=problem.Code=="JX3-FOCUS-001"
            ? "\n"+(startKey==null?problem.Advice:$"请先手动切回游戏，再按 {startKey} 重新开始辅助。")
            : problem.Code.StartsWith("JX3-WUJIE-",StringComparison.Ordinal)?"\n"+problem.Advice:"";
        return UserMessage(message)+detail+"\n问题代码："+problem.Code;
    }

    internal static ProblemInfo Identify(string message)=>message switch
    {
        WujieTarget.MissingProcess=>new("JX3-WUJIE-001","未找到正在运行的极速端客户端","请打开极速端并进入游戏后再开始。"),
        WujieTarget.MultipleProcesses=>new("JX3-WUJIE-002","极速端客户端数量超过单开限制","请只保留一个极速端客户端。"),
        WujieTarget.WindowUnavailable or "Wujie window identity or visibility changed"=>new("JX3-WUJIE-003","极速端游戏窗口未确认","请恢复游戏窗口并重新开始辅助。"),
        WujieTarget.MultipleWindows or "Ambiguous Wujie top-level windows"=>new("JX3-WUJIE-004","极速端有多个可见窗口","请关闭游戏弹出的多余窗口后重试。"),
        ObservationEvidence.InvalidEntrySize or ObservationEvidence.InvalidRecognitionBounds or "Invalid Wujie entry dimensions" or "Recognizer box outside verified bounds"
            =>new("JX3-WUJIE-005","极速端识别坐标与已验证的窗口范围不符","请恢复试用时的游戏分辨率与缩放。"),
        var s when s.StartsWith("极速端本地试用识别配置缺失",StringComparison.Ordinal)=>new("JX3-WUJIE-006","极速端试用识别配置缺失","请从完整试用包启动助手。"),
        "Exactly one Wujie process is required" or "Exactly one bound Wujie process is required"
            =>new("JX3-WINDOW-001","旧日志未确认极速端客户端数量","请确认已打开且只保留一个极速端客户端。"),
        var s when s.StartsWith("目标不是游戏客户端")=>new("JX3-WINDOW-001","目标窗口未通过游戏身份检查","未发送新的游戏点击，请保留问题报告以排查窗口定位。"),
        var s when s.StartsWith("点击后未确认进入表演")=>new("JX3-START-001","未确认表演启动结果","请检查游戏当前状态，保留报告；不要反复点击。"),
        var s when s.StartsWith("Windows未允许切换到游戏")=>new("JX3-FOCUS-001","未能切换到游戏","请先手动切回游戏，再用开始快捷键或助手的「开始辅助」重新开始。"),
        var s when s.StartsWith("输入未完整提交")||s.Contains("人工松开鼠标")=>new("JX3-INPUT-001","输入释放或提交需要检查","请先人工确认鼠标已松开。"),
        var s when s.StartsWith("需要管理员权限")=>new("JX3-PERMISSION-001","启动权限不足","请正常双击程序并完成系统授权。"),
        var s when s.StartsWith("游戏窗口身份已失效")=>new("JX3-WINDOW-001","目标游戏窗口身份失效","请检查客户端和目标窗口，必要时重新开始辅助。"),
        var s when s.Contains(WujieTarget.CaptureWindowMinimized,StringComparison.Ordinal)=>new("JX3-WINDOW-002","目标游戏窗口已最小化","请恢复游戏窗口。可切到其他软件前台，但请保持游戏窗口打开。"),
        var s when s.StartsWith("游戏窗口已失效")||s.StartsWith("未找到游戏")||s.StartsWith("需要且只支持一个")=>new("JX3-WINDOW-001","目标游戏窗口未确认","请检查是否只打开了一个游戏客户端。"),
        _=>new("JX3-UNKNOWN-001","辅助暂停，原因尚未分类","请导出问题报告，并说明大约时间和操作步骤。")
    };
}
