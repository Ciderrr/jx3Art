namespace Int1;

/// <summary>
/// GAME_CONTENT_READY 的独立上游来源（保守策略；R1 加固）：
///  - 默认 None（unknown）：确定性判定被降级 UNKNOWN（evidence held）；
///  - 置 True/False 仅经人工显式输入（y/n/u）；
///  - R1：门禁变化带版本号（Revision）。任何"帧源受损事件"（尺寸变化/无新帧/桥故障/身份失效）
///    都会使 Revision 递增——检测在途期间门禁或帧源发生变化时，返回结果因 Revision 不匹配被废弃，
///    旧确定性状态绝不复活；按 n/u 时 MonitorCore 立即失效旧确定性显示。
/// </summary>
internal sealed class ContentGate
{
    private volatile int _state; // 0=None,1=True,2=False
    private long _revision;
    public string Source = "default-conservative(None)";

    public bool? Value => _state switch { 1 => true, 2 => false, _ => null };
    public long Revision => _revision;

    public void Set(bool? v, string source)
    {
        _state = v switch { true => 1, false => 2, _ => 0 };
        Source = source;
        System.Threading.Interlocked.Increment(ref _revision);
    }

    /// <summary>帧源受损事件：使在途检测结果失效（Revision 递增）。</summary>
    public long InvalidateForFrameSourceEvent(string reason)
    {
        LastInvalidation = reason;
        return System.Threading.Interlocked.Increment(ref _revision);
    }
    public string LastInvalidation;
}
