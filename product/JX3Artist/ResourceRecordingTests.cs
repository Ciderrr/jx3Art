using System.IO;
using System.Text.Json;
namespace JX3Artist;

internal static class ResourceRecordingTests
{
    internal static void Add(Action<string,Action> test,string root)
    {
        string Fresh()=>Path.Combine(root,"resource_test_"+Guid.NewGuid().ToString("N"));
        void Check(bool yes){if(!yes)throw new Exception("resource assertion failed");}
        bool Await(Func<bool> condition)=>SpinWait.SpinUntil(condition,TimeSpan.FromSeconds(4));
        test("resources: actual own-process sample and normal close",()=>
        {
            using var recording=new ResourceRecording(Fresh());Check(Await(()=>recording.Health.Samples>0));
            string folder=recording.Folder;recording.Dispose();
            using var summary=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"session.json")));
            Check(summary.RootElement.GetProperty("complete").GetBoolean());
            using var sample=JsonDocument.Parse(File.ReadLines(Path.Combine(folder,"resources.jsonl")).First());
            Check(sample.RootElement.GetProperty("data").GetProperty("pid").GetInt32()==Environment.ProcessId);
        });
        test("resources: collector failure recorded and next samples recover",()=>
        {
            int reads=0;using var r=new ResourceRecording(Fresh(),()=>Interlocked.Increment(ref reads)==1?throw new IOException("injected counter failure"):new{ok=true},TimeSpan.FromMilliseconds(20));
            Check(Await(()=>r.Health.Samples>=2));Check(r.Health.Errors==1 && r.Health.Error==null);
            r.Dispose();Check(File.ReadAllLines(Path.Combine(r.Folder,"resources.jsonl")).Any(line=>line.Contains("sample_error")));
        });
        test("resources: disk preparation error visible and retry recovers",()=>
        {
            string folder=Fresh();Directory.CreateDirectory(folder);string occupied=Path.Combine(folder,"test-owned-sentinel");File.WriteAllText(occupied,"do not overwrite");
            using var r=new ResourceRecording(folder,()=>new{ok=true},TimeSpan.FromMilliseconds(20));
            Check(Await(()=>r.Health.Errors>0));Check(r.Health.Samples==0 && r.Health.Error!=null && File.ReadAllText(occupied)=="do not overwrite");
            File.Delete(occupied);Check(Await(()=>r.Health.Samples>0 && r.Health.Error==null));
        });
        test("resources: unclosed writer stays explicitly incomplete",()=>
        {
            var writer=new ResourceWriter(Fresh());writer.Sample(new{ok=true});
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(writer.Folder,"session.json")));
            Check(!json.RootElement.GetProperty("complete").GetBoolean());
        });
        test("resources: rotation retains three parts",()=>
        {
            var writer=new ResourceWriter(Fresh());for(int i=0;i<4;i++)writer.Sample(new{payload=new string('x',(int)ResourceWriter.MaxPartBytes)});
            Check(Directory.GetFiles(writer.Folder,"resources.jsonl*").Length==3);
        });
        test("resources: each application uses a separate session directory",()=>
        {
            string rootPath=Fresh();using var first=new ResourceRecording(rootPath);using var second=new ResourceRecording(rootPath);
            Check(first.Folder!=second.Folder && Await(()=>first.Health.Samples>0 && second.Health.Samples>0));
        });
        test("resources: close interrupts long sample wait",()=>
        {
            using var r=new ResourceRecording(Fresh(),()=>new{ok=true},TimeSpan.FromMinutes(1));Check(Await(()=>r.Health.Samples>0));
            var watch=System.Diagnostics.Stopwatch.StartNew();r.Dispose();Check(watch.ElapsedMilliseconds<1400 && r.Health.Closed);
        });
    }
}
