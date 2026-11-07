using System.IO;
using System.Text.Json;
using Int1;
namespace JX3Artist;
// Explicit, bounded read-only runner. No service, focus transaction or input API.
internal static class WindowObservation
{
    internal static int Run(string[] args)
    {
        string Value(string key,string fallback){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        int seconds=int.Parse(Value("--seconds","20"));if(seconds is <10 or >60)throw new ArgumentOutOfRangeException("seconds");
        string output=Path.GetFullPath(Value("--out","window_observation_"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(output);
        using var stop=new CancellationTokenSource();ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
        var rows=new List<object>();int confirmed=0;string? error=null;
        using var log=new StreamWriter(new FileStream(Path.Combine(output,"samples.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        try
        {
            using var session=new GameSession(Path.Combine(AppContext.BaseDirectory,"engine"),Path.Combine(output,"cache"));
            double deadline=FrameTiming.NowMs()+seconds*1000;
            while(FrameTiming.NowMs()<deadline && !stop.IsCancellationRequested)
            {
                double started=FrameTiming.NowMs();var picture=session.Capture(stop.Token);var o=picture.Observation;
                bool recognized=Recognized(o);
                if(recognized)confirmed++;
                if(rows.Count==0)File.Copy(picture.Path,Path.Combine(output,"first.bmp"));
                var row=Sample(picture,rows.Count+1,FrameTiming.NowMs()-started,session.TimerEvidencePath,session.TimerEvidenceError,session.EntryEvidencePath,session.EntryEvidenceError);
                rows.Add(row);log.WriteLine(JsonSerializer.Serialize(row));
                int delay=ObservationSchedule.Plan(started,FrameTiming.NowMs(),2000).WaitMs;if(stop.Token.WaitHandle.WaitOne(delay))break;
            }
        }
        catch(Exception ex){error=ex.ToString();}
        finally{Console.CancelKeyPress-=cancel;}
        bool passed=error==null&&!stop.IsCancellationRequested&&confirmed>=2;
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed,scope="actual GameSession capture and recognizer; read only, no input or focus operations",seconds,samples=rows.Count,confirmed,error,cancelled=stop.IsCancellationRequested,actionsDispatched=0,rows},new JsonSerializerOptions{WriteIndented=true}));
        return passed?0:9;
    }
    internal static bool Recognized(Observation o)=>o.Identity&&o.Geometry&&double.IsFinite(o.Source)&&o.Now>=o.Source&&o.Now-o.Source<=Jx3.OneClickTest.ClickPolicy.MaxFrameAgeMs&&(EntryReadiness.Confirmed(o)||o.D2=="VISIBLE"&&o.PanelLocated);
    internal static object Sample(Picture picture,int index,double processingMs,string? timerPath=null,string? timerError=null,string? entryPath=null,string? entryError=null)
    {
        var o=picture.Observation;
        return new{index,geometry=picture.Map.Snapshot,frameWidth=picture.Map.Width,frameHeight=picture.Map.Height,clientInFrame=picture.Map.ClientInFrame,o.Location,o.Count,o.EntryContextConfirmed,o.D2,picture.UiScale,o.PanelLocation,o.CountdownSeconds,o.CountdownQuality,o.CountdownReason,o.Geometry,recognized=Recognized(o),sourceAgeMs=double.IsFinite(o.Now-o.Source)?(double?)(o.Now-o.Source):null,processingMs,actionsDispatched=0,
            Diagnostics=ObservationDiagnostics.Create(picture.Metrics,timerPath,timerError,entryPath,entryError)};
    }
}
