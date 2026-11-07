using Jx3.OneClickTest;
namespace JX3Artist;
internal static class PreclickEvidence
{
    internal static bool CanReuse(Observation observation,Check check)=>EntryReadiness.Confirmed(observation)&&ClickPolicy.Refusal(check)==null;
    internal static bool TryReuse(Observation observation,Func<Check> readCurrent)
    {if(!EntryReadiness.Confirmed(observation))return false;try{return CanReuse(observation,readCurrent());}catch(System.IO.IOException){return false;}}
    internal static bool FreshPair(double first,double second,double foregroundSince)=>double.IsFinite(first)&&double.IsFinite(second)&&double.IsFinite(foregroundSince)&&second>first&&second>=foregroundSince;
    internal static T RecoverStale<T>(T first,Func<T,string?> refusal,Func<T> capture,Action<T,int> diagnostic,CancellationToken stop)
    {
        var current=first;
        for(int attempt=0;attempt<2;attempt++){stop.ThrowIfCancellationRequested();if(refusal(current)!="STALE_FRAME")break;diagnostic(current,attempt+1);current=capture();}
        return current;
    }
    internal static void AddTests(Action<string,Action> test)
    {
        test("preclick reuses only still-valid current entry evidence",()=>{
            var o=new Observation(1000,2000,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true);
            var c=new Check(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED","UNIQUE_CANDIDATE",1,1920,1080,1000,new(500,500,572,566),"UNKNOWN",WindowFrame:true);
            if(!CanReuse(o,c))throw new Exception("fresh seed rejected");
            foreach(var bad in new[]{c with{AgeMs=3001},c with{Geometry=false},c with{Identity=false},c with{Foreground=false},c with{Cancelled=true},c with{ButtonHeld=true},c with{HitTarget=false}})if(CanReuse(o,bad))throw new Exception("invalid seed reused");
            if(CanReuse(o with{PanelLocation="AMBIGUOUS"},c)||CanReuse(o with{Location="NO_VERIFIED_CANDIDATE",Count=0},c))throw new Exception("weak seed reused");
            if(!CanReuse(o with{EntryContextConfirmed=false},c))throw new Exception("gold ornament incorrectly gates reuse");
            if(TryReuse(o,()=>throw new System.IO.IOException("changed geometry")))throw new Exception("changed seed did not fall back");
        });
        test("preclick second source must be distinct and after foreground activation",()=>{
            if(!FreshPair(1000,2100,2000)||FreshPair(1000,1000,900)||FreshPair(1000,1900,2000)||FreshPair(double.NaN,2100,2000))throw new Exception("preclick freshness");
        });
        test("stale preclick refreshes current evidence with bounded retries and respects cancellation",()=>{
            int captures=0,diagnostics=0;int result=RecoverStale(0,n=>n==0?"STALE_FRAME":null,()=>{captures++;return 1;},(_,_)=>diagnostics++,CancellationToken.None);
            if(result!=1||captures!=1||diagnostics!=1)throw new Exception("stale did not recover");
            captures=0;RecoverStale(0,_=>"STALE_FRAME",()=>{captures++;return 0;},(_,_)=>{},CancellationToken.None);if(captures!=2)throw new Exception("unbounded recovery");
            captures=0;RecoverStale(0,_=>"NO_UNIQUE_CURRENT_TARGET",()=>{captures++;return 1;},(_,_)=>{},CancellationToken.None);if(captures!=0)throw new Exception("ambiguity retried blindly");
            using var stop=new CancellationTokenSource();stop.Cancel();try{RecoverStale(0,_=>"STALE_FRAME",()=>1,(_,_)=>{},stop.Token);throw new Exception("ignored stop");}catch(OperationCanceledException){}
        });
    }
}
