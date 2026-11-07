using System.IO;
using System.Text.Json;
namespace JX3Artist;
internal static class SupportHistoryTests
{
    static string Root(){var root=Path.Combine(Path.GetTempPath(),"jx3-history-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    static void Check(bool ok){if(!ok)throw new Exception("automatic context contract failed");}
    internal static void Add(Action<string,Action> test)
    {
        test("automatic incident survives recent timeline rollover with sanitized before and after",()=>{
            string root=Root();try{using var history=new SupportHistory(root);
                history.Record("start_requested",new{accepted=true,maxStarts=4,unattended=false,origin="button"});
                history.Record("observation_context",new{width=3840,height=2160,Dpi=144,visualScale=1.04,OtherWindowTitle="SECRET"});
                history.Record("resource_sample",new{workingSetBytes=123456,pid=1234,username="SECRET"});
                history.Record("problem_detected",new{state="Paused",error="SECRET_OCR"},"JX3-START-001");history.Record("stop_requested",new{origin="shortcut"});
                for(int i=0;i<600;i++)history.Record("state",new{state="Performing",Starts=i});
                var snapshot=history.Snapshot();Check(snapshot.Recent.Length==256&&snapshot.Incidents.Length==1&&snapshot.Incidents[0].After.Length==20);
                Check(snapshot.Incidents[0].Before.Any(x=>x.Kind=="start_requested")&&snapshot.Incidents[0].After.Any(x=>x.Kind=="stop_requested"));
                string report=SupportReport.Preview(root,new(Phase.Stopped,""),snapshot);Check(report.Contains("JX3-START-001")&&report.Contains("3840")&&report.Contains("workingSetBytes")&&!report.Contains("SECRET")&&!report.Contains("username")&&!report.Contains("\"pid\""));
                snapshot.Incidents[0].Context["secret"]=JsonSerializer.SerializeToElement("SECRET");snapshot.Incidents[0].Before[0]=snapshot.Incidents[0].Before[0] with{Data=JsonSerializer.SerializeToElement(new{Message="SECRET"})};
                Check(!SupportReport.Preview(root,new(Phase.Stopped,""),snapshot).Contains("SECRET")&&!JsonSerializer.Serialize(history.Snapshot()).Contains("SECRET"));
            }finally{Directory.Delete(root,true);}
        });
        test("automatic context reload retains incidents but does not claim previous launch conditions are current",()=>{
            string root=Root();try{using(var first=new SupportHistory(root)){first.Record("start_requested",new{accepted=true,maxStarts=2});first.Record("observation_context",new{width=1920,height=1080,Dpi=144});first.Record("problem_detected",new{state="Paused"},"JX3-UNKNOWN-001");first.Flush();Check(first.Snapshot().SavedSuccessfully);}
                using var next=new SupportHistory(root);var snapshot=next.Snapshot();Check(snapshot.Incidents.Length==1&&snapshot.Incidents[0].Context.ContainsKey("observation_context")&&!snapshot.Current.ContainsKey("observation_context"));
                Check(new FileInfo(Path.Combine(root,"support-context","snapshot.json")).Length<=2*1024*1024);
            }finally{Directory.Delete(root,true);}
        });
        test("incident retention deduplicates a pause and bounds launches and samples",()=>{
            string root=Root();try{using var history=new SupportHistory(root);for(int run=0;run<6;run++){
                    history.Record("start_requested",new{accepted=true});history.Record("problem_detected",new{state="Paused"},"JX3-START-001");history.Record("problem_detected",new{state="Paused"},"JX3-START-001");
                    for(int i=0;i<100;i++)history.Record("state",new{state="Performing"});
                }var snapshot=history.Snapshot();Check(snapshot.Incidents.Length==3&&snapshot.Incidents.All(x=>x.Before.Length<=80&&x.After.Length==20)&&snapshot.Recent.Length<=256);
                history.Record("start_requested",new{accepted=false,origin="shortcut"});Check(history.Snapshot().Current["start_requested"].GetProperty("accepted").GetBoolean());
            }finally{Directory.Delete(root,true);}
        });
        test("unowned context directory is preserved and diagnostic failure never stops memory collection",()=>{
            string root=Root();try{string folder=Path.Combine(root,"support-context");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"notes.txt"),"keep");
                using var history=new SupportHistory(root);history.Record("problem_detected",new{state="Paused"},"JX3-UNKNOWN-001");history.Flush();Check(history.SaveErrors>0&&history.Snapshot().Incidents.Length==1&&!File.Exists(Path.Combine(folder,".owner"))&&File.ReadAllText(Path.Combine(folder,"notes.txt"))=="keep");
            }finally{Directory.Delete(root,true);}
        });
        test("stored context is projected again on reload instead of trusting cached strings",()=>{
            string root=Root();try{using(var original=new SupportHistory(root)){original.Flush();}
                var step=new SupportStep(1,DateTime.UtcNow,Guid.NewGuid(),1,"0.1.49.0","state",JsonSerializer.SerializeToElement(new{state="Performing",Message="SECRET",Location="SECRET"}));
                var bad=new SupportContext("jx3-context-v1",new[]{step},Array.Empty<SupportIncident>(),new(){["secret"]=JsonSerializer.SerializeToElement("SECRET")},false,0);File.WriteAllText(Path.Combine(root,"support-context","snapshot.json"),JsonSerializer.Serialize(bad));
                using var next=new SupportHistory(root);Check(!JsonSerializer.Serialize(next.Snapshot()).Contains("SECRET"));
            }finally{Directory.Delete(root,true);}
        });
    }
}
