using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jx3.OneClickTest;

namespace JX3Artist;

internal static class WujieTrialTests
{
    internal static void Add(Action<string,Action> test,string[] args)
    {
        var box=new Box(684,967,780,1063);
        var entry=new VisualEvidence("ENTRY",box,null,null);
        var stop=new VisualEvidence("PERFORMING_STOP_BUTTON",null,null,null);
        var unknown=new VisualEvidence("UNKNOWN",null,null,null);
        Observation Sample(VisualEvidence visual,double source)=>GameSession.WujieObservation(visual,source,source+100,true,true,"saved");
        test("Wujie startup faults have distinct localized issue codes and report routing",()=>
        {
            string CountFailure(int count)
            {
                try{WujieTarget.RequireSingleProcess(count);}
                catch(IOException error){return error.Message;}
                throw new InvalidOperationException("unexpected valid process count");
            }
            var messages=new[]{CountFailure(0),CountFailure(2),WujieTarget.WindowUnavailable,WujieTarget.MultipleWindows,
                ObservationEvidence.InvalidRecognitionBounds,"极速端本地试用识别配置缺失"};
            var codes=messages.Select(message=>ProblemCatalog.Identify(message).Code).ToArray();
            Require(codes.SequenceEqual(Enumerable.Range(1,6).Select(n=>$"JX3-WUJIE-{n:000}"))&&codes.All(SupportReport.KnownCode));
            Require(ProblemCatalog.PauseMessage(messages[0]).Contains("请打开极速端"));
            Require(!ProblemCatalog.PauseMessage("Exactly one Wujie process is required").Contains("Exactly"));
            Require(ProblemCatalog.PauseMessage("Unhandled system failure").Contains("JX3-UNKNOWN-001"));
        });
        test("Wujie minimization waits for restore without treating lost identity as recoverable",()=>
        {
            var minimized=new CaptureEvidence("before_pull",true,true);
            var reasons=CaptureDiagnostics.Reasons(minimized);
            Require(reasons.SequenceEqual(new[]{"WINDOW_MINIMIZED"}));
            var failure=new CaptureFailureException(minimized,reasons);
            Require(CaptureDiagnostics.WaitForRestore(true,true,failure));
            Require(!CaptureDiagnostics.WaitForRestore(true,false,failure)&&!CaptureDiagnostics.WaitForRestore(false,true,failure));
            Require(ProblemCatalog.Identify(failure.Message).Code=="JX3-WINDOW-002"&&SupportReport.KnownCode("JX3-WINDOW-002"));
            Require(ProblemCatalog.Identify("游戏窗口身份已失效").Code=="JX3-WINDOW-001");
            var core=new AutomationController(0,0);
            core.Observe(Sample(entry,1000));
            core.ObservationUnavailable(WujieTarget.CaptureWindowMinimized);
            core.Observe(Sample(entry,2000));Require(!core.WantsStart&&core.State==Phase.ConfirmingEntry);
            core.Observe(Sample(entry,3000));Require(core.WantsStart);
        });
        test("foreground recovery accepts only post-switch entry frames and can observe an already restarted performance",()=>
        {
            var core=new AutomationController(0,0);
            core.Observe(Sample(entry,1000));
            core.ObservationUnavailable(FocusTransaction.ForegroundRequired);
            Require(!AutomationService.CanObserveDuringForegroundRecovery(Sample(entry,2000),null));
            Require(!AutomationService.CanObserveDuringForegroundRecovery(Sample(entry,2000),2000));
            Require(AutomationService.CanObserveDuringForegroundRecovery(Sample(entry,2100),2000));
            core.Observe(Sample(entry,2100));Require(!core.WantsStart);
            core.Observe(Sample(entry,2200));Require(core.WantsStart);
            Require(AutomationService.CanObserveDuringForegroundRecovery(Sample(stop,2300),null));
            core.Observe(Sample(stop,2300));core.Observe(Sample(stop,2400));
            Require(!core.WantsStart&&core.State==Phase.Performing&&core.Starts==0);
        });

        test("Wujie click shape is scoped to measured window and original safety checks",()=>
        {
            var valid=new Check(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED",
                "UNIQUE_CANDIDATE",1,2564,1487,3000,box,"UNKNOWN",WindowFrame:true,WujieWindow:true);
            Require(ClickPolicy.Refusal(valid)==null);
            foreach(var bad in new[]{valid with{WujieWindow=false},valid with{FrameWidth=2560},valid with{AgeMs=3001},
                valid with{Foreground=false},valid with{Identity=false},valid with{HitTarget=false},valid with{Geometry=false},
                valid with{ButtonHeld=true},valid with{Cancelled=true},valid with{CandidateCount=2},valid with{D2="VISIBLE"}})
                Require(ClickPolicy.Refusal(bad)!=null);
        });
        test("Wujie folded performance cannot grant start, and recovery requires two fresh entries",()=>
        {
            var core=new AutomationController(0,0);
            core.Observe(Sample(stop,1000));Require(!core.WantsStart&&core.State!=Phase.Performing);
            core.Observe(Sample(stop,2000));Require(!core.WantsStart&&core.State==Phase.Performing&&core.Countdown.EndSourceMs==null);
            core.Observe(Sample(unknown,3000));Require(!core.WantsStart&&core.State==Phase.WaitingVisibility);
            core.Observe(Sample(entry,4000));Require(!core.WantsStart);
            core.Observe(Sample(entry,4000));Require(!core.WantsStart);
            core.Observe(Sample(entry,5000));Require(!core.WantsStart);
            core.Observe(Sample(entry,6000));Require(core.WantsStart&&core.Returns==1);
        });
        test("Wujie post-input stop-button feedback requires released input and two new frames",()=>
        {
            var feedback=new StartFeedback(FeedbackFocus.Any);
            feedback.InputCompleted(2,1,1000);
            FeedbackSample Frame(double source)=>new(source,source+100,true,true,true,"NO_VERIFIED_CANDIDATE",0,"UNKNOWN",StopButtonVisible:true);
            feedback.Observe(Frame(1000));Require(feedback.Consecutive==0);
            feedback.Observe(Frame(1400));feedback.Observe(Frame(1400));Require(feedback.Consecutive==0);
            feedback.Observe(Frame(1700));feedback.Observe(Frame(1900));feedback.Complete(2100);
            Require(feedback.Done&&feedback.State==FeedbackState.StartFeedbackObserved);
            var noRelease=new StartFeedback();noRelease.InputCompleted(2,0,1000);
            noRelease.Observe(Frame(1700));Require(noRelease.State==FeedbackState.InputRejected);
        });
        test("Wujie start and stop evidence conflict stays closed",()=>
        {
            var sample=Sample(entry,1000) with{StopButtonVisible=true};
            Require(!EntryReadiness.Confirmed(sample));
            var core=new AutomationController(0,0);
            core.Observe(sample);core.Observe(sample with{Source=2000,Now=2100});
            Require(!core.WantsStart&&core.State==Phase.WaitingVisibility);
        });
        test("Wujie timer rejects pixels outside the panel region or without green text",()=>
        {
            var reader=new CountdownReader();
            var pixels=new byte[100*40*4];
            Require(reader.ReadWujieRegion(pixels,100,40,new(0,0,100,40),CancellationToken.None).Result.Reason=="wujie_green_text_missing");
            Require(reader.ReadWujieRegion(pixels,100,40,new(-1,0,99,40),CancellationToken.None).Result.Reason=="timer_out_of_frame");
        });
        test("Wujie single-minute parsing keeps the flagship contract and rejects OCR conflicts",()=>
        {
            Require(CountdownReader.Parse("3:14")==null);
            Require(CountdownReader.Parse("3：14",singleMinute:true)==194);
            Require(CountdownReader.Parse("3:60",singleMinute:true)==null);
            Require(CountdownReader.Combine(["3:14","3:15"],"test",singleMinute:true).Seconds==null);
        });
        test("Wujie established countdown keeps ticking through sustained numeric-only misses",()=>
        {
            var core=new AutomationController(0,0,retainCountdownOnNumericMiss:true);
            Observation Panel(double source,int? seconds)=>Sample(new("PERFORMING_EXPANDED",null,null,null),source)
                with{CountdownSeconds=seconds,CountdownQuality=seconds.HasValue?"DUAL_PASS_AGREEMENT":"UNKNOWN",Now=source+1500};
            core.Observe(Panel(1000,612));core.Observe(Panel(2200,611));
            var clock=core.Countdown;double end=clock.DisplayDeadline!.Value;
            Require(core.State==Phase.Performing&&core.NextObservationDelayMs(3700)==15000);
            Require(CountdownTracker.DisplaySeconds(end,3700)==609);
            for(int source=122000;source<600000;source+=15000)core.Observe(Panel(source,null));
            Require(clock.Estimated&&clock.DisplayDeadline==end&&!core.WantsStart);
            Require(CountdownTracker.DisplaySeconds(end,600000)==13);
            core.Observe(Panel(614000,null));
            Require(!clock.Estimated&&clock.DisplayDeadline==null&&!core.WantsStart);
        });
        test("Wujie manual restart reanchors after two readings and entrance clears the old clock",()=>
        {
            var core=new AutomationController(0,0,retainCountdownOnNumericMiss:true);
            Observation Panel(double source,int seconds)=>Sample(new("PERFORMING_EXPANDED",null,null,null),source)
                with{CountdownSeconds=seconds,CountdownQuality="DUAL_PASS_AGREEMENT"};
            core.Observe(Panel(1000,612));core.Observe(Panel(2000,611));
            core.Observe(Sample(stop,3000));Require(core.Countdown.Estimated&&core.Countdown.DisplayDeadline.HasValue);
            core.Observe(Panel(4000,1800));Require(core.Countdown.DisplayDeadline==null&&!core.WantsStart);
            core.Observe(Panel(5000,1799));Require(core.Countdown.Reanchors==1&&core.Countdown.DisplayDeadline.HasValue);
            core.Observe(Sample(entry,6000));Require(core.Countdown.DisplayDeadline==null&&!core.WantsStart);
            core.Observe(Sample(entry,7000));Require(core.WantsStart);
        });

        int replay=Array.IndexOf(args,"--wujie-replay");
        if(replay<0)return;
        test("Wujie packaged worker maps all eight native saved frames",()=>
        {
            var geometry=new WindowSnapshot(new(0,0,2564,1487),new(0,0,2564,1487),new(2,45,2562,1485),144,false,true);
            var map=new WindowFrameMap(geometry,geometry,2564,1487,2564,1487,2564L*1487*4);
            using var worker=new RecognizerWorker(Path.Combine(AppContext.BaseDirectory,"wujie-local","replay.py"),args[replay+2]);
            foreach(var folder in new[]{"entry","panel","entry-moved","panel-folded"})
            foreach(var name in new[]{"first","last"})
            {
                string path=Path.Combine(args[replay+1],folder,name+".bmp");
                var visual=ObservationEvidence.Read(worker.Read(path,map.ClientInFrame,null,CancellationToken.None),map);
                string expected=folder switch
                {
                    "entry" or "entry-moved"=>"ENTRY",
                    "panel"=>"PERFORMING_EXPANDED",
                    _ when name=="last"=>"PERFORMING_STOP_BUTTON",
                    _=>"UNKNOWN"
                };
                if(visual.State!=expected)throw new IOException($"{folder}/{name}: expected {expected}, got {visual.State}");
            }
        });
        if(!args.Contains("--wujie-timers"))return;
        var timerReader=new CountdownReader();
        foreach(var (name,expected,conflict) in new[]{("panel/first.bmp",1778,true),("panel/last.bmp",1769,false),("timer-integration/trial-cache.bmp",1749,false)})
        {
            test("Wujie OCR from packaged automatically located timer "+name,()=>
            {
                var geometry=new WindowSnapshot(new(0,0,2564,1487),new(0,0,2564,1487),new(2,45,2562,1485),144,false,true);
                var map=new WindowFrameMap(geometry,geometry,2564,1487,2564,1487,2564L*1487*4);
                string path=Path.Combine(args[replay+1],name);
                using var worker=new RecognizerWorker(Path.Combine(AppContext.BaseDirectory,"wujie-local","replay.py"),args[replay+2]);
                var visual=ObservationEvidence.Read(worker.Read(path,map.ClientInFrame,null,CancellationToken.None),map);
                if(visual.State!="PERFORMING_EXPANDED"||visual.Timer is not {} timer)
                    throw new IOException("automatic Wujie timer location unavailable");
                var result=ReadSavedTimer(timerReader,path,map.Width,map.Height,timer,expected);
                if(conflict)
                    Require(result.Seconds==null&&result.Reason=="wujie_digit_shape_conflict");
                else if(result.Seconds!=expected)throw new IOException($"expected {expected}, got {result.Seconds}; {result.Reason}");
            });
        }
        int five=Array.IndexOf(args,"--wujie-timer-five");
        if(five>=0)test("Wujie stroke guard preserves the known genuine 5",()=>
            Require(ReadSavedTimer(timerReader,args[five+1],2517,1394,new(1018,789,1117,825),1705).Seconds==1705));
        if(args.Contains("--wujie-short-clock"))
        {
            string clock=Path.Combine(args[replay+1],"clock-stage");
            var region=new Box(1055,868,1154,904);
            test("Wujie OCR recognizes a composed single-minute line using captured glyphs",()=>
                Require(ReadSavedTimer(timerReader,Path.Combine(clock,"composed-9-09.bmp"),2564,1487,region,549).Seconds==549));
            test("Wujie rejects a missing leading digit instead of treating it as a single-minute timer",()=>
                Require(ReadSavedTimer(timerReader,Path.Combine(clock,"removed-leading-digit.bmp"),2564,1487,region,1749).Seconds==null));
        }
    }

    static CountdownReading ReadSavedTimer(CountdownReader reader,string path,int width,int height,Box timer,int expected)
    {
        using var stream=File.OpenRead(path);
        var source=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        var bitmap=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        Require(bitmap.PixelWidth==width&&bitmap.PixelHeight==height);
        var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var result=reader.ReadWujieRegion(pixels,width,height,timer,CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"{Path.GetFileName(path)}: expected {expected}, got {result.Seconds}; {watch.ElapsedMilliseconds} ms; {result.Reason}; {result.Raw}");
        return result;
    }

    static void Require(bool value)
    {
        if(!value)throw new InvalidOperationException("Wujie safety assertion failed");
    }
}
