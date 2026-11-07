namespace JX3Artist;
internal sealed record AssistanceNotice(string Kind,string Title,string Text,bool Warning=false);
// Record transitions at publication time: UI polling must not miss a brief pause.
internal sealed class LifecycleNotices
{
    readonly object gate=new();readonly Queue<AssistanceNotice> pending=new();
    Phase previous=Phase.Stopped;bool waiting,closed;
    internal void Close(){lock(gate){closed=true;pending.Clear();}}
    internal void Begin(){lock(gate){if(closed)return;previous=Phase.Watching;waiting=false;pending.Enqueue(new("started","辅助已开始","正在确认当前表演状态。"));}}
    internal void Observe(ViewState view,bool automatic)
    {
        lock(gate){
            if(closed)return;
            if(view.Phase!=previous){
                if(view.Phase==Phase.Paused)pending.Enqueue(new("paused","辅助已暂停",PauseReason(view.Message),true));
                else if(view.Phase==Phase.Completed)pending.Enqueue(new("completed","辅助已结束","本次计划已完成。"));
                else if(view.Phase==Phase.Stopped)pending.Enqueue(new("stopped","辅助已结束","已停止后续辅助。"));
            }
            bool needs=view.AwaitingApproval&&!automatic&&view.Phase is not (Phase.Paused or Phase.Stopped or Phase.Completed);
            if(needs&&!waiting)pending.Enqueue(new("approval","表演已结束，等待接管","请打开艺人助手，决定是否继续表演。"));
            waiting=needs;previous=view.Phase;
        }
    }
    static string PauseReason(string reason)
        =>PauseBody(reason)+"\n问题代码："+ProblemCatalog.Identify(reason).Code;
    static string PauseBody(string reason)
    {
        if(reason.StartsWith("点击后未确认进入表演"))return "点击后暂未确认进入表演，已暂停后续辅助。请打开助手检查当前游戏状态。";
        string text=string.IsNullOrWhiteSpace(reason)?"请打开助手查看原因。":"原因："+reason.Trim();
        return text.Length>200?text[..200]+"…":text;
    }
    internal bool TryTake(out AssistanceNotice? notice){lock(gate){if(pending.Count==0){notice=null;return false;}notice=pending.Dequeue();return true;}}
    internal static void AddTests(Action<string,Action> test)
    {
        test("lifecycle notifications retain fast Start Pause and End instead of UI sampling",()=>{
            var notices=new LifecycleNotices();notices.Observe(new(Phase.Stopped,"等待开始"),false);if(notices.TryTake(out _))throw new Exception("idle startup notified");
            notices.Begin();notices.Observe(new(Phase.Paused,"点击后未确认进入表演，测试原因"),false);notices.Observe(new(Phase.Paused,"same pause"),false);notices.Observe(new(Phase.Stopped,"已停止"),false);
            var kinds=new List<string>();while(notices.TryTake(out var notice)){kinds.Add(notice!.Kind);if(notice.Kind=="paused"&&(!notice.Warning||!notice.Text.Contains("暂未确认")))throw new Exception("pause lost actionable reason");}
            if(string.Join(',',kinds)!="started,paused,stopped")throw new Exception("lifecycle transition lost or duplicated");
        });
        test("approval notice follows manual mode only and completed is distinct from stopped",()=>{
            var notices=new LifecycleNotices();var pending=new ViewState(Phase.WaitingIdle,"等待",AwaitingApproval:true);
            notices.Observe(pending,true);if(notices.TryTake(out _))throw new Exception("automatic mode asked approval");
            notices.Observe(pending,false);notices.Observe(pending,false);if(!notices.TryTake(out var one)||one!.Kind!="approval"||notices.TryTake(out _))throw new Exception("approval missing or spammed");
            notices.Observe(pending with{AwaitingApproval=false},false);notices.Observe(pending,false);if(!notices.TryTake(out one)||one!.Kind!="approval")throw new Exception("new approval lost");
            notices.Observe(new(Phase.Completed,"完成"),false);notices.Observe(new(Phase.Completed,"完成"),false);if(!notices.TryTake(out one)||one!.Kind!="completed"||notices.TryTake(out _))throw new Exception("completion duplicated");
            notices.Begin();if(!notices.TryTake(out one)||one!.Kind!="started")throw new Exception("restart notice missing");
        });
        test("closing clears all notices and refuses late lifecycle events or restarts",()=>{
            var notices=new LifecycleNotices();notices.Begin();notices.Observe(new(Phase.Paused,"目标不是游戏客户端"),false);notices.Close();notices.Close();notices.Begin();notices.Observe(new(Phase.Stopped,"结束"),false);notices.Observe(new(Phase.WaitingIdle,"等待",AwaitingApproval:true),false);if(notices.TryTake(out _))throw new Exception("closed notification queue emitted stale status");
        });
    }
}
