using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Int1;
using Jx3.OneClickTest;
namespace JX3Artist;

internal sealed record CaptureMetrics(double PullMapMs,double TransportMs,double RecognitionMs,double ValidationOcrEvidenceMs,double TotalMs,string? RecognizerScheduling=null,string? RecoveryStatus=null,int? RecoveryAttempts=null,double? OriginalPanelScale=null,double? SelectedPanelScale=null,PanelVerification? PanelVerification=null,SmallPanelEvidence? SmallPanelEvidence=null);
internal sealed record Picture(Observation Observation,Box? Icon,Box Bounds,string Path,WindowFrameMap Map,double UiScale=1,CaptureMetrics? Metrics=null,EntryPixelProof? EntryProof=null);
internal sealed class GameSession:IDisposable
{
    readonly WgcEngine engine=new();readonly BitmapFrameTransport transport;readonly string script;readonly DateTime processStarted;int frameId;
    readonly CountdownReader countdown=new();readonly UiScaleHint scaleHint=new();
    readonly RecognizerWorker recognizer;
    readonly BitmapFrameTransport localTransport;int localFrameId;
    readonly EntryEvidenceStore entryEvidence;readonly TimerEvidenceStore timerEvidence;
    internal string? TimerEvidencePath{get;private set;}internal string? TimerEvidenceError{get;private set;}
    internal string? EntryEvidencePath{get;private set;}internal string? EntryEvidenceError{get;private set;}
    internal nint Window{get;}internal uint Pid{get;}
    internal bool Identity
    {
        get
        {
            if(!Win32RO.IsWindow(Window))return false;Win32RO.GetWindowThreadProcessId(Window,out uint pid);if(pid!=Pid)return false;
            try{using var process=Process.GetProcessById((int)Pid);return process.ProcessName.Equals("JX3ClientX64",StringComparison.OrdinalIgnoreCase) && process.StartTime.ToUniversalTime()==processStarted;}catch{return false;}
        }
    }
    internal GameSession(string engineRoot,string cache)
    {
        if(!Win32RO.AreDpiAwarenessContextsEqual(Win32RO.GetThreadDpiAwarenessContext(),Win32RO.PMv2))throw new IOException("DPI物理坐标上下文未确认，已停止准备输入");
        uint expectedPid;
        var processes=Process.GetProcessesByName("JX3ClientX64");try{if(processes.Length!=1)throw new InvalidOperationException("需要且只支持一个已打开的剑网3客户端");expectedPid=(uint)processes[0].Id;}finally{foreach(var p in processes)p.Dispose();}
        var (window,pid,error)=Win32RO.LocateGame(expectedPid);if(window==0)throw new IOException("未找到游戏："+error);
        using var process=Process.GetProcessById((int)pid);
        if(!string.Equals(process.ProcessName,"JX3ClientX64",StringComparison.OrdinalIgnoreCase))throw new IOException("目标不是游戏客户端");
        processStarted=process.StartTime.ToUniversalTime();
        Window=window;Pid=pid;EngineBundle.Validate(engineRoot);script=Path.Combine(engineRoot,"diagnostics/one_click/locate.py");recognizer=new(script);
        Cache.Prepare(cache);timerEvidence=new(Path.Combine(cache,"timer-evidence"));entryEvidence=new(Path.Combine(cache,"entry-evidence"));transport=new(cache);localTransport=new(cache);string? start=engine.Start(Window);if(start!=null){engine.Dispose();throw new IOException(start);}
    }
    internal Picture Capture(CancellationToken token,double sourceAfter=double.NegativeInfinity)
    {
        double captureStarted=FrameTiming.NowMs();
        token.ThrowIfCancellationRequested();
        CaptureDiagnostics.Ensure(new("before_pull",Identity,Win32RO.IsIconic(Window)));
        var before=WindowGeometryReader.Read(Window);var bounds=before.Visible;var frame=engine.PullLatest(500);
        double freshDeadline=FrameTiming.NowMs()+500;
        while(frame.Ok && frame.FrameQpcMs<=sourceAfter && FrameTiming.NowMs()<freshDeadline)
        {if(token.WaitHandle.WaitOne(20))throw new OperationCanceledException(token);frame=engine.PullLatest(50);}
        bool identity=Identity;WindowSnapshot? afterSnapshot=null;Box? after=null;string? boundsError=null;
        if(identity)try{afterSnapshot=WindowGeometryReader.Read(Window);after=afterSnapshot.Visible;}catch(Exception ex){boundsError=ex.GetType().Name+": "+ex.Message;}
        var evidence=new CaptureEvidence("after_pull",identity,Win32RO.IsIconic(Window),bounds,after,frame.Ok,frame.RecreatedThisPull,
            frame.SurfaceW,frame.SurfaceH,frame.ContentW,frame.ContentH,frame.Error,boundsError,double.IsFinite(frame.FrameQpcMs)?frame.FrameQpcMs:null,before,afterSnapshot,WindowFrame:true);
        CaptureDiagnostics.Ensure(evidence);
        WindowFrameMap map;
        try{map=new(before,afterSnapshot!,frame.SurfaceW,frame.SurfaceH,frame.ContentW,frame.ContentH,frame.Bgra.LongLength);}
        catch(IOException ex){throw new CaptureFailureException(evidence with{BoundsError=ex.Message},new[]{"PIXEL_MAP_NOT_VERIFIED"});}
        double mapCompleted=FrameTiming.NowMs();
        string path;using(var lease=transport.Stage(frame.Bgra,map.Width,map.Height,++frameId))path=lease.KeepLatest();
        double transportCompleted=FrameTiming.NowMs();
        string text=recognizer.Read(path,map.ClientInFrame,scaleHint.For(before),token);
        double recognitionCompleted=FrameTiming.NowMs();
        {
            var timerBox=WindowFramePolicy.Validate(text,map);var numericRegion=timerBox??WindowFramePolicy.SearchRegion(text,map);
            var observation=ReadObservation(text,frame.FrameQpcMs,FrameTiming.NowMs(),Identity,Jx3.OneClickTest.Native.Bounds(Window)==bounds,path,out var icon);
            double uiScale=WindowFramePolicy.ReadScale(text);scaleHint.Observe(before,uiScale,EntryReadiness.Confirmed(observation)||observation.D2=="VISIBLE"&&observation.PanelLocated);
            if(observation.Location=="UNIQUE_CANDIDATE" && observation.Count==1 && !EntryReadiness.Confirmed(observation) && icon!=null && EntryEvidencePath==null && EntryEvidenceError==null)
            {
                try{EntryEvidencePath=entryEvidence.Record(frame.Bgra,map.Width,map.Height,EntryEvidenceStore.CropFor(icon,map.Width,map.Height,uiScale),text,frame.FrameQpcMs);}
                catch(Exception ex){EntryEvidenceError=ex.GetType().Name+": "+ex.Message;}
            }
            string timerRaw="";bool usedSemanticSearch=timerBox==null;TimerEvidencePath=TimerEvidenceError=null;
            if(CountdownReader.Eligible(observation) && numericRegion!=null)
            {
                var reading=countdown.ReadRegion(frame.Bgra,map.Width,map.Height,numericRegion!,token,search:timerBox==null).GetAwaiter().GetResult();
                if(timerBox!=null && reading.Reason=="ocr_insufficient_valid_passes")
                {var searchRegion=WindowFramePolicy.SearchRegion(text,map);if(searchRegion!=null && searchRegion!=timerBox){usedSemanticSearch=true;reading=CountdownReader.Merge(reading,countdown.ReadRegion(frame.Bgra,map.Width,map.Height,searchRegion,token,search:true).GetAwaiter().GetResult());}}
                timerRaw=reading.Raw;observation=observation with{CountdownSeconds=reading.Seconds,CountdownQuality=reading.Quality,CountdownReason=(usedSemanticSearch?"semantic_search/":"")+reading.Reason};
            }
            if(CountdownReader.Eligible(observation) && numericRegion==null)observation=observation with{CountdownReason="timer_text_region_unresolved"};
            if(observation.PanelLocated && observation.D2!="VISIBLE")observation=observation with{CountdownReason="panel_confirmation_unknown"};
            if(!observation.PanelLocated && observation.PanelLocation=="UNRESOLVED_PANEL_CANDIDATE")observation=observation with{CountdownReason="panel_location_unresolved"};
            if(observation.CountdownSeconds==null)
            {
                try{
                    System.Windows.Int32Rect? diagnostic=WindowFramePolicy.DiagnosticRegion(text,map);
                    if(observation.PanelLocated){using var payload=JsonDocument.Parse(text);var values=payload.RootElement.GetProperty("d2_location").GetProperty("text_search_region").EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(values.Length!=4)throw new IOException("invalid diagnostic region");diagnostic=new(values[0],values[1],values[2]-values[0],values[3]-values[1]);}
                    if(diagnostic.HasValue)TimerEvidencePath=timerEvidence.Record(frame.Bgra,map.Width,map.Height,diagnostic.Value,observation.CountdownReason,text,timerRaw,frame.FrameQpcMs);
                }
                catch(Exception ex){TimerEvidenceError=ex.GetType().Name+": "+ex.Message;}
            }
            observation=observation with{Now=FrameTiming.NowMs(),Identity=Identity,Geometry=map.Matches(WindowGeometryReader.Read(Window))};
            EntryPixelProof? entryProof=null;if(icon!=null&&EntryReadiness.Confirmed(observation))try{entryProof=EntryPixelProof.Create(frame.Bgra,map.Width,map.Height,icon,uiScale);}catch(InvalidDataException){}
            var recovery=WindowFramePolicy.Recovery(text);
            var verification=WindowFramePolicy.Verification(text);
            var smallEvidence=WindowFramePolicy.SmallEvidence(text);
            double completed=FrameTiming.NowMs();return new(observation,icon,bounds,path,map,uiScale,new(mapCompleted-captureStarted,transportCompleted-mapCompleted,recognitionCompleted-transportCompleted,completed-recognitionCompleted,completed-captureStarted,recognizer.Scheduling,recovery.Status,recovery.Attempts,recovery.Original,recovery.Selected,verification,smallEvidence),entryProof);
        }
    }
    internal static Observation ReadObservation(string text,double source,double now,bool identity,bool geometry,string path,out Box? icon)
    {
        using var json=JsonDocument.Parse(text);var location=json.RootElement.GetProperty("location");var candidates=location.GetProperty("candidates");icon=null;
        if(candidates.GetArrayLength()==1){var b=candidates[0].GetProperty("bbox").EnumerateArray().Select(v=>v.GetInt32()).ToArray();if(b.Length!=4)throw new IOException("定位结果无效");icon=new(b[0],b[1],b[2],b[3]);}
        bool context=json.RootElement.TryGetProperty("entry_context",out var evidence) && evidence.TryGetProperty("confirmed",out var confirmed) && confirmed.ValueKind==JsonValueKind.True;
        bool located=false;int dx=0,dy=0;string panelStatus="NOT_CHECKED";
        string d2=json.RootElement.GetProperty("d2").GetProperty("state").GetString()!;
        if(json.RootElement.TryGetProperty("d2_location",out var panel))
        {
            panelStatus=panel.GetProperty("status").GetString()??"NOT_CHECKED";
            located=panelStatus=="UNIQUE_PANEL" && panel.TryGetProperty("verified",out var v) && v.ValueKind==JsonValueKind.True;
            if(located){var offset=panel.GetProperty("timer_offset").EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(offset.Length!=2)throw new IOException("面板定位偏移无效");dx=offset[0];dy=offset[1];}
            if(d2=="VISIBLE" && !located)d2="NOT_EVALUABLE";
        }
        else d2="NOT_EVALUABLE"; // Mixed/stale engine bundle must not authorize a click.
        return new(source,now,identity,geometry,location.GetProperty("status").GetString()!,candidates.GetArrayLength(),d2,path,context,PanelLocated:located,PanelDx:dx,PanelDy:dy,PanelLocation:panelStatus);
    }
    internal FastEntryCheck VerifyPreparedEntry(Picture seed,double after,CancellationToken token)
    {
        double started=FrameTiming.NowMs();
        FastEntryCheck Result(bool ok,double source,string reason)=>new(ok,source,FrameTiming.NowMs()-started,reason);
        if(seed.EntryProof==null||!Identity)return Result(false,0,"prepared_entry_unavailable");
        var before=WindowGeometryReader.Read(Window);if(!seed.Map.Matches(before))return Result(false,0,"geometry_changed");
        var frame=engine.PullLatest(50);double deadline=started+180;
        while(frame.Ok&&frame.FrameQpcMs<=after&&FrameTiming.NowMs()<deadline){if(token.WaitHandle.WaitOne(10))throw new OperationCanceledException(token);frame=engine.PullLatest(20);}
        token.ThrowIfCancellationRequested();double now=FrameTiming.NowMs();
        if(!frame.Ok||frame.RecreatedThisPull||!double.IsFinite(frame.FrameQpcMs)||frame.FrameQpcMs<=after||now-frame.FrameQpcMs>500||frame.FrameQpcMs>now)return Result(false,frame.FrameQpcMs,"fresh_pixel_frame_unavailable");
        var current=WindowGeometryReader.Read(Window);
        if(!Identity||!seed.Map.Matches(current)||frame.SurfaceW!=seed.Map.Width||frame.SurfaceH!=seed.Map.Height||frame.ContentW!=frame.SurfaceW||frame.ContentH!=frame.SurfaceH)return Result(false,frame.FrameQpcMs,"geometry_or_identity_changed");
        var region=seed.EntryProof.Region;var local=new byte[region.Width*region.Height*4];
        for(int row=0;row<region.Height;row++)Buffer.BlockCopy(frame.Bgra,((region.Y+row)*frame.SurfaceW+region.X)*4,local,row*region.Width*4,region.Width*4);
        string path;using(var staged=localTransport.Stage(local,region.Width,region.Height,++localFrameId))path=staged.KeepLatest();
        var expected=new Box(seed.Icon!.X-region.X,seed.Icon.Y-region.Y,seed.Icon.X2-region.X,seed.Icon.Y2-region.Y);
        string result;try{result=recognizer.Read(path,expected,seed.UiScale,token,entryPatch:true);}catch(TimeoutException){return Result(false,frame.FrameQpcMs,"local_entry_timeout");}
        using var response=JsonDocument.Parse(result);
        bool matched=response.RootElement.GetProperty("confirmed").GetBoolean();bool fresh=FrameTiming.NowMs()-frame.FrameQpcMs<=500;
        return Result(matched&&fresh,frame.FrameQpcMs,!fresh?"local_frame_expired":response.RootElement.GetProperty("reason").GetString()??"local_entry_unconfirmed");
    }
    public void Dispose(){try{recognizer.Dispose();}finally{engine.Dispose();}}
}
