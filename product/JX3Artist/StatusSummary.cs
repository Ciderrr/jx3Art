using Int1;
namespace JX3Artist;
internal static class StatusSummary
{
    internal static string VisibilityHint(ViewState view)
    {
        if(view.Message.StartsWith("已找到入口，但入口周围控件尚未确认",StringComparison.Ordinal))return "请露出完整的开始表演图标，移开遮挡面板。\n确认前不会据此点击，助手会继续检查。";
        if(view.Message.Contains("证据冲突",StringComparison.Ordinal))return view.Message+"；确认前不会点击。";
        if(view.Message.Contains("坐标已变化",StringComparison.Ordinal)||view.Message.Contains("画面过期",StringComparison.Ordinal))return view.Message;
        return "未能确认艺人界面。请检查是否已开启艺人身份，并移开遮挡「开始表演」图标或表演界面的面板。\n确认前不会据此点击，助手会继续检查。";
    }
    internal static string? CountdownFallback(ViewState view,bool automatic,bool active=true)
    {
        if(!active||view.Phase!=Phase.Performing||view.CountdownEnd.HasValue)return null;
        return "倒计时暂未识别，不影响挂机流程。\n"+(automatic?"仍会定时检查表演是否结束，并按计划自动续演。":"仍会定时检查表演是否结束，续演时提醒您继续。");
    }
    internal static string Format(ViewState view,double now,DateTime wallNow)
    {
        string updated=view.FrameTime.HasValue?$"{Math.Max(0,Math.Floor((wallNow-view.FrameTime.Value).TotalSeconds)):0}秒前":"尚未更新";
        string state=view.AwaitingApproval?"待续演，等待确认":view.Phase switch{Phase.Performing=>"表演中",Phase.ConfirmingEntry=>"确认入口",Phase.WaitingIdle=>"准备续演",Phase.Starting=>"正在开始表演",Phase.WaitingVisibility=>"暂未确认",Phase.Paused=>"已暂停",Phase.Completed=>"计划已完成",Phase.Stopped=>"已停止",_=>"正在确认"};
        string next=view.Phase is Phase.Stopped or Phase.Completed or Phase.Paused?"已停止检查":view.NextCheck.HasValue&&view.NextCheck>now?$"下次画面检查于{Math.Ceiling((view.NextCheck.Value-now)/1000):0}秒后":"正在检查画面";
        return $"上次表演状态更新：{updated}，状态：{state}；{next}";
    }
}
