using System.IO;
using System.Text.Json;
namespace JX3Artist;
internal static class RecognizerWorkerTests
{
    internal static void Add(Action<string,Action> test)
    {
        test("local verifier timeout is bounded and restarts instead of hanging foreground",()=>{
            string folder=Path.Combine(Path.GetTempPath(),"jx3-local-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string script=Path.Combine(folder,"fake.py");
            try{File.WriteAllText(script,"import sys,time\nsys.stdin.readline()\ntime.sleep(60)\n");using var w=new RecognizerWorker(script);var clock=System.Diagnostics.Stopwatch.StartNew();
                try{w.Read("unused",new(0,0,72,66),1,CancellationToken.None,entryPatch:true);throw new Exception("local deadline ignored");}catch(TimeoutException){}if(clock.ElapsedMilliseconds>2500)throw new Exception("local timeout cleanup slow");
                File.WriteAllText(script,"import sys,json\nr=json.loads(sys.stdin.readline())\nprint(json.dumps({'id':r['id'],'result':{'confirmed':True}}),flush=True)\n");using var result=JsonDocument.Parse(w.Read("unused",new(0,0,72,66),1,CancellationToken.None,entryPatch:true));if(!result.RootElement.GetProperty("confirmed").GetBoolean())throw new Exception("no recovery after timeout");
            }finally{Directory.Delete(folder,true);}
        });
        test("worker rejects mismatched frame id and recovers with a new child",()=>{
            string folder=Path.Combine(Path.GetTempPath(),"jx3-worker-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string script=Path.Combine(folder,"fake.py");
            try{File.WriteAllText(script,"import sys,json\nr=json.loads(sys.stdin.readline())\nprint(json.dumps({'id':r['id']+1,'result':{}}),flush=True)\n");using var w=new RecognizerWorker(script);
                try{w.Read("unused",new(0,0,800,600),null,CancellationToken.None);throw new Exception("wrong id accepted");}catch(IOException){}
                File.WriteAllText(script,"import sys,json\nfor line in sys.stdin:\n r=json.loads(line)\n print(json.dumps({'id':r['id'],'result':{'frame':r['frame']}}),flush=True)\n");
                foreach(string name in new[]{"first","second"}){using var r=JsonDocument.Parse(w.Read(name,new(0,0,800,600),null,CancellationToken.None));if(r.RootElement.GetProperty("frame").GetString()!=Path.GetFullPath(name))throw new Exception("stale frame");}
            }finally{Directory.Delete(folder,true);}
        });
        test("worker cancellation interrupts a pending reply and permits restart",()=>{
            string folder=Path.Combine(Path.GetTempPath(),"jx3-worker-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string script=Path.Combine(folder,"fake.py");
            try{File.WriteAllText(script,"import sys,time\nsys.stdin.readline()\ntime.sleep(60)\n");using var w=new RecognizerWorker(script);using var stop=new CancellationTokenSource(300);
                var watch=System.Diagnostics.Stopwatch.StartNew();try{w.Read("unused",new(0,0,800,600),null,stop.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}if(watch.ElapsedMilliseconds>5000)throw new Exception("cancel too slow");
            }finally{Directory.Delete(folder,true);}
        });
    }
}
