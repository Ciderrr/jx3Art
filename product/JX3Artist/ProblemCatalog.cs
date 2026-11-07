namespace JX3Artist;
internal sealed record ProblemInfo(string Code,string Summary,string Advice);
internal static class ProblemCatalog
{
    internal static ProblemInfo Identify(string message)=>message switch
    {
        var s when s.StartsWith("目标不是游戏客户端")=>new("JX3-WINDOW-001","目标窗口未通过游戏身份检查","未发送新的游戏点击，请保留问题报告以排查窗口定位。"),
        var s when s.StartsWith("点击后未确认进入表演")=>new("JX3-START-001","未确认表演启动结果","请检查游戏当前状态，保留报告；不要反复点击。"),
        var s when s.StartsWith("Windows未允许切换到游戏")=>new("JX3-FOCUS-001","未能切换到游戏","请检查游戏窗口是否可用。"),
        var s when s.StartsWith("输入未完整提交")||s.Contains("人工松开鼠标")=>new("JX3-INPUT-001","输入释放或提交需要检查","请先人工确认鼠标已松开。"),
        var s when s.StartsWith("需要管理员权限")=>new("JX3-PERMISSION-001","启动权限不足","请正常双击程序并完成系统授权。"),
        var s when s.StartsWith("游戏窗口已失效")||s.StartsWith("未找到游戏")||s.StartsWith("需要且只支持一个")=>new("JX3-WINDOW-001","目标游戏窗口未确认","请检查是否只打开了一个游戏客户端。"),
        _=>new("JX3-UNKNOWN-001","辅助暂停，原因尚未分类","请导出问题报告，并说明大约时间和操作步骤。")
    };
}
