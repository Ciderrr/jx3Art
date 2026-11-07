namespace JX3Artist;

internal sealed record ExperienceBaseline(int Level,long Experience,long EstimateAtInitialization);
internal sealed record ExperienceEstimateView(long EstimatedGain,long ObservedMilliseconds,bool HasUnobservedTime,
    ExperienceBaseline? Baseline,long? EstimatedSinceInitialization,bool Closed)
{
    // There is no actual reward reader. An estimate must never fill this field.
    internal long? ConfirmedGain=>null;
}

// Pure application-owned accounting. No timer, capture, game, input or UI APIs.
// Only intervals between accepted performing observations are estimated. Whole
// minutes are rounded per continuous segment, never across gaps or round ends.
internal sealed class ExperienceEstimate
{
    internal const long MaximumGapMs=123000; // Existing 120s observation period + 3s source-age bound.
    readonly long openedAt;
    readonly object gate=new();
    long lastSource=-1,segmentStart,segmentEnd,segmentRound,allowedGap,closedPoints,closedDuration;
    bool active,unobserved,closed;
    ExperienceBaseline? baseline;
    internal ExperienceEstimate(long openedAtMs)
    {
        if(openedAtMs<0)throw new ArgumentOutOfRangeException(nameof(openedAtMs));
        openedAt=openedAtMs;
    }
    static long Points(long milliseconds)=>checked(milliseconds/60000*6);
    internal ExperienceEstimateView Read()
    {
        lock(gate){
        long duration=active?segmentEnd-segmentStart:0;
        long points=checked(closedPoints+Points(duration));
        return new(points,checked(closedDuration+duration),unobserved,baseline,
            baseline is null?null:points-baseline.EstimateAtInitialization,closed);
        }
    }
    void Settle()
    {
        if(!active)return;
        long duration=segmentEnd-segmentStart;
        closedDuration=checked(closedDuration+duration);closedPoints=checked(closedPoints+Points(duration));active=false;
    }
    internal bool Observe(long sourceMs,long nowMs,bool performingConfirmed,long round,long maximumGapMs=MaximumGapMs)
    {
        lock(gate){
        if(closed)return false;
        // Duplicate/out-of-order delivery cannot add time or discard fractions
        // already supported by a later observation.
        if(sourceMs>=openedAt&&sourceMs<=lastSource)return false;
        if(sourceMs<openedAt||nowMs<sourceMs||nowMs-sourceMs>3000||round<0||maximumGapMs<=0||maximumGapMs>MaximumGapMs)
        {Suspend();return false;}
        lastSource=sourceMs;
        if(!performingConfirmed){Suspend();return true;}
        if(active&&(round!=segmentRound||sourceMs-segmentEnd>allowedGap))
        {Settle();unobserved=true;}
        if(!active)
        {
            if(sourceMs>openedAt)unobserved=true;
            segmentStart=segmentEnd=sourceMs;segmentRound=round;active=true;
        }
        else segmentEnd=sourceMs;
        allowedGap=maximumGapMs;return true;
        }
    }
    internal void Suspend(){lock(gate){if(closed)return;Settle();unobserved=true;}}
    internal void RoundEnded(){lock(gate){if(!closed)Settle();}}
    internal bool Initialize(int level,long experience)
    {
        lock(gate){
        if(closed)return false;
        if(level<=0||experience<0)throw new ArgumentException("等级须为正整数，经验须为非负整数");
        baseline=new(level,experience,Read().EstimatedGain);return true;
        }
    }
    internal void Close(){lock(gate){if(closed)return;Settle();closed=true;}}
}
