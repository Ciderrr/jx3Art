using Int1;
namespace JX3Artist;
internal static class StatusSummary
{
    internal static string MinimizedHint(ViewState view,double now)=>
        view.MinimizedDeadline is double deadline
            ?deadline>now?"游戏已最小化，当前为估算时间。":"预计到时，请恢复游戏窗口。"
            :"游戏已最小化，请恢复窗口。";
    internal static string ForegroundHint(bool rechecking)=>rechecking
        ?"已切回游戏。助手正在重新确认画面。"
        :"Windows 暂未允许切回游戏。本轮计划已保留，请手动切回游戏继续。";
    internal static string VisibilityHint(ViewState view,double now)
    {
        if(view.Message.StartsWith(WujieTarget.CaptureWindowMinimized,StringComparison.Ordinal))
            return WujieTarget.CaptureWindowMinimized+"。";
        string hint;
        if(view.Message.StartsWith("已找到入口，但入口周围控件尚未确认",StringComparison.Ordinal))hint="请露出完整的开始表演图标，移开遮挡面板。";
        else if(view.Message.Contains("证据冲突",StringComparison.Ordinal)||view.Message.Contains("坐标已变化",StringComparison.Ordinal)||
            view.Message.Contains("画面过期",StringComparison.Ordinal))
            hint=view.Message.Split('；',2)[0].TrimEnd('。')+"。";
        else hint="请检查是否已开启艺人身份，并移开遮挡。";
        string next=SecondsUntil(view.NextCheck,now) is int seconds?$"助手会在 {seconds} 秒之后再次自动检查。":"助手正在自动检查。";
        return hint+"\n"+next;
    }
    static int? SecondsUntil(double? next,double now)=>next is double at&&double.IsFinite(at)&&double.IsFinite(now)&&at>now
        ?(int)Math.Min(int.MaxValue,Math.Ceiling((at-now)/1000)):null;
    internal static string? CountdownFallback(ViewState view,bool automatic,bool active=true)
    {
        if(!active||view.Phase!=Phase.Performing)return null;
        if(view.CountdownEnd.HasValue)return view.CountdownEstimated?"根据上次读数继续计时，正在定时校准。":null;
        return "倒计时暂未识别，不影响挂机流程。\n"+(automatic?"仍会定时检查表演是否结束，并按计划自动续演。":"仍会定时检查表演是否结束，续演时提醒您继续。");
    }
    internal static string Format(ViewState view,double now,DateTime wallNow)
    {
        string updated=view.FrameTime.HasValue?$"{Math.Max(0,Math.Floor((wallNow-view.FrameTime.Value).TotalSeconds)):0}秒前":"尚未更新";
        string state=view.AwaitingApproval?"待续演，等待确认":view.Phase switch{Phase.Performing=>"表演中",Phase.ConfirmingEntry=>"确认入口",Phase.WaitingIdle=>"准备续演",Phase.Starting=>"正在开始表演",Phase.WaitingVisibility=>"暂未确认",Phase.Paused=>"已暂停",Phase.Completed=>"计划已完成",Phase.Stopped=>"已停止",_=>"正在确认"};
        string next=view.Phase is Phase.Stopped or Phase.Completed or Phase.Paused?"已停止检查":
            SecondsUntil(view.NextCheck,now) is int seconds?$"下次画面检查于{seconds}秒后":"正在检查画面";
        return $"上次表演状态更新：{updated}，状态：{state}；{next}";
    }
}
