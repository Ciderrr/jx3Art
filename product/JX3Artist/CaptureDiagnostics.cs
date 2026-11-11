using System.IO;
using Jx3.OneClickTest;
namespace JX3Artist;

internal sealed record CaptureEvidence(string Stage,bool Identity,bool Minimized,Box? Before=null,Box? After=null,bool? FrameOk=null,bool? Recreated=null,
    int? SurfaceW=null,int? SurfaceH=null,int? ContentW=null,int? ContentH=null,string? EngineError=null,string? BoundsError=null,double? Source=null,WindowSnapshot? BeforeGeometry=null,WindowSnapshot? AfterGeometry=null,bool WindowFrame=false);
internal static class CaptureDiagnostics
{
    internal static string[] Reasons(CaptureEvidence e)
    {
        var reasons=new List<string>();
        if(!e.Identity)reasons.Add("TARGET_IDENTITY_LOST");
        if(e.Minimized)reasons.Add("WINDOW_MINIMIZED");
        if(e.Stage=="before_pull")return reasons.ToArray();
        if(e.FrameOk!=true)reasons.Add("FRAME_UNAVAILABLE");
        if(e.Recreated==true)reasons.Add("FRAME_POOL_RECREATED");
        if(e.After==null)reasons.Add("WINDOW_BOUNDS_UNAVAILABLE");
        else if(e.Before!=e.After)reasons.Add("WINDOW_BOUNDS_CHANGED");
        bool MatchesSize(int? w,int? h)=>e.WindowFrame?e.Before!=null&&w==e.Before.Width&&h==e.Before.Height&&WindowFramePolicy.SizeSupported(w.Value,h.Value):w==3840&&h==2088;
        if(e.Before==null || !MatchesSize(e.Before.Width,e.Before.Height))reasons.Add("WINDOW_SIZE_UNSUPPORTED");
        // A failed pull's default zeros are not measured unsupported dimensions.
        if(e.FrameOk==true)
        {
            if(!MatchesSize(e.SurfaceW,e.SurfaceH))reasons.Add("SURFACE_SIZE_UNSUPPORTED");
            if(!MatchesSize(e.ContentW,e.ContentH))reasons.Add("CONTENT_SIZE_UNSUPPORTED");
        }
        return reasons.ToArray();
    }
    internal static void Ensure(CaptureEvidence evidence)
    {var reasons=Reasons(evidence);if(reasons.Length>0)throw new CaptureFailureException(evidence,reasons);}
    internal static bool WaitForRestore(bool wujie,bool identity,Exception error)=>
        wujie&&identity&&error is CaptureFailureException {Evidence.Minimized:true};
    internal static string Describe(string code)=>code switch
    {
        "PIXEL_MAP_NOT_VERIFIED"=>"窗口与采集画面坐标未确认，请等待新画面",
        "TARGET_IDENTITY_LOST"=>"游戏窗口身份已失效",
        "WINDOW_MINIMIZED"=>WujieTarget.CaptureWindowMinimized,
        "FRAME_UNAVAILABLE"=>"采集未取得有效画面",
        "FRAME_POOL_RECREATED"=>"采集缓冲区刚重建，等待稳定画面",
        "WINDOW_BOUNDS_UNAVAILABLE"=>"无法读取游戏窗口范围",
        "WINDOW_BOUNDS_CHANGED"=>"取帧期间窗口位置或大小发生变化",
        "WINDOW_SIZE_UNSUPPORTED"=>"窗口尺寸不受支持或采集范围不一致",
        "SURFACE_SIZE_UNSUPPORTED"=>"采集表面尺寸不受支持",
        "CONTENT_SIZE_UNSUPPORTED"=>"游戏内容尺寸不受支持",
        _=>code
    };
}
internal sealed class CaptureFailureException(CaptureEvidence evidence,string[] reasons):IOException(string.Join("；",reasons.Select(CaptureDiagnostics.Describe)))
{
    public CaptureEvidence Evidence{get;}=evidence;
    public string[] Reasons{get;}=reasons;
}
