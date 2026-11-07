namespace JX3Artist;

internal enum Phase { Watching, ConfirmingEntry, WaitingVisibility, WaitingIdle, Starting, Performing, Paused, Stopped, Completed }
internal record Observation(double Source,double Now,bool Identity,bool Geometry,string Location,int Count,string D2,string? ImagePath=null,bool EntryContextConfirmed=false,int? CountdownSeconds=null,string CountdownQuality="UNKNOWN",string CountdownReason="not_read",bool PanelLocated=false,int PanelDx=0,int PanelDy=0,string PanelLocation="NOT_CHECKED");
internal static class EntryReadiness
{
    // PO 2026-11-07: the unique visible performer icon is sufficient positive
    // entry evidence. Gold ornament is diagnostic only. Panel conflicts still deny.
    internal static bool Confirmed(Observation o)=>o.Location=="UNIQUE_CANDIDATE" && o.Count==1 && o.PanelLocation is not ("AMBIGUOUS" or "SEARCH_LIMIT" or "UNRESOLVED_PANEL_CANDIDATE" or "SCALE_CONFLICT") &&
        (o.D2=="NOT_VISIBLE" || o.D2=="UNKNOWN" && !o.PanelLocated && o.PanelLocation!="UNIQUE_PANEL");
}
internal sealed class AutomationController(int maxStarts,double epoch)
{
    internal Phase State{get;private set;}=Phase.Watching;
    internal string Message{get;private set;}="正在确认游戏画面";
    internal int Starts{get;private set;}
    internal int Returns{get;private set;}
    internal bool WantsStart{get;private set;}
    internal double? PerformingSince{get;private set;}
    internal bool Done=>State is Phase.Paused or Phase.Stopped or Phase.Completed;
    internal CountdownTracker Countdown{get;}=new();
    double lastSource=epoch;int entry,panel;bool hasPanel,knownStart,actionPending;
    internal int NextObservationDelayMs(double now)=>!Done&&State!=Phase.Starting&&Countdown.Acquiring(now)?1000:State switch
    {
        Phase.Performing=>Countdown.DelayMs(now),
        Phase.Watching or Phase.ConfirmingEntry=>1000,
        _=>5000
    };
    internal void Pause(string reason){Countdown.Unavailable();State=Phase.Paused;Message=reason;WantsStart=false;entry=panel=0;}
    internal void Stop(){Countdown.Unavailable();State=Phase.Stopped;Message="已停止后续操作，不会打断正在进行的表演";WantsStart=false;}
    internal void Observe(Observation o)
    {
        if(Done || State==Phase.Starting)return;
        WantsStart=false;
        if(!o.Identity){Pause("游戏窗口已失效，未进行新的点击");return;}
        if(!o.Geometry)
        {if(double.IsFinite(o.Now))lastSource=Math.Max(lastSource,o.Now);Uncertain("游戏窗口坐标已变化，等待新画面重新定位");return;}
        if(!double.IsFinite(o.Source) || !double.IsFinite(o.Now) || o.Source<=lastSource || o.Source>o.Now || o.Now-o.Source>3000)
        {Uncertain("画面过期或未更新");return;}
        lastSource=o.Source;
        bool isEntry=EntryReadiness.Confirmed(o);
        bool isPanel=o.Location=="NO_VERIFIED_CANDIDATE" && o.Count==0 && o.D2=="VISIBLE";
        if(!isEntry && !isPanel){Uncertain(o.Location=="UNIQUE_CANDIDATE" && o.Count==1 && o.D2=="UNKNOWN"?"入口与表演界面证据冲突，等待新画面确认":"画面证据不足，请保持入口/表演界面可见");return;}
        if(isPanel)
        {
            Countdown.Observe(o.CountdownSeconds,o.CountdownQuality,o.Source);
            entry=0;panel++;WantsStart=false;
            if(panel>=2)
            {if(!hasPanel){PerformingSince=o.Now;knownStart=false;Countdown.BeginAcquisition(o.Now);}hasPanel=true;State=Phase.Performing;Message=knownStart?"检测到表演面板，正在后台观察，不发送输入":"已接管正在进行的表演：只观察，自然结束后再续演，不重复点击";}
            return;
        }
        Countdown.Unavailable();panel=0;entry++;if(entry<2){State=Phase.ConfirmingEntry;Message="检测到入口，等待第二张新画面确认";return;}
        if(hasPanel)
        {
            // PO policy: in-game interruption is not a request to stop the assistant.
            // No duration inference grants input: the same entry/preflight gates still apply.
            Returns++;hasPanel=false;knownStart=false;PerformingSince=null;
            if(maxStarts>0 && Starts>=maxStarts){State=Phase.Completed;Message="已达到本次轮数上限，停止后续操作";return;}
        }
        State=Phase.WaitingIdle;WantsStart=true;Message="入口已确认，准备短暂切回游戏";
    }
    void Uncertain(string reason){Countdown.Unavailable();entry=panel=0;WantsStart=false;State=Phase.WaitingVisibility;Message=reason+"；等待界面恢复，继续观察，不会点击";}
    internal void ObservationUnavailable(string reason){if(!Done)Uncertain(reason);}
    internal void Deferred(string? reason=null){if(!Done){State=Phase.WaitingIdle;WantsStart=false;Message=reason??"等待接管确认";}}
    internal void BeginAction(){if(!WantsStart || Done)throw new InvalidOperationException("no current start evidence");State=Phase.Starting;WantsStart=false;actionPending=true;}
    internal void ActionDeferred(string reason)
    {if(!actionPending)throw new InvalidOperationException("no pending action receipt");actionPending=false;if(Done)return;Uncertain(reason);}
    internal void ActionFinished(bool started,bool restored,double now,double source,string? error,bool controlTransferred=false)
    {
        if(!actionPending)throw new InvalidOperationException("no pending action receipt");actionPending=false;
        if(started)Starts++;
        if(State==Phase.Stopped)return;
        if(!started){Pause(error??"未确认进入表演，已暂停");return;}
        Countdown.Clear();Countdown.BeginAcquisition(now);lastSource=Math.Max(lastSource,source);entry=panel=0;hasPanel=true;knownStart=true;PerformingSince=now;
        if(!restored && !controlTransferred){Pause(error??"已进入表演，但原窗口恢复未确认，已暂停");return;}
        State=Phase.Performing;Message=controlTransferred?$"第 {Starts} 次开始已确认，尊重当前窗口与键鼠控制，继续观察":$"第 {Starts} 次开始已观察到画面反馈，已交还原窗口";
    }
    internal bool AdoptStartCountdown(IReadOnlyList<Observation> samples,double inputCompleted,double now)
    {
        if(State!=Phase.Performing||!knownStart||samples.Count is <1 or >3||!double.IsFinite(inputCompleted)||inputCompleted<=0||!double.IsFinite(now))return false;
        var last=samples[^1];
        if(last.Source!=lastSource||now<last.Now||now-last.Source>3000)return false;
        double previous=inputCompleted;
        foreach(var sample in samples){
            bool valid=sample.Identity&&sample.Geometry&&sample.D2=="VISIBLE"&&sample.Location=="NO_VERIFIED_CANDIDATE"&&sample.Count==0&&
                double.IsFinite(sample.Source)&&double.IsFinite(sample.Now)&&sample.Source>previous&&sample.Now>=sample.Source&&sample.Now<=now&&sample.Now-sample.Source<=3000;
            if(!valid){Countdown.Unavailable();continue;}
            previous=sample.Source;Countdown.Observe(sample.CountdownSeconds,sample.CountdownQuality,sample.Source);
        }
        return Countdown.EndSourceMs.HasValue;
    }
}
