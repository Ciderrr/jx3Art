using Int1;
namespace Jx3.OneClickTest;

internal enum FeedbackState { NotSubmitted, AwaitingFeedback, FeedbackUnknown, StartFeedbackObserved, InputRejected, TimedOut, Cancelled, Aborted, FeedbackInsufficient }
internal record FeedbackSample(double SourceQpcMs,double NowQpcMs,bool Identity,bool Foreground,bool Geometry,
    string Location,int CandidateCount,string D2,bool CaptureOk=true,bool StopButtonVisible=false);
internal enum FeedbackFocus { Foreground, Background, Any }

// Visual response only, NOT a full game-state/complete-performance classifier.
// The same instance is called by the runtime and offline state-machine tests.
internal sealed class StartFeedback(FeedbackFocus requiredFocus=FeedbackFocus.Foreground)
{
    internal const double WindowMs=10000;
    internal FeedbackState State{get;private set;}=FeedbackState.NotSubmitted;
    internal string Reason{get;private set;}="no input submitted";
    internal int Consecutive{get;private set;}
    internal bool Done{get;private set;}
    internal bool EverObserved{get;private set;}
    double start,lastNow,lastSource;
    internal void InputCompleted(uint press,uint release,double now)
    {
        if(State!=FeedbackState.NotSubmitted || Done)throw new InvalidOperationException("input completion already recorded or stopped");
        if(press!=2 || release!=1 || !double.IsFinite(now) || now<=0){Stop(FeedbackState.InputRejected,"input incomplete or invalid completion clock");return;}
        start=lastNow=now;State=FeedbackState.AwaitingFeedback;Reason="awaiting fresh post-input samples";
    }
    void Unknown(string reason){Consecutive=0;State=FeedbackState.FeedbackUnknown;Reason=reason;}
    void Stop(FeedbackState state,string reason){Consecutive=0;State=state;Reason=reason;Done=true;}
    internal void Cancel()=>StopUnlessDone(FeedbackState.Cancelled,"operator cancelled; no retry");
    internal void Abort(string reason)=>StopUnlessDone(FeedbackState.Aborted,reason);
    void StopUnlessDone(FeedbackState state,string reason){if(!Done)Stop(state,reason);}
    bool CheckClock(double now)
    {
        if(!double.IsFinite(now) || now<lastNow){Unknown("invalid/backwards observation clock");return false;}
        lastNow=now;
        if(now-start>WindowMs){Stop(FeedbackState.TimedOut,"bounded feedback window expired; no retry");return false;}
        return true;
    }
    internal void Observe(FeedbackSample sample)
    {
        if(Done)return;
        if(State==FeedbackState.NotSubmitted)throw new InvalidOperationException("cannot attribute feedback before input completion");
        if(!sample.Identity){Abort("target identity lost");return;}
        if(requiredFocus!=FeedbackFocus.Any && sample.Foreground!=(requiredFocus==FeedbackFocus.Foreground)){Abort(requiredFocus==FeedbackFocus.Foreground?"game lost foreground":"game became foreground");return;}
        if(!sample.Geometry){Abort("capture geometry changed");return;}
        if(!CheckClock(sample.NowQpcMs))return;
        if(!sample.CaptureOk){Unknown("capture/detection failed");return;}
        double source=sample.SourceQpcMs;
        if(!double.IsFinite(source) || source<=start || source>sample.NowQpcMs || sample.NowQpcMs-source>MonitorCore.StaleAfterMs)
        {Unknown("stale, future or pre-input source frame");return;}
        if(source<=lastSource){Unknown("duplicate/out-of-order source frame");return;}
        lastSource=source;
        // No candidate is NOT a global absence claim. It only avoids an explicit
        // conflicting entry candidate while requiring independent positive D2 evidence.
        if((sample.D2=="VISIBLE"||sample.StopButtonVisible) && sample.Location=="NO_VERIFIED_CANDIDATE" && sample.CandidateCount==0)
        {
            Consecutive++;State=Consecutive>=2?FeedbackState.StartFeedbackObserved:FeedbackState.AwaitingFeedback;
            Reason=Consecutive>=2?(sample.StopButtonVisible?"two distinct fresh frames show performance feedback":"two distinct fresh frames show countdown-panel feedback"):"one feedback sample; another required";
            if(State==FeedbackState.StartFeedbackObserved)EverObserved=true;
        }
        else Unknown(sample.D2=="VISIBLE"?"panel and entry/localization evidence conflict":"no sufficient start feedback");
    }
    internal void Complete(double now)
    {
        if(Done)return;
        if(State==FeedbackState.NotSubmitted){Abort("completed without input");return;}
        if(!CheckClock(now)){if(!Done)Stop(FeedbackState.FeedbackInsufficient,"invalid completion clock; no retry");return;}
        if(State==FeedbackState.StartFeedbackObserved && now-lastSource<=MonitorCore.StaleAfterMs)
        {Done=true;return;}
        Stop(FeedbackState.FeedbackInsufficient,"bounded sample budget ended without current sufficient feedback; no retry");
    }
}
