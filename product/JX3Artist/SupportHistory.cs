using System.IO;
using System.Text.Json;
namespace JX3Artist;
internal sealed record SupportStep(long Sequence,DateTime Utc,Guid Launch,int Run,string Version,string Kind,JsonElement Data,string? ProblemCode=null);
internal sealed record SupportIncident(Guid Id,string Code,DateTime Utc,SupportStep[] Before,SupportStep[] After,Dictionary<string,JsonElement> Context);
internal sealed record SupportContext(string Schema,SupportStep[] Recent,SupportIncident[] Incidents,Dictionary<string,JsonElement> Current,bool SavedSuccessfully,int SaveErrors);
// Own-app events only. Projection occurs before memory retention or disk writes.
internal sealed class SupportHistory:IDisposable
{
    const int MaxBytes=2*1024*1024;
    readonly object gate=new(),writeGate=new();readonly Queue<SupportStep> recent=new();readonly List<SupportIncident> incidents=new();readonly Dictionary<string,JsonElement> current=new();
    readonly string folder;readonly Guid launch=Guid.NewGuid();readonly string version=typeof(SupportHistory).Assembly.GetName().Version!.ToString();
    readonly AutoResetEvent changed=new(false);readonly Thread worker;long sequence,revision,savedRevision=-1;int run,errors;bool stopping,closed;string? incidentRun;
    internal int SaveErrors=>Volatile.Read(ref errors);
    internal SupportHistory(string dataRoot)
    {
        folder=Path.Combine(Path.GetFullPath(dataRoot),"support-context");
        try{Load();}catch{errors++;}
        Record("app_open",new{});worker=new Thread(Work){IsBackground=true,Name="JX3Artist-support-context"};worker.Start();
    }
    internal void Record(string kind,object data,string? problemCode=null)
    {
        try{
            if(!SupportReport.Sanitize(kind,JsonSerializer.SerializeToElement(data),out var safe))return;
            lock(gate){if(stopping)return;
                if(kind=="observation_context"&&current.TryGetValue(kind,out var old)&&old.GetRawText()==safe.GetRawText())return;
                bool accepted=kind=="start_requested"&&safe.TryGetProperty("accepted",out var allowed)&&allowed.ValueKind==JsonValueKind.True;if(accepted)run++;
                var step=new SupportStep(++sequence,DateTime.UtcNow,launch,run,version,kind,safe,SupportReport.KnownCode(problemCode)?problemCode:null);
                foreach(int i in Enumerable.Range(0,incidents.Count).ToArray()){var incident=incidents[i];if(incident.After.Length<20)incidents[i]=incident with{After=incident.After.Append(step).ToArray()};}
                recent.Enqueue(step);while(recent.Count>256)recent.Dequeue();
                if(accepted||kind is "observation_context" or "mode_changed" or "resource_sample" or "settings_changed" or "experience_status")current[kind]=safe;
                if(SupportReport.KnownCode(problemCode)&&incidentRun!=$"{launch}/{run}"){
                    incidentRun=$"{launch}/{run}";incidents.Add(new(Guid.NewGuid(),problemCode!,step.Utc,recent.TakeLast(80).ToArray(),Array.Empty<SupportStep>(),new(current)));while(incidents.Count>3)incidents.RemoveAt(0);
                }
                revision++;
            }
            if(SupportReport.KnownCode(problemCode))changed.Set();
        }catch{Interlocked.Increment(ref errors);}
    }
    internal SupportContext Snapshot(){lock(gate)return new("jx3-context-v1",recent.ToArray(),incidents.Select(i=>i with{Before=i.Before.ToArray(),After=i.After.ToArray(),Context=new(i.Context)}).ToArray(),new(current),savedRevision==revision,Volatile.Read(ref errors));}
    static SupportStep? ReviewStep(SupportStep? step)
    {if(step==null||step.Sequence<0||step.Run<0||!Version.TryParse(step.Version,out _)||!SupportReport.Sanitize(step.Kind,step.Data,out var safe))return null;return step with{Data=safe,ProblemCode=SupportReport.KnownCode(step.ProblemCode)?step.ProblemCode:null};}
    static Dictionary<string,JsonElement> ReviewConditions(Dictionary<string,JsonElement>? source)
    {
        var result=new Dictionary<string,JsonElement>();foreach(var pair in source??new())if(pair.Key is "observation_context" or "start_requested" or "mode_changed" or "resource_sample" or "settings_changed" or "experience_status"&&SupportReport.Sanitize(pair.Key,pair.Value,out var safe))result[pair.Key]=safe;return result;
    }
    internal static SupportContext Review(SupportContext source)
    {
        if(source.Schema!="jx3-context-v1")return new("jx3-context-v1",Array.Empty<SupportStep>(),Array.Empty<SupportIncident>(),new(),false,1);
        var safeIncidents=new List<SupportIncident>();foreach(var item in (source.Incidents??Array.Empty<SupportIncident>()).TakeLast(3)){
            if(item==null||!SupportReport.KnownCode(item.Code))continue;
            safeIncidents.Add(item with{Before=(item.Before??Array.Empty<SupportStep>()).TakeLast(80).Select(ReviewStep).Where(x=>x!=null).Cast<SupportStep>().ToArray(),After=(item.After??Array.Empty<SupportStep>()).Take(20).Select(ReviewStep).Where(x=>x!=null).Cast<SupportStep>().ToArray(),Context=ReviewConditions(item.Context)});
        }
        return source with{Recent=(source.Recent??Array.Empty<SupportStep>()).TakeLast(256).Select(ReviewStep).Where(x=>x!=null).Cast<SupportStep>().ToArray(),Incidents=safeIncidents.ToArray(),Current=ReviewConditions(source.Current),SaveErrors=Math.Clamp(source.SaveErrors,0,1000000)};
    }
    static bool Link(string path)=>Directory.Exists(path)||File.Exists(path)?(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0:false;
    void EnsureOwned()
    {
        if(Link(Path.GetDirectoryName(folder)!)||Link(folder))throw new IOException("context path refused");
        string owner=Path.Combine(folder,".owner");if(Link(owner))throw new IOException("context owner refused");
        if(File.Exists(owner)){if(new FileInfo(owner).Length>128||File.ReadAllText(owner)!="jx3-support-context-v1")throw new IOException("context ownership mismatch");}
        else{if(Directory.Exists(folder)&&Directory.EnumerateFileSystemEntries(folder).Any())throw new IOException("context directory occupied");Directory.CreateDirectory(folder);File.WriteAllText(owner,"jx3-support-context-v1");}
        foreach(string temporary in Directory.EnumerateFiles(folder,"snapshot-*.tmp").Take(16)){
            string name=Path.GetFileName(temporary);if(name.Length==45&&Guid.TryParseExact(name.Substring(9,32),"N",out _)&&!Link(temporary)&&File.GetLastWriteTimeUtc(temporary)<DateTime.UtcNow.AddMinutes(-10))File.Delete(temporary);
        }
    }
    void Load()
    {
        if(!Directory.Exists(folder))return;EnsureOwned();string file=Path.Combine(folder,"snapshot.json");if(!File.Exists(file))return;
        if(Link(file)||new FileInfo(file).Length>MaxBytes)throw new IOException("invalid context snapshot");
        var snapshot=JsonSerializer.Deserialize<SupportContext>(File.ReadAllText(file));if(snapshot?.Schema!="jx3-context-v1")throw new IOException("invalid context schema");snapshot=Review(snapshot);
        errors=Math.Max(errors,Math.Clamp(snapshot.SaveErrors,0,1000000));
        foreach(var step in snapshot.Recent){recent.Enqueue(step);sequence=Math.Max(sequence,step.Sequence);}incidents.AddRange(snapshot.Incidents);
        // Current conditions must come from this launch, not a previous app run.
    }
    internal void Flush()
    {
        lock(writeGate){long at;SupportContext snapshot;lock(gate){at=revision;if(at==savedRevision)return;snapshot=Snapshot();}
            string? temporary=null;
            try{EnsureOwned();string text=JsonSerializer.Serialize(snapshot with{SavedSuccessfully=true});if(System.Text.Encoding.UTF8.GetByteCount(text)>MaxBytes)throw new IOException("context budget exceeded");
                temporary=Path.Combine(folder,"snapshot-"+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(temporary,text);File.Move(temporary,Path.Combine(folder,"snapshot.json"),true);lock(gate)savedRevision=at;
            }catch{Interlocked.Increment(ref errors);}finally{if(temporary!=null&&File.Exists(temporary))try{File.Delete(temporary);}catch{Interlocked.Increment(ref errors);}}
        }
    }
    void Work(){while(true){changed.WaitOne(1000);Flush();lock(gate)if(stopping)return;}}
    public void Dispose(){lock(gate){if(closed)return;closed=true;stopping=true;}changed.Set();if(worker.Join(1500))changed.Dispose();}
}
