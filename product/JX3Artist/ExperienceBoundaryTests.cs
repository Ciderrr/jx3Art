using System.IO;
namespace JX3Artist;
internal static class ExperienceBoundaryTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("experience boundary assertion failed");}
        ExperienceSession Session()=>new(Path.GetTempPath(),memoryOnly:true,clock:()=>0);
        Observation Panel(long source,int? seconds=null)=>new(source,source+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownSeconds:seconds,CountdownQuality:seconds.HasValue?"DUAL_PASS_AGREEMENT":"UNKNOWN",PanelLocated:true,PanelLocation:"UNIQUE_PANEL");
        void Feed(ExperienceSession e,long source,int round=0,int? seconds=null)=>e.Observe(Panel(source,seconds),Phase.Performing,round,0,120000);
        void Span(ExperienceSession e,long start,long duration,int round=0)
        {Feed(e,start,round);for(long source=start+60000;source<start+duration;source+=60000)Feed(e,source,round);Feed(e,start+duration,round);}
        test("boundary audit rounds at the exact millisecond without a near-minute bonus",()=>{
            using var e=Session();Feed(e,0);Feed(e,59999);Check(e.Read().EstimatedGain==0);Feed(e,60000);Check(e.Read().EstimatedGain==6);Feed(e,60001);Check(e.Read().EstimatedGain==6);
        });
        test("boundary audit fully observed thirty minutes remain thirty estimated minutes",()=>{
            using var e=Session();Span(e,0,1800000);e.Observe(Panel(1800100) with{D2="UNKNOWN",Location="UNIQUE_CANDIDATE",Count=1,PanelLocated=false},Phase.WaitingIdle,1,0,1000);
            Check(e.Read().EstimatedGain==180&&e.Read().ObservedMilliseconds==1800000);
        });
        test("boundary audit early interruption never rounds up from the later entry observation",()=>{
            foreach(long duration in new[]{59999L,119999L}){using var e=Session();Span(e,0,duration);e.Observe(Panel(duration+2000) with{D2="UNKNOWN",PanelLocated=false},Phase.WaitingIdle,1,0,1000);Check(e.Read().EstimatedGain==duration/60000*6);}
        });
        test("boundary audit unknown gaps do not join fractions even within the same round",()=>{
            using var e=Session();Span(e,0,40000);e.Suspend();Span(e,50000,40000);Check(e.Read().EstimatedGain==0&&e.Read().ObservedMilliseconds==80000);
        });
        test("boundary audit stop close and delayed reads add no unsampled time",()=>{
            using var e=Session();Span(e,0,59999);e.Suspend();for(int i=0;i<100;i++)Check(e.Read().EstimatedGain==0);e.Dispose();Check(e.Read().EstimatedGain==0&&e.Read().Closed);
        });
        test("boundary audit valid duplicate metadata cannot end the current accounting segment",()=>{
            using var e=Session();Feed(e,0);Feed(e,40000);
            e.Observe(Panel(40000),Phase.Performing,1,1,120000);Feed(e,60000);
            Check(e.Read().EstimatedGain==6&&e.Read().ObservedMilliseconds==60000);
        });
        test("boundary audit valid out of order countdown metadata cannot replace a newer epoch",()=>{
            using var e=Session();Feed(e,0,seconds:1799);Feed(e,40000,seconds:1759);
            e.Observe(Panel(39000,1799) with{Now=40100},Phase.Performing,0,0,120000);Feed(e,60000,seconds:1739);
            Check(e.Read().EstimatedGain==6&&e.Read().ObservedMilliseconds==60000);
        });
        test("boundary audit countdown unknown alone does not stop a confirmed performance estimate",()=>{
            using var e=Session();Feed(e,0,seconds:1799);Feed(e,40000);Feed(e,60000,seconds:1739);Check(e.Read().EstimatedGain==6);
        });
        test("boundary audit retains the 63 observed-duration arithmetic without forcing an unaligned reference",()=>{
            using var e=Session();Span(e,0,1796948);e.Suspend();Span(e,1806948,964281,1);
            Check(e.Read().EstimatedGain==270&&e.Read().ObservedMilliseconds==2761229&&e.Read().ConfirmedGain is null);
        });
    }
}
