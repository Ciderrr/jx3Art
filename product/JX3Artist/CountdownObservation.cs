using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Int1;
namespace JX3Artist;

internal static class CountdownObservation
{
    [DllImport("user32.dll",SetLastError=true)]static extern nint SetThreadDpiAwarenessContext(nint context);
    internal static int Run(string[] args)
    {
        string Value(string name,string fallback)=>Array.IndexOf(args,name) is int i && i>=0 && i+1<args.Length?args[i+1]:fallback;
        string output=Path.GetFullPath(Value("--out","readonly_countdown_"+Guid.NewGuid().ToString("N")));
        int seconds=int.Parse(Value("--seconds","150"));if(seconds is <10 or >300)throw new ArgumentOutOfRangeException("seconds");
        string mode=Value("--mode","adaptive");if(mode is not ("adaptive" or "rapid"))throw new ArgumentException("unknown observation mode");
        Directory.CreateDirectory(output);using var stop=new CancellationTokenSource();
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
        int count=0,confirmed=0;double? maxAge=null;var clock=Stopwatch.StartNew();var rows=new List<object>();
        using var journal=new StreamWriter(new FileStream(Path.Combine(output,"events.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        void Log(string kind,object data)=>journal.WriteLine(JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,kind,data}));
        nint priorDpi=0;
        try
        {
            // Changes only this observer's DPI context, never the target window.
            Win32RO.SetProcessDpiAwarenessContext(Win32RO.PMv2);
            priorDpi=SetThreadDpiAwarenessContext(Win32RO.PMv2);
            var core=new AutomationController(0,FrameTiming.NowMs());
            using var session=new GameSession(Path.Combine(AppContext.BaseDirectory,"engine"),Path.Combine(output,"cache"));
            File.WriteAllText(Path.Combine(output,"started.json"),JsonSerializer.Serialize(new{observerPid=Environment.ProcessId,targetPid=session.Pid,seconds,mode,readOnly=true,actionsDispatched=0}));
            Log("started",new{observerPid=Environment.ProcessId,targetPid=session.Pid,seconds,mode,readOnly=true});
            Console.WriteLine($"Read-only countdown observation started; mode={mode}; bound={seconds}s; no input dispatch.");
            double deadline=FrameTiming.NowMs()+seconds*1000d;
            count=ReadOnlyCountdownLoop.Run(core,deadline,FrameTiming.NowMs,()=>session.Capture(stop.Token).Observation,
                ms=>{if(stop.Token.WaitHandle.WaitOne(ms))throw new OperationCanceledException();},
                (index,o,c)=>
                {
                    count=index;
                    double age=o.Now-o.Source;maxAge=Math.Max(maxAge??0,age);if(c.Countdown.EndSourceMs.HasValue)confirmed++;
                    string? crop=null;
                    if(o.ImagePath!=null)
                    {
                        if(index==1)File.Copy(o.ImagePath,Path.Combine(output,"first.bmp"),false);
                        // Small timer crops are independent visual ground truth, not full-frame history.
                        if(o.PanelLocated)
                        {
                            using var input=File.OpenRead(o.ImagePath);var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                            var source=new CroppedBitmap(decoder.Frames[0],new Int32Rect(1898+o.PanelDx,1455+o.PanelDy,102,50));
                            crop=$"timer_{index:000}.png";var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var target=File.Create(Path.Combine(output,crop));encoder.Save(target);
                        }
                    }
                    using var self=Process.GetCurrentProcess();self.Refresh();
                    var row=new{index,o.Source,o.Now,ageMs=age,o.Identity,o.Geometry,o.Location,o.Count,o.D2,o.PanelLocated,o.PanelDx,o.PanelDy,o.PanelLocation,o.CountdownSeconds,o.CountdownQuality,o.CountdownReason,
                        state=c.State.ToString(),c.Message,c.WantsStart,c.Countdown.Status,c.Countdown.EndSourceMs,c.Countdown.Reanchors,
                        nextDelayMs=c.NextObservationDelayMs(FrameTiming.NowMs()),crop,observerWorkingSetMiB=self.WorkingSet64/1048576d,observerPrivateMiB=self.PrivateMemorySize64/1048576d};
                    rows.Add(row);Log("sample",row);Console.WriteLine($"sample={index} seconds={o.CountdownSeconds} state={c.State} age={age:0}ms delay={c.NextObservationDelayMs(FrameTiming.NowMs())}ms");
                },stop.Token,mode=="rapid"?2000:null);
            bool passed=count>=2 && confirmed>0 && !core.Done;
            var result=new{scope="actual GameSession/reader/controller with read-only loop; no GUI/input/focus operations",passed,seconds,mode,count,confirmed,maxSourceAgeMs=maxAge,elapsedMs=clock.ElapsedMilliseconds,state=core.State.ToString(),core.Message,actionsDispatched=0,rows};
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Log("finished",new{passed,count,confirmed,actionsDispatched=0});return passed?0:9;
        }
        catch(Exception ex)
        {
            Log("failed",new{error=ex.Message,actionsDispatched=0});
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,count,confirmed,error=ex.ToString(),elapsedMs=clock.ElapsedMilliseconds,actionsDispatched=0},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(ex.Message);return 9;
        }
        finally{if(priorDpi!=0)SetThreadDpiAwarenessContext(priorDpi);Console.CancelKeyPress-=cancel;}
    }
}
