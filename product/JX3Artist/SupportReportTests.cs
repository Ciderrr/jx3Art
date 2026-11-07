using System.IO;
using System.IO.Compression;
using System.Text.Json;
namespace JX3Artist;
internal static class SupportReportTests
{
    internal static void Add(Action<string,Action> test)
    {
        test("support report strips secrets raw errors OCR paths and arbitrary strings",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"logs"));
            try{var data=new{Message="SECRET_ACCOUNT C:\\PRIVATE_PATH",error="SECRET_TOKEN",ImagePath="SECRET_SCREENSHOT",CountdownReason="SECRET_OCR",Location="SECRET_NAME",reasons=new[]{"WINDOW_MINIMIZED","SECRET_REASON"},D2="VISIBLE",Started=true,Metrics=new{RecognitionMs=123.0,RecognizerScheduling="SECRET_PID"}};
                File.WriteAllText(Path.Combine(root,"logs/events.jsonl"),JsonSerializer.Serialize(new{utc="2026-11-02T00:00:00Z",kind="paused",data})+"\n{bad partial\n");
                string preview=SupportReport.Preview(root,new(Phase.Paused,"SECRET_EXCEPTION"));if(preview.Contains("SECRET")||preview.Contains(root)||!preview.Contains("JX3-UNKNOWN-001")||!preview.Contains("RecognitionMs")||!preview.Contains("WINDOW_MINIMIZED"))throw new Exception("privacy projection");
                using var doc=JsonDocument.Parse(preview);if(doc.RootElement.GetProperty("discarded").GetInt32()!=1)throw new Exception("partial log not diagnosed");
            }finally{Directory.Delete(root,true);}
        });
        test("support archive contains exactly reviewed JSON and no screenshot or original file",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var preview=SupportReport.Preview(root,new(Phase.Paused,"点击后未确认进入表演"));string path=Path.Combine(root,"report.zip");SupportReport.Export(path,preview);
                using(var zip=ZipFile.OpenRead(path)){if(zip.Entries.Count!=2||zip.GetEntry("README.txt")==null)throw new Exception("unexpected attachment");using var reader=new StreamReader(zip.GetEntry("report.json")!.Open());if(reader.ReadToEnd()!=preview)throw new Exception("export differs from reviewed data");}
                try{SupportReport.Export(path,preview);throw new Exception("existing file replaced");}catch(IOException){}
                if(ProblemCatalog.Identify("点击后未确认进入表演").Code!="JX3-START-001")throw new Exception("stable code changed");
            }finally{Directory.Delete(root,true);}
        });
        test("support report bounds retained history and ignores unrecognized event types",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"logs"));
            try{var line=JsonSerializer.Serialize(new{utc="2026-11-02T00:00:00Z",kind="state",data=new{state="Performing",Starts=1}});File.WriteAllText(Path.Combine(root,"logs/events.jsonl"),string.Join('\n',Enumerable.Repeat(line,220)));
                using var doc=JsonDocument.Parse(SupportReport.Preview(root,new(Phase.Stopped,"")));if(doc.RootElement.GetProperty("events").GetArrayLength()!=200)throw new Exception("unbounded history");
            }finally{Directory.Delete(root,true);}
        });
    }
}
