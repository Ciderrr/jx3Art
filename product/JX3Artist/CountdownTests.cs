namespace JX3Artist;

internal static class CountdownTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool ok){if(!ok)throw new Exception("countdown contract failed");}
        const string Good="DUAL_PASS_AGREEMENT";
        CountdownTracker Track(int seconds=1200)
        {var t=new CountdownTracker();t.Observe(seconds,Good,1000);t.Observe(seconds-1,Good,2000);return t;}
        Observation Panel(double source,int? seconds)=>new(source,source+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownSeconds:seconds,CountdownQuality:seconds.HasValue?Good:"UNKNOWN");
        test("display phase reproduces one-second-late old estimate without a fixed offset",()=>
        {
            var t=new CountdownTracker();foreach(double source in new[]{100d,800d,1200d,1900d})t.Observe((int)Math.Ceiling((10000-source)/1000),Good,source);
            Check(t.DisplayDeadline==10000 && t.DisplayUncertaintyMs==100);
            Check(CountdownTracker.DisplaySeconds(t.EndSourceMs!.Value,10100)==1);
            Check(CountdownTracker.DisplaySeconds(t.DisplayDeadline!.Value,10100)==0);
        });
        test("display phase learns distinct boundaries from source samples instead of subtracting a second",()=>
        {
            foreach(double end in new[]{10000d,10400d,10800d}){
                var t=new CountdownTracker();for(double source=100;source<5000;source+=137)t.Observe((int)Math.Ceiling((end-source)/1000),Good,source);
                Check(t.DisplayUncertaintyMs<100 && Math.Abs(t.DisplayDeadline!.Value-end)<=t.DisplayUncertaintyMs+0.001);
                for(double now=5000;now<end+2000;now+=113)if(Math.Abs((end-now)%1000)>100 && Math.Abs((end-now)%1000)<900)Check(CountdownTracker.DisplaySeconds(t.DisplayDeadline!.Value,now)==Math.Max(0,(int)Math.Ceiling((end-now)/1000)));
            }
        });
        test("occlusion and resets discard old display phase",()=>
        {var t=Track();Check(t.DisplayDeadline.HasValue);t.Unavailable();Check(t.DisplayDeadline==null);t.Observe(100,Good,5000);Check(t.DisplayDeadline==null);t.Observe(99,Good,6000);Check(t.DisplayDeadline.HasValue);t.Observe(1799,Good,7000);Check(t.DisplayDeadline==null);});
        test("a zero read never predicts a positive displayed remainder",()=>
        {var t=new CountdownTracker();t.Observe(1,Good,1000);t.Observe(0,Good,2000);Check(CountdownTracker.DisplaySeconds(t.DisplayDeadline!.Value,2001)==0);});
        test("near-end cadence includes processing time and never overlaps work",()=>
        {
            Check(ObservationSchedule.Plan(1000,1300,1000)==(2000d,700));
            Check(ObservationSchedule.Plan(1000,2500,1000)==(2500d,0));
            Check(ObservationSchedule.Plan(1000,2500,5000)==(6000d,3500));
        });
        test("production read-only loop does not add an idle second after slow near-end capture",()=>
        {
            double now=1000;var c=new AutomationController(0,0);var starts=new List<double>();
            ReadOnlyCountdownLoop.Run(c,11000,()=>now,()=>{starts.Add(now);double source=now;now+=1500;return Panel(source,Math.Max(0,(int)Math.Ceiling((10000-source)/1000)));},ms=>now+=ms,(_,_,_)=>{},CancellationToken.None,1000);
            Check(starts.Count>3 && starts.Zip(starts.Skip(1),(a,b)=>b-a).All(ms=>ms==1500) && c.Starts==0 && !c.WantsStart);
        });
        test("ambiguous complete time lines veto even other agreeing passes",()=>{Check(CountdownReader.Combine(new[]{"12:34","12:34","__AMBIGUOUS_TIME_LINES__"},"test").Seconds==null);});
        test("semantic OCR search selects only a unique strict time line",()=>{Check(CountdownReader.SearchText(new[]{"flowers","12:34"},"noise")=="12:34");Check(CountdownReader.SearchText(new[]{"12:34","12:34"},"same")=="__AMBIGUOUS_TIME_LINES__");Check(CountdownReader.Parse(CountdownReader.SearchText(new[]{"12.34"},"12.34"))==null);});
        test("cross-region recovery cannot overrule a valid conflicting first reading",()=>{var good=new CountdownReading(754,"DUAL_PASS_AGREEMENT","12:34 | 12:34","agreed");Check(CountdownReader.Merge(CountdownReading.Unknown("insufficient","12:35"),good).Seconds==null);Check(CountdownReader.Merge(CountdownReading.Unknown("insufficient","garbage"),good).Seconds==754);});
        test("timer syntax accepts strict MM:SS including zero",()=>
        {Check(CountdownReader.Parse("29:52")==1792);Check(CountdownReader.Parse("00:00")==0);Check(CountdownReader.Parse("01:00")==60);Check(CountdownReader.Parse(" 06：54 ")==414);});
        test("timer parser never repairs guessed punctuation or digits",()=>
        {foreach(var s in new[]{"29.52","29252","O0:58","29:60","29:5","129:59","6:54","text 29:59","29:59\n00:58","-1:10","1:00 extra",""})Check(CountdownReader.Parse(s)==null);});
        test("OCR separated digit tokens preserve all four actual digits",()=>
        {Check(CountdownReader.Parse("1 0 ： 0 1")==601);Check(CountdownReader.Parse("0 1 ： 0 0")==60);});
        test("OCR normalization cannot merge separate text lines",()=>
        {Check(CountdownReader.Parse("29\n:59")==null);Check(CountdownReader.Parse("1\n0:01")==null);});
        test("one valid preprocessing cannot authorize numeric prediction",()=>
        {Check(CountdownReader.Combine(new[]{"02:58","","garbage",""},"test").Seconds==null);});
        test("any valid disagreement defeats even a majority",()=>
        {var r=CountdownReader.Combine(new[]{"02:58","02:58","02:58","02:59"},"test");Check(r.Seconds==null && r.Reason=="ocr_valid_passes_conflict");});
        test("two consistent reads may corroborate without guessing invalid glyphs",()=>
        {Check(CountdownReader.Combine(new[]{"02:58","O2:58","02 ： 58","02.58"},"test").Seconds==178);});
        test("unsupported pixel geometry never invokes numeric recognition",()=>
        {var r=new CountdownReader().Read(new byte[4],1,1,CancellationToken.None).GetAwaiter().GetResult();Check(r.Seconds==null && r.Quality=="UNKNOWN" && r.Reason=="unsupported_geometry");});
        test("cancelled numeric recognition exits before processing pixels",()=>
        {using var stop=new CancellationTokenSource();stop.Cancel();try{new CountdownReader().Read(new byte[4],1,1,stop.Token).GetAwaiter().GetResult();throw new Exception("ignored cancellation");}catch(OperationCanceledException){}});
        test("one numeric frame is not a trusted schedule",()=>
        {var t=new CountdownTracker();t.Observe(1200,Good,1000);Check(t.EndSourceMs==null && t.DelayMs(1100)==1000);});
        test("two sequential reads establish end from frame source time",()=>
        {var t=Track();Check(t.EndSourceMs==1201000);});
        test("normal decrement retains the same expected end",()=>
        {var t=Track();t.Observe(1080,Good,121000);Check(t.EndSourceMs==1201000 && t.Reanchors==0);});
        test("single upward jump immediately withdraws trusted end",()=>
        {var t=Track();t.Observe(1790,Good,121000);Check(t.EndSourceMs==null && t.DelayMs(121100)==1000 && t.Reanchors==0);});
        test("confirmed upward reset replaces previous expected end",()=>
        {var t=Track();t.Observe(1790,Good,121000);t.Observe(1789,Good,122000);Check(t.EndSourceMs==1911000 && t.Reanchors==1);});
        test("confirmed downward change also needs reanchoring",()=>
        {var t=Track();t.Observe(20,Good,121000);Check(t.EndSourceMs==null);t.Observe(19,Good,122000);Check(t.EndSourceMs==141000 && t.Reanchors==1);});
        test("one misread cannot remain trusted when normal timer returns",()=>
        {var t=Track();t.Observe(1790,Good,121000);t.Observe(1079,Good,122000);Check(t.EndSourceMs==null);t.Observe(1078,Good,123000);Check(t.EndSourceMs==1201000 && t.Reanchors==0);});
        test("alternating inconsistent readings never establish an end",()=>
        {var t=new CountdownTracker();for(int i=0;i<10;i++){t.Observe(i%2==0?1000:1500,Good,1000+i*1000);Check(t.EndSourceMs==null);}});
        test("map occlusion invalidates prediction until two new reads",()=>
        {var t=Track();t.Unavailable();Check(t.EndSourceMs==null);t.Observe(1100,Good,101000);Check(t.EndSourceMs==null);t.Observe(1099,Good,102000);Check(t.EndSourceMs==1201000);});
        test("reset while hidden reanchors only after new agreement",()=>
        {var t=Track();t.Unavailable();t.Observe(1700,Good,101000);t.Observe(1699,Good,102000);Check(t.EndSourceMs==1801000 && t.Reanchors==1);});
        test("duplicate and backwards timestamps cannot confirm timer",()=>
        {foreach(double time in new[]{1000d,900d,double.NaN,double.PositiveInfinity}){var t=new CountdownTracker();t.Observe(1200,Good,1000);t.Observe(1200,Good,time);Check(t.EndSourceMs==null);}});
        test("invalid number or quality never establishes schedule",()=>
        {foreach(int? v in new int?[]{null,-1,6000}){var t=Track();t.Observe(v,Good,3000);Check(t.EndSourceMs==null);}var q=Track();q.Observe(1198,"UNKNOWN",3000);Check(q.EndSourceMs==null);});
        test("long timer checks are capped by two-minute health interval",()=>
        {var t=Track();Check(t.DelayMs(2100)==120000);});
        test("schedule targets five-minute boundary without oversleeping",()=>
        {var t=Track(310);Check(t.DelayMs(2000)==9000);});
        test("five-minute-to-one-minute range uses at most thirty seconds",()=>
        {var t=Track(200);Check(t.DelayMs(2000)==30000);Check(t.DelayMs(136000)==5000);});
        test("last minute-to-ten-seconds uses at most five seconds",()=>
        {var t=Track(50);Check(t.DelayMs(2000)==5000);Check(t.DelayMs(39000)==2000);});
        test("last ten seconds and predicted expiry only poll, never authorize",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,10));c.Observe(Panel(2000,9));Check(c.NextObservationDelayMs(10500)==1000 && !c.WantsStart);Check(c.NextObservationDelayMs(50000)==1000 && !c.WantsStart && c.Starts==0);});
        test("missing OCR uses bounded fallback, not a fabricated thirty-minute clock",()=>
        {var t=new CountdownTracker();for(int n=0;n<20;n++)t.Unavailable();Check(t.EndSourceMs==null && t.DelayMs(1000)==30000);});
        test("old frame with timer cannot change production controller schedule",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,1200));c.Observe(Panel(2000,1199));Check(c.Countdown.EndSourceMs.HasValue);c.Observe(Panel(1500,1799));Check(!c.WantsStart && c.Countdown.EndSourceMs==null);});
        test("visible panel numeric reset does not request a start",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,1200));c.Observe(Panel(2000,1199));c.Observe(Panel(121000,1799));c.Observe(Panel(122000,1798));Check(c.State==Phase.Performing && c.Countdown.Reanchors==1 && c.Starts==0 && !c.WantsStart);});
        test("map restoration in production requires fresh numeric confirmation",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,1200));c.Observe(Panel(2000,1199));c.Observe(Panel(3000,null) with{D2="UNKNOWN"});Check(c.Countdown.EndSourceMs==null && !c.WantsStart);c.Observe(Panel(4000,1197));Check(c.Countdown.EndSourceMs==null);c.Observe(Panel(5000,1196));Check(c.Countdown.EndSourceMs.HasValue && c.State==Phase.Performing && !c.WantsStart);});
        test("numeric tracking does not turn interruption into an assistant stop",()=>
        {var c=new AutomationController(2,0);var entry=new Observation(1000,1100,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE");c.Observe(entry);c.Observe(entry with{Source=2000,Now=2100});c.BeginAction();c.ActionFinished(true,true,3000,2900,null);c.Observe(Panel(4000,1799));c.Observe(Panel(5000,1798));c.Observe(entry with{Source=6000,Now=6100});Check(!c.WantsStart);c.Observe(entry with{Source=7000,Now=7100});Check(!c.Done && c.WantsStart && c.Starts==1);});
        test("stop remains terminal even if predicted countdown expires",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,10));c.Observe(Panel(2000,9));c.Stop();c.Observe(Panel(20000,0));Check(c.State==Phase.Stopped && c.Countdown.EndSourceMs==null && !c.WantsStart);});
        test("new round clears old timer instead of inheriting deadline",()=>
        {var t=Track();t.Clear();Check(t.EndSourceMs==null);t.Observe(1799,Good,2000000);Check(t.EndSourceMs==null);});
        test("numeric text alone cannot qualify a panel or override conflict",()=>
        {var p=Panel(1000,1200);Check(CountdownReader.Eligible(p));foreach(var bad in new[]{p with{Identity=false},p with{Geometry=false},p with{D2="UNKNOWN"},p with{D2="NOT_VISIBLE"},p with{Count=1},p with{Location="UNIQUE_CANDIDATE"}})Check(!CountdownReader.Eligible(bad));});
        test("capture gap immediately revokes old entry and numeric confirmation",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,1200));c.Observe(Panel(2000,1199));c.ObservationUnavailable("capture failure");Check(c.Countdown.EndSourceMs==null && !c.WantsStart && c.State==Phase.WaitingVisibility);c.Observe(Panel(3000,1198));Check(c.Countdown.EndSourceMs==null);c.Observe(Panel(4000,1197));Check(c.Countdown.EndSourceMs.HasValue);});
        test("OCR unknown at visible panel falls back without terminal pause",()=>
        {var c=new AutomationController(2,0);c.Observe(Panel(1000,62));c.Observe(Panel(2000,61));c.Observe(Panel(3000,null));Check(c.State==Phase.Performing && !c.Done && !c.WantsStart && c.Countdown.EndSourceMs==null && c.NextObservationDelayMs(3100)==5000);c.Observe(Panel(8000,55));Check(c.Countdown.EndSourceMs==null);c.Observe(Panel(9000,54));Check(c.Countdown.EndSourceMs==63000 && !c.WantsStart);});
        test("historical VIS observed numeric jumps reanchor without claiming mechanism",()=>
        {
            // Values/times independently annotated in vis1/roi_metadata.json; source milliseconds simulated.
            var t=new CountdownTracker();t.Observe(1550,Good,1000);t.Observe(1549,Good,2000);
            t.Observe(1518,Good,337000);Check(t.EndSourceMs==null);
            t.Observe(922,Good,936000);Check(t.EndSourceMs.HasValue && t.Reanchors==1);
            t.Observe(414,Good,1441000);t.Observe(58,Good,1799000);Check(t.EndSourceMs.HasValue);
            t.Observe(1744,Good,1915000);Check(t.EndSourceMs==null);
            t.Observe(1628,Good,2034000);Check(t.EndSourceMs.HasValue && t.Reanchors==2);
        });
        test("simulated full countdown uses adaptive observations and never timer-only input",()=>
        {
            var c=new AutomationController(2,0);double source=1000,end=1801000;int samples=0;
            while(source<=end+1000)
            {int remaining=(int)Math.Max(0,Math.Ceiling((end-source)/1000));c.Observe(Panel(source,remaining));samples++;Check(!c.WantsStart && c.Starts==0);source+=c.NextObservationDelayMs(source+100);}
            Check(samples>20 && samples<100 && c.State==Phase.Performing);
        });
    }
}
