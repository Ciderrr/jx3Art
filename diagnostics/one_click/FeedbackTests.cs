namespace Jx3.OneClickTest;
internal static class FeedbackTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Assert(bool value){if(!value)throw new Exception("feedback assertion failed");}
        StartFeedback New(){var f=new StartFeedback();f.InputCompleted(2,1,1000);return f;}
        FeedbackSample Sample(double source=1100,double now=1200)=>new(source,now,true,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE");
        StartFeedback Observed(){var f=New();f.Observe(Sample());f.Observe(Sample(1300,1400));return f;}
        void Case(string name,Action action)=>test("feedback: "+name,action);
        Case("input return alone not feedback",()=>Assert(New().State==FeedbackState.AwaitingFeedback));
        Case("before input cannot attribute",()=>{try{new StartFeedback().Observe(Sample());throw new Exception("accepted pre-input observation");}catch(InvalidOperationException){}});
        Case("partial press terminal",()=>{var f=new StartFeedback();f.InputCompleted(1,1,1000);Assert(f.Done && f.State==FeedbackState.InputRejected);});
        Case("release failure terminal",()=>{var f=new StartFeedback();f.InputCompleted(2,0,1000);Assert(f.Done && f.State==FeedbackState.InputRejected);});
        Case("invalid input time",()=>{var f=new StartFeedback();f.InputCompleted(2,1,double.NaN);Assert(f.Done && f.State==FeedbackState.InputRejected);});
        Case("one frame insufficient",()=>{var f=New();f.Observe(Sample());Assert(f.Consecutive==1 && !f.EverObserved);});
        Case("two distinct fresh frames",()=>Assert(Observed().State==FeedbackState.StartFeedbackObserved));
        Case("duplicate resets streak",()=>{var f=New();f.Observe(Sample());f.Observe(Sample(1100,1300));Assert(f.Consecutive==0 && f.State==FeedbackState.FeedbackUnknown);});
        Case("out of order does not count",()=>{var f=Observed();f.Observe(Sample(1200,1500));Assert(f.Consecutive==0 && f.State==FeedbackState.FeedbackUnknown);});
        Case("pre-input source rejected",()=>{var f=New();f.Observe(Sample(999,1200));Assert(f.Consecutive==0);});
        Case("equal input source rejected",()=>{var f=New();f.Observe(Sample(1000,1200));Assert(f.Consecutive==0);});
        Case("future source rejected",()=>{var f=New();f.Observe(Sample(1300,1200));Assert(f.Consecutive==0);});
        Case("stale source rejected",()=>{var f=New();f.Observe(Sample(1100,4101));Assert(f.Consecutive==0);});
        Case("freshness boundary inclusive",()=>{var f=New();f.Observe(Sample(1100,4100));Assert(f.Consecutive==1);});
        Case("NaN source rejected",()=>{var f=New();f.Observe(Sample(double.NaN,1200));Assert(f.Consecutive==0);});
        Case("nonfinite observation time rejected",()=>{var f=New();f.Observe(Sample(1100,double.PositiveInfinity));Assert(f.State==FeedbackState.FeedbackUnknown);});
        Case("clock regression resets then recovers",()=>{var f=New();f.Observe(Sample());f.Observe(Sample(1150,1199));f.Observe(Sample(1300,1400));Assert(f.Consecutive==1);f.Observe(Sample(1500,1600));Assert(f.State==FeedbackState.StartFeedbackObserved);});
        Case("capture failure clears current observation",()=>{var f=Observed();f.Observe(Sample(1500,1600) with{CaptureOk=false});Assert(f.State==FeedbackState.FeedbackUnknown && f.EverObserved);});
        Case("two fresh samples recover after capture failure",()=>{var f=Observed();f.Observe(Sample(1500,1600) with{CaptureOk=false});f.Observe(Sample(1700,1800));Assert(f.State!=FeedbackState.StartFeedbackObserved);f.Observe(Sample(1900,2000));Assert(f.State==FeedbackState.StartFeedbackObserved);});
        Case("panel UNKNOWN not success",()=>{var f=Observed();f.Observe(Sample(1500,1600) with{D2="UNKNOWN"});Assert(f.State==FeedbackState.FeedbackUnknown && f.Consecutive==0);});
        Case("entry and panel conflict",()=>{var f=Observed();f.Observe(Sample(1500,1600) with{Location="UNIQUE_CANDIDATE",CandidateCount=1});Assert(f.State==FeedbackState.FeedbackUnknown);});
        Case("ambiguous search never confirms",()=>{var f=New();f.Observe(Sample() with{Location="SEARCH_LIMIT"});Assert(f.Consecutive==0);});
        Case("timeout terminal prevents resurrection",()=>{var f=New();f.Observe(Sample(10900,11001));f.Observe(Sample(11100,11200));Assert(f.State==FeedbackState.TimedOut && f.Done && !f.EverObserved);});
        Case("deadline boundary inclusive",()=>{var f=New();f.Observe(Sample(10800,11000));f.Observe(Sample(10900,11000));f.Complete(11000);Assert(f.Done && f.State==FeedbackState.StartFeedbackObserved);});
        Case("cancel before input cannot restart",()=>{var f=new StartFeedback();f.Cancel();try{f.InputCompleted(2,1,1000);throw new Exception("restarted");}catch(InvalidOperationException){}Assert(f.State==FeedbackState.Cancelled && f.Done);});
        Case("cancel after feedback clears current state",()=>{var f=Observed();f.Cancel();f.Observe(Sample(1500,1600));Assert(f.State==FeedbackState.Cancelled && f.Done && f.EverObserved);});
        foreach(var row in new[]{("identity",Sample() with{Identity=false}),("foreground",Sample() with{Foreground=false}),("geometry",Sample() with{Geometry=false})})
            Case(row.Item1+" failure terminal",()=>{var f=New();f.Observe(row.Item2);f.Observe(Sample(1300,1400));Assert(f.Done && f.State==FeedbackState.Aborted);});
        Case("finish without input",()=>{var f=new StartFeedback();f.Complete(1200);Assert(f.Done && f.State==FeedbackState.Aborted);});
        Case("finish with one sample",()=>{var f=New();f.Observe(Sample());f.Complete(1300);Assert(f.Done && f.State==FeedbackState.FeedbackInsufficient);});
        Case("finish observed feedback",()=>{var f=Observed();f.Complete(1500);Assert(f.Done && f.State==FeedbackState.StartFeedbackObserved);});
        Case("finished feedback cannot be reused",()=>{var f=Observed();f.Complete(1500);f.Observe(Sample(1700,1800) with{D2="UNKNOWN"});Assert(f.Done && f.State==FeedbackState.StartFeedbackObserved);});
        Case("aged feedback not retained at finish",()=>{var f=Observed();f.Complete(4301);Assert(f.Done && f.State==FeedbackState.FeedbackInsufficient && f.EverObserved);});
        Case("invalid finish time is terminal",()=>{var f=Observed();f.Complete(double.NaN);Assert(f.Done && f.State==FeedbackState.FeedbackInsufficient);});
        Case("input completed twice refused",()=>{var f=New();try{f.InputCompleted(2,1,1100);throw new Exception("accepted duplicate");}catch(InvalidOperationException){}});
    }
}
