namespace JX3Artist;
internal static class ExperienceEstimateTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("experience estimate assertion failed");}
        test("whole minutes only and reading never adds elapsed application uptime",()=>{
            var e=new ExperienceEstimate(0);Check(e.Read().EstimatedGain==0&&e.Read().ConfirmedGain is null);
            e.Observe(0,0,true,0);e.Observe(59999,59999,true,0);Check(e.Read().EstimatedGain==0);
            e.Observe(60000,60000,true,0);Check(e.Read().EstimatedGain==6);e.Observe(120000,120000,true,0);Check(e.Read().EstimatedGain==12);
            Check(e.Read()==e.Read());
        });
        test("two partial performances cannot be rounded as one full minute",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,1);e.Observe(40000,40000,true,1);e.RoundEnded();
            e.Observe(50000,50000,true,2);e.Observe(90000,90000,true,2);Check(e.Read().EstimatedGain==0&&e.Read().ObservedMilliseconds==80000);
        });
        test("unknown and stopped intervals are excluded and restarts retain earned estimates",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,1);e.Observe(90000,90000,true,1);e.Suspend();
            Check(e.Read().EstimatedGain==6);e.Observe(900000,900000,true,0);e.Observe(960000,960000,true,0);
            Check(e.Read().EstimatedGain==12&&e.Read().ObservedMilliseconds==150000&&e.Read().HasUnobservedTime);
        });
        test("missed observation deadline starts a new segment without crediting the gap",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,0,33000);e.Observe(30000,30000,true,0,33000);
            e.Observe(120000,120000,true,0,33000);Check(e.Read().EstimatedGain==0&&e.Read().ObservedMilliseconds==30000);
            e.Observe(150000,150000,true,0,33000);Check(e.Read().EstimatedGain==0&&e.Read().ObservedMilliseconds==60000);
        });
        test("duplicate or out of order frames neither double count nor discard a fraction",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,0);e.Observe(40000,40000,true,0);
            Check(!e.Observe(40000,40001,true,0));Check(!e.Observe(30000,40002,true,0));
            e.Observe(60000,60000,true,0);Check(e.Read().EstimatedGain==6);
        });
        test("stale future and invalid source ages never grant more estimated time",()=>{
            foreach(int kind in new[]{0,1,2}){
                var e=new ExperienceEstimate(0);e.Observe(0,0,true,0);e.Observe(60000,60000,true,0);
                bool accepted=kind switch{0=>e.Observe(70000,73001,true,0),1=>e.Observe(70001,70000,true,0),_=>e.Observe(-1,70000,true,0)};
                Check(!accepted&&e.Read().EstimatedGain==6&&e.Read().HasUnobservedTime);
            }
        });
        test("unknown panel evidence closes the interval without pretending confirmed zero",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,0);e.Observe(60000,60000,true,0);e.Observe(120000,120000,false,0);
            Check(e.Read().EstimatedGain==6&&e.Read().ConfirmedGain is null&&e.Read().HasUnobservedTime);
        });
        test("round changes settle independently even if no intermediate entry frame was forwarded",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,1);e.Observe(90000,90000,true,1);
            e.Observe(100000,100000,true,2);e.Observe(130000,130000,true,2);Check(e.Read().EstimatedGain==6);
        });
        test("manual initialization anchors future estimates without resetting this application",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,0);e.Observe(60000,60000,true,0);e.Initialize(5,42);
            Check(e.Read().Baseline==new ExperienceBaseline(5,42,6)&&e.Read().EstimatedSinceInitialization==0);
            e.Observe(120000,120000,true,0);Check(e.Read().EstimatedGain==12&&e.Read().EstimatedSinceInitialization==6);
            e.Initialize(6,10);Check(e.Read().EstimatedGain==12&&e.Read().EstimatedSinceInitialization==0&&e.Read().Baseline!.Level==6);
        });
        test("invalid manual inputs do not replace an accepted baseline",()=>{
            var e=new ExperienceEstimate(0);e.Initialize(3,8);int rejected=0;
            try{e.Initialize(0,8);}catch(ArgumentException){rejected++;}try{e.Initialize(3,-1);}catch(ArgumentException){rejected++;}
            Check(rejected==2&&e.Read().Baseline==new ExperienceBaseline(3,8,0));
        });
        test("application close is idempotent and later evidence or initialization is refused",()=>{
            var e=new ExperienceEstimate(0);e.Observe(0,0,true,0);e.Observe(60000,60000,true,0);e.Close();var snapshot=e.Read();
            e.Close();Check(!e.Observe(120000,120000,true,0)&&!e.Initialize(3,4)&&snapshot==e.Read()&&snapshot.Closed);
            var next=new ExperienceEstimate(200000);Check(next.Read().EstimatedGain==0&&next.Read().Baseline is null);
        });
        test("observation interval budget cannot be expanded by a caller",()=>{
            var e=new ExperienceEstimate(0);Check(!e.Observe(0,0,true,0,ExperienceEstimate.MaximumGapMs+1));Check(!e.Observe(0,0,true,0,0));
            Check(e.Read().EstimatedGain==0&&e.Read().ConfirmedGain is null);
        });
        test("concurrent observation settlement and reads expose one consistent snapshot",()=>{
            var e=new ExperienceEstimate(0);using var start=new ManualResetEventSlim(false);
            var writer=Task.Run(()=>{start.Wait();for(int index=0;index<2000;index++){long source=index*120000L;e.Observe(source,source,true,index);e.Observe(source+60000,source+60000,true,index);e.RoundEnded();}});
            var reader=Task.Run(()=>{start.Wait();for(int index=0;index<10000;index++){var view=e.Read();Check(view.EstimatedGain==view.ObservedMilliseconds/60000*6&&view.ConfirmedGain is null);}});
            start.Set();Task.WaitAll(writer,reader);Check(e.Read().EstimatedGain==12000);
        });
    }
}
