namespace JX3Artist;

// Only schedules observations. This type has no authority to request game input.
internal sealed class CountdownTracker
{
    const double ToleranceMs=3000;
    double lastSource=double.NegativeInfinity;double? pendingEnd,previousEnd;
    int misses;double? phaseLow,phaseHigh;bool observedZero;
    double acquireUntil=double.NegativeInfinity;
    internal void BeginAcquisition(double now){if(double.IsFinite(now))acquireUntil=EndSourceMs.HasValue?double.NegativeInfinity:now+20000;}
    internal bool Acquiring(double now)=>double.IsFinite(now)&&now<acquireUntil&&!EndSourceMs.HasValue;
    // An integer N at source S constrains the on-screen zero transition to
    // (S+(N-1)*1000, S+N*1000]. It is not an exact game-expiry timestamp.
    internal double? DisplayDeadline=>EndSourceMs.HasValue && phaseLow.HasValue && phaseHigh.HasValue?(observedZero?lastSource:(phaseLow.Value+phaseHigh.Value)/2):null;
    internal double? DisplayUncertaintyMs=>DisplayDeadline.HasValue?(phaseHigh!.Value-phaseLow!.Value)/2:null;
    internal static int DisplaySeconds(double deadline,double now)=>(int)Math.Clamp(Math.Ceiling((deadline-now)/1000),0,5999);
    void Phase(double upper,bool merge)
    {
        double lower=upper-TimeSpan.FromSeconds(1).TotalMilliseconds;
        if(merge && phaseLow.HasValue && phaseHigh.HasValue && Math.Max(lower,phaseLow.Value)<=Math.Min(upper,phaseHigh.Value))
        {phaseLow=Math.Max(lower,phaseLow.Value);phaseHigh=Math.Min(upper,phaseHigh.Value);}
        else{phaseLow=lower;phaseHigh=upper;}
    }
    internal double? EndSourceMs{get;private set;}
    internal int Reanchors{get;private set;}
    internal string Status{get;private set;}="剩余时间未知";
    internal void Clear()
    {EndSourceMs=pendingEnd=previousEnd=phaseLow=phaseHigh=null;observedZero=false;lastSource=double.NegativeInfinity;misses=0;acquireUntil=double.NegativeInfinity;Status="等待新一轮倒计时";}
    internal void Unavailable()
    {EndSourceMs=pendingEnd=phaseLow=phaseHigh=null;observedZero=false;misses++;Status="未确认，等待新画面";}
    internal void Observe(int? seconds,string quality,double source)
    {
        if(!double.IsFinite(source) || source<=lastSource){Unavailable();return;}
        lastSource=source;
        if(quality!="DUAL_PASS_AGREEMENT" || seconds is null or <0 or >5999){Unavailable();return;}
        misses=0;observedZero=seconds.Value==0;double candidate=source+seconds.Value*1000d;
        if(EndSourceMs.HasValue && Math.Abs(candidate-EndSourceMs.Value)<=ToleranceMs)
        {Phase(candidate,true);EndSourceMs=previousEnd=candidate;acquireUntil=double.NegativeInfinity;Status="倒计时已确认";return;}
        // A jump invalidates the previous prediction immediately; confirm with another fresh frame.
        EndSourceMs=null;
        if(pendingEnd.HasValue && Math.Abs(candidate-pendingEnd.Value)<=ToleranceMs)
        {
            bool changed=previousEnd.HasValue && Math.Abs(candidate-previousEnd.Value)>ToleranceMs;
            if(changed)Reanchors++;
            Phase(candidate,true);EndSourceMs=previousEnd=candidate;pendingEnd=null;acquireUntil=double.NegativeInfinity;
            Status=changed?"倒计时发生变化，已重新计时":"倒计时已确认";
        }
        else{Phase(candidate,false);pendingEnd=candidate;Status=previousEnd.HasValue?"倒计时变化，正在重新确认":"正在确认倒计时";}
    }
    internal int DelayMs(double now)
    {
        if(!double.IsFinite(now))return 1000;
        if(!EndSourceMs.HasValue)return now<acquireUntil||pendingEnd.HasValue?1000:Math.Min(30000,5000*Math.Max(1,Math.Min(misses,6)));
        double remaining=EndSourceMs.Value-now;
        if(remaining>300000)return (int)Math.Clamp(remaining-300000,1000,120000);
        if(remaining>60000)return (int)Math.Clamp(remaining-60000,1000,30000);
        if(remaining>10000)return (int)Math.Clamp(remaining-10000,1000,5000);
        return 1000;
    }
}
