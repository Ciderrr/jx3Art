using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace JX3Artist;

internal sealed record RecordingHealth(int Samples,int Errors,DateTime? LastSuccess,string? Error,bool Closed)
{
    internal string Message=>Error!=null?"运行诊断记录异常："+Error:Closed?"本次运行诊断记录已结束":Samples==0?"正在准备运行诊断记录":Errors>0?$"运行诊断记录已恢复，曾失败 {Errors} 次":"运行诊断记录正常";
}

// Application-owned, read-only diagnostics. No child process, window or input APIs.
internal sealed class ResourceRecording:IDisposable
{
    readonly CancellationTokenSource stop=new();readonly Thread worker;readonly Func<object> collect;
    readonly ResourceWriter writer;readonly TimeSpan interval;
    RecordingHealth health=new(0,0,null,null,false);
    internal RecordingHealth Health=>Volatile.Read(ref health);
    internal string Folder=>writer.Folder;
    object? latestCounters;
    internal object? LatestCounters=>Volatile.Read(ref latestCounters);
    internal event Action<object>? Sampled;
    internal ResourceRecording(string dataRoot):this(Path.Combine(dataRoot,"resources","app_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..8]),()=>ReadCounters(dataRoot),TimeSpan.FromMinutes(1)){}
    internal ResourceRecording(string folder,Func<object> collector,TimeSpan interval)
    {
        if(interval<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(interval));
        writer=new(folder);collect=collector;this.interval=interval;
        worker=new Thread(Work){IsBackground=true,Name="JX3Artist-resources"};worker.Start();
    }
    internal static object ReadCounters(string root)
    {
        using var p=Process.GetCurrentProcess();p.Refresh();long bytes=0;int files=0;
        foreach(string name in new[]{"cache","logs"})
        {
            string folder=Path.Combine(root,name);if(!Directory.Exists(folder))continue;
            foreach(string file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
                try{bytes+=new FileInfo(file).Length;files++;}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        return new{pid=p.Id,workingSetBytes=p.WorkingSet64,privateBytes=p.PrivateMemorySize64,cpuSeconds=p.TotalProcessorTime.TotalSeconds,handles=p.HandleCount,threads=p.Threads.Count,cacheAndLogFiles=files,cacheAndLogBytes=bytes};
    }
    void Work()
    {
        try
        {
            do
            {
                var before=Health;
                try
                {
                    var counters=collect();writer.Sample(counters);Volatile.Write(ref latestCounters,counters);try{Sampled?.Invoke(counters);}catch{}
                    Volatile.Write(ref health,new(before.Samples+1,before.Errors,DateTime.UtcNow,null,false));
                }
                catch(Exception e)
                {
                    var failed=before with{Errors=before.Errors+1,Error=e.GetType().Name+": "+e.Message};Volatile.Write(ref health,failed);
                    try{writer.Error(failed);}catch{} // In-memory health still reaches the GUI if disk writes fail.
                }
            }while(!stop.Token.WaitHandle.WaitOne(interval));
        }
        finally
        {
            try{writer.Close(Health);Volatile.Write(ref health,Health with{Closed=true});}
            catch(Exception e){Volatile.Write(ref health,Health with{Error="结束记录保存失败："+e.Message,Errors=Health.Errors+1});}
        }
    }
    int disposed;
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();
        // A stuck filesystem/provider must not block application shutdown forever.
        if(worker.Join(1500))stop.Dispose();
        // If it could not finish, the persisted complete=false marker stays truthful.
    }
}

internal sealed class ResourceWriter
{
    internal const long MaxPartBytes=2*1024*1024;
    internal string Folder{get;}
    readonly DateTime started=DateTime.UtcNow;bool initialized,claimed;
    internal ResourceWriter(string folder)=>Folder=Path.GetFullPath(folder);
    void Initialize()
    {
        if(initialized)return;
        if(!claimed)
        {
            if(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any())throw new IOException("资源记录目录已被占用");
            Directory.CreateDirectory(Folder);claimed=true;
        }
        Atomic("session.json",new{schema="jx3-app-resources-v1",version=typeof(ResourceWriter).Assembly.GetName().Version?.ToString(),startedUtc=started,complete=false,scope="whole application, own process counters; excludes GPU and short-lived recognition peaks"});initialized=true;
    }
    void Atomic(string name,object data)
    {
        string temporary=Path.Combine(Folder,name+".tmp");File.WriteAllText(temporary,JsonSerializer.Serialize(data));File.Move(temporary,Path.Combine(Folder,name),true);
    }
    void Append(object data)
    {
        Initialize();string path=Path.Combine(Folder,"resources.jsonl");
        if(File.Exists(path) && new FileInfo(path).Length>=MaxPartBytes)
        {
            string oldest=path+".2";if(File.Exists(oldest))File.Delete(oldest);
            if(File.Exists(path+".1"))File.Move(path+".1",oldest);File.Move(path,path+".1");
        }
        using var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read);
        using var output=new StreamWriter(stream);output.WriteLine(JsonSerializer.Serialize(data));
    }
    internal void Sample(object counters)=>Append(new{utc=DateTime.UtcNow,kind="sample",data=counters});
    internal void Error(RecordingHealth health)=>Append(new{utc=DateTime.UtcNow,kind="sample_error",health.Errors,health.Error});
    internal void Close(RecordingHealth health)
    {
        Initialize();Atomic("session.json",new{schema="jx3-app-resources-v1",version=typeof(ResourceWriter).Assembly.GetName().Version?.ToString(),startedUtc=started,endedUtc=DateTime.UtcNow,complete=true,health.Samples,health.Errors,health.LastSuccess,health.Error,
            scope="normal recorder close; does not prove every sample succeeded or game assistance completed"});
    }
}
