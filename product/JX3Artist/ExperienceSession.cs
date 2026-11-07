using System.IO;
using System.Text.Json;
using Int1;
namespace JX3Artist;

internal sealed record ExperienceEntry(Guid Id,DateTime StartedUtc,DateTime? EndedUtc,bool Complete,long Observations,ExperienceEstimateView Estimate);
internal sealed record ExperienceDocument(string Schema,ExperienceEntry Current,ExperienceEntry[] History,long? LifetimeBeforeCurrent=null,bool LifetimePartial=false,long? LifetimeResetOffset=null);
// Whole-app owner. All game evidence is supplied by the existing observer;
// saving is a single-slot background queue and never part of input permission.
internal sealed class ExperienceSession:IDisposable
{
    const string Schema="jx3-experience-estimate-v3",PreviousSchema="jx3-experience-estimate-v2",LegacySchema="jx3-experience-estimate-v1",Marker="JX3ArtistExperienceV1";
    readonly object gate=new(),writeGate=new();readonly ExperienceEstimate estimate;readonly Guid id=Guid.NewGuid();readonly DateTime started=DateTime.UtcNow;
    readonly string folder;readonly ExperienceEntry[] history;readonly bool memoryOnly;readonly AutoResetEvent changed=new(false);
    Thread? writer;ExperienceDocument? pending;bool closed,stopping,cleanupBlocked;long observations,revision,savedRevision;int saveErrors;string? error;
    Observation? pendingPanel;long pendingGap;double? lastTimerEnd;double lastPanelSource=double.NegativeInfinity;int lastRound=-1,lastReanchors=-1;
    long? lifetimeBeforeCurrent;long lifetimeResetOffset;bool lifetimePartial;
    internal long OpenedAt{get;}
    internal ExperienceSession(string root,bool memoryOnly=false,Func<long>? clock=null)
    {
        this.memoryOnly=memoryOnly;OpenedAt=(clock??(()=>checked((long)Math.Floor(FrameTiming.NowMs()))))();estimate=new(OpenedAt);
        folder=Path.Combine(Path.GetFullPath(root),"experience");history=Array.Empty<ExperienceEntry>();
        if(memoryOnly){lifetimeBeforeCurrent=0;return;}
        try
        {
            Directory.CreateDirectory(folder);string marker=Path.Combine(folder,".owner");
            if(!File.Exists(marker)){if(Directory.EnumerateFileSystemEntries(folder).Any())throw new IOException("unowned");using var output=new StreamWriter(new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None));output.Write(Marker);}
            if(File.ReadAllText(marker)!=Marker)throw new IOException("unowned");
            string path=Path.Combine(folder,"session.json");
            if(File.Exists(path))
            {
                using var input=File.OpenRead(path);if(input.Length>65536)throw new InvalidDataException("budget");
                using var json=JsonDocument.Parse(input,new JsonDocumentOptions{MaxDepth=16});
                var previous=json.RootElement.Deserialize<ExperienceDocument>(new JsonSerializerOptions{RespectRequiredConstructorParameters=true,MaxDepth=16})??throw new InvalidDataException("empty");
                if(previous.Schema is Schema or PreviousSchema&&(!json.RootElement.TryGetProperty("LifetimeBeforeCurrent",out _)||!json.RootElement.TryGetProperty("LifetimePartial",out _)))throw new InvalidDataException("lifetime_missing");
                if(previous.Schema==Schema&&!json.RootElement.TryGetProperty("LifetimeResetOffset",out _))throw new InvalidDataException("reset_offset_missing");
                Validate(previous);
                if(previous.Schema==LegacySchema)
                {
                    long total=0;foreach(var entry in new[]{previous.Current}.Concat(previous.History))total=checked(total+entry.Estimate.EstimatedGain);
                    lifetimeBeforeCurrent=total;lifetimePartial=previous.History.Length==10||new[]{previous.Current}.Concat(previous.History).Any(x=>!x.Complete);
                    string backup=Path.Combine(folder,"legacy-"+previous.Current.Id.ToString("N")+".json");
                    input.Position=0;
                    if(!File.Exists(backup)){using var copy=new FileStream(backup,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(copy);}
                    else{using var copy=File.OpenRead(backup);if(copy.Length!=input.Length||!System.Security.Cryptography.SHA256.HashData(copy).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(input)))throw new InvalidDataException("legacy_backup_conflict");lifetimePartial=true;}
                }
                else
                {
                    if(previous.Schema==PreviousSchema){string backup=Path.Combine(folder,"v2-"+previous.Current.Id.ToString("N")+".json");input.Position=0;if(!File.Exists(backup)){using var copy=new FileStream(backup,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(copy);}else{using var copy=File.OpenRead(backup);if(copy.Length!=input.Length||!System.Security.Cryptography.SHA256.HashData(copy).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(input)))throw new InvalidDataException("v2_backup_conflict");}}
                    long offset=previous.Schema==Schema?previous.LifetimeResetOffset!.Value:0;
                    lifetimeBeforeCurrent=previous.LifetimeBeforeCurrent.HasValue?checked(previous.LifetimeBeforeCurrent.Value+(previous.Current.Estimate.EstimatedGain-offset)):null;
                    lifetimePartial=previous.LifetimePartial||!previous.Current.Complete;
                }
                history=new[]{previous.Current}.Concat(previous.History).Take(10).ToArray();
            }
            else lifetimeBeforeCurrent=0;
            writer=new Thread(WriteLoop){IsBackground=true,Name="JX3Artist-experience"};writer.Start();Queue();
        }
        catch{if(writer?.IsAlive!=true)writer=null;saveErrors++;error="经验记录无法保存；原文件已保留";}
    }
    internal ExperienceEstimateView Read()=>estimate.Read();
    internal long? LifetimeGain{get{lock(gate){return Total(estimate.Read());}}}
    internal bool LifetimePartial{get{lock(gate)return lifetimePartial||!Total(estimate.Read()).HasValue;}}
    long? Total(ExperienceEstimateView current)
    {if(!lifetimeBeforeCurrent.HasValue)return null;try{return checked(lifetimeBeforeCurrent.Value+(current.EstimatedGain-lifetimeResetOffset));}catch(OverflowException){return null;}}
    internal void ResetLifetime()
    {
        // Serialize with the writer before taking the statistics lock. A pending
        // old checkpoint cannot overwrite the reset after it has been committed.
        lock(writeGate)lock(gate){
            if(closed||cleanupBlocked||!memoryOnly&&writer==null)throw new InvalidOperationException("经验记录不可写，累计未清零。请先处理保存故障。");
            long offset=estimate.Read().EstimatedGain;
            var next=Document() with{LifetimeBeforeCurrent=0,LifetimePartial=false,LifetimeResetOffset=offset};
            try{if(!memoryOnly)Persist(next);}
            catch{saveErrors++;error="累计清零未保存，原累计已保留";throw new InvalidOperationException(error);}
            lifetimeBeforeCurrent=0;lifetimeResetOffset=offset;lifetimePartial=false;error=null;pending=null;savedRevision=++revision;Monitor.PulseAll(gate);
        }
    }
    internal ExperienceEntry? Previous=>history.FirstOrDefault();
    // Legacy metadata remains readable, but is neither requested nor used by UI/totals.
    internal ExperienceBaseline? SuggestedBaseline=>Read().Baseline??history.Select(x=>x.Estimate.Baseline).FirstOrDefault(x=>x!=null);
    internal bool HasObservation{get{lock(gate)return observations>0;}}
    internal string? SaveError{get{lock(gate)return error;}}
    internal int SaveErrors{get{lock(gate)return saveErrors;}}
    internal bool Initialize(int level,long experience)
    {lock(gate){if(closed)return false;bool accepted=estimate.Initialize(level,experience);Queue();return accepted;}}
    internal void Suspend()
    {lock(gate){if(closed)return;estimate.Suspend();pendingPanel=null;lastTimerEnd=null;Queue();}}
    internal void Observe(Observation sample,Phase phase,int round,int reanchors,int nextInterval)
    {
        // Statistics may degrade independently; they never interrupt automation.
        try{lock(gate){
            if(closed)return;
            bool valid=round>=0&&reanchors>=0&&nextInterval is >0 and <=120000&&sample.Identity&&sample.Geometry&&sample.Location=="NO_VERIFIED_CANDIDATE"&&sample.Count==0&&sample.D2=="VISIBLE"&&sample.PanelLocated&&sample.PanelLocation=="UNIQUE_PANEL"&&double.IsFinite(sample.Source)&&double.IsFinite(sample.Now)&&sample.Source>=OpenedAt&&sample.Source<long.MaxValue&&sample.Now>=sample.Source&&sample.Now-sample.Source<=3000;
            if(!valid){estimate.Suspend();pendingPanel=null;lastTimerEnd=null;Queue();return;}
            // A replayed panel cannot change round/clock metadata before the
            // numeric core gets a chance to reject its already-seen source.
            // Invalid identity/geometry/age above still suspend independently.
            if(sample.Source<=lastPanelSource)return;
            lastPanelSource=sample.Source;
            if(phase!=Phase.Performing){pendingPanel=sample;pendingGap=(long)nextInterval+3000;return;}
            if(round!=lastRound||reanchors!=lastReanchors){estimate.RoundEnded();lastTimerEnd=null;lastRound=round;lastReanchors=reanchors;}
            if(sample.CountdownQuality=="DUAL_PASS_AGREEMENT"&&sample.CountdownSeconds is >=0 and <=5999)
            {
                double end=sample.Source+sample.CountdownSeconds.Value*1000d;
                if(lastTimerEnd.HasValue&&Math.Abs(end-lastTimerEnd.Value)>3000){estimate.Suspend();pendingPanel=null;}
                lastTimerEnd=end;
            }
            long budget=Math.Clamp((long)nextInterval+3000,3001,ExperienceEstimate.MaximumGapMs);
            if(pendingPanel is Observation prior&&prior.Source<sample.Source&&sample.Source-prior.Source<=pendingGap)
                Feed(prior,round,pendingGap);
            pendingPanel=null;Feed(sample,round,budget);Queue();
        }}catch{Suspend();}
    }
    void Feed(Observation sample,int round,long budget)
    {if(estimate.Observe(checked((long)Math.Floor(sample.Source)),checked((long)Math.Floor(sample.Now)),true,round,budget))observations++;}
    ExperienceDocument Document()
    {
        var current=estimate.Read();bool available=Total(current).HasValue;
        return new(Schema,new(id,started,closed?DateTime.UtcNow:null,closed,observations,current),history,available?lifetimeBeforeCurrent:null,lifetimePartial||!available,lifetimeResetOffset);
    }
    void Queue(){if(memoryOnly||writer==null)return;pending=Document();revision++;changed.Set();}
    internal bool Flush(int timeoutMs=3000)
    {
        if(timeoutMs<0)throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        lock(gate){long target=revision;while(writer!=null&&savedRevision<target&&watch.ElapsedMilliseconds<timeoutMs)Monitor.Wait(gate,Math.Max(1,timeoutMs-(int)watch.ElapsedMilliseconds));return memoryOnly||writer!=null&&savedRevision>=target&&error is null;}
    }
    static void Validate(ExperienceDocument document)
    {
        if(document.Schema is not (Schema or PreviousSchema or LegacySchema)||document.Current==null||document.History==null||document.History.Length>10)throw new InvalidDataException("schema");
        if(document.Schema is Schema or PreviousSchema&&(!document.LifetimeBeforeCurrent.HasValue&&!document.LifetimePartial||document.LifetimeBeforeCurrent is long total&&(total<0||total%6!=0)))throw new InvalidDataException("lifetime");
        if(document.Schema==Schema&&(document.LifetimeResetOffset is not long offset||offset<0||offset%6!=0||offset>document.Current.Estimate.EstimatedGain))throw new InvalidDataException("reset_offset");
        if(new[]{document.Current}.Concat(document.History).Select(x=>x?.Id).Distinct().Count()!=document.History.Length+1)throw new InvalidDataException("duplicate_session");
        foreach(var entry in new[]{document.Current}.Concat(document.History))
        {
            if(entry is null||entry.Id==Guid.Empty||entry.Observations<0||entry.Estimate is not ExperienceEstimateView value||value.EstimatedGain<0||value.EstimatedGain%6!=0||value.ObservedMilliseconds<0||value.EstimatedGain>value.ObservedMilliseconds/60000*6||entry.Complete!=value.Closed||entry.Complete!=entry.EndedUtc.HasValue||entry.EndedUtc<entry.StartedUtc)throw new InvalidDataException("entry");
            if(value.Baseline is ExperienceBaseline b&&(b.Level<=0||b.Experience<0||b.EstimateAtInitialization<0||b.EstimateAtInitialization>value.EstimatedGain||value.EstimatedSinceInitialization!=value.EstimatedGain-b.EstimateAtInitialization))throw new InvalidDataException("baseline");
            if(value.Baseline is null&&value.EstimatedSinceInitialization!=null)throw new InvalidDataException("baseline_missing");
        }
    }
    void WriteLoop()
    {
        while(true)
        {
            changed.WaitOne();lock(writeGate){ExperienceDocument? snapshot;long saving;
            lock(gate){snapshot=pending;pending=null;saving=revision;}
            if(snapshot!=null&&!cleanupBlocked)
            {
                try{Persist(snapshot);lock(gate){error=null;savedRevision=saving;Monitor.PulseAll(gate);}}
                catch{lock(gate){saveErrors++;error="经验记录未保存";}}
            }
            }
            lock(gate){if(stopping&&pending==null)return;if(pending!=null)changed.Set();}
        }
    }
    void Persist(ExperienceDocument snapshot)
    {
        string temporary=Path.Combine(folder,"session-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(stream,snapshot);File.Move(temporary,Path.Combine(folder,"session.json"),true);}
        finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch{cleanupBlocked=true;lock(gate){saveErrors++;error="经验记录清理失败，后续保存已暂停";}}}
    }
    public void Dispose()
    {
        lock(gate){if(closed)return;estimate.Close();pendingPanel=null;closed=true;stopping=true;Queue();}
        if(writer!=null&&!writer.Join(2000)){lock(gate){saveErrors++;error="经验记录结束保存未完成";}}
        if(writer==null||!writer.IsAlive)changed.Dispose();
    }
}
