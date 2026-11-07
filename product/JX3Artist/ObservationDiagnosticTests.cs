using System.IO;
using System.IO.Compression;
using System.Text.Json;
namespace JX3Artist;

internal static class ObservationDiagnosticTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("observation diagnostic assertion");}
        Picture Picture(Observation? observation=null,CaptureMetrics? metrics=null){
            var snapshot=new WindowSnapshot(new(0,0,1920,1080),new(0,0,1920,1080),new(0,0,1920,1080),144,false,true);
            var map=new WindowFrameMap(snapshot,snapshot,1920,1080,1920,1080,1920L*1080*4);
            return new(observation??new(100,200,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",ImagePath:"SECRET_IMAGE",EntryContextConfirmed:true),null,snapshot.Visible,"SECRET_FRAME",map,1,metrics);
        }
        test("read-only sample retains stage timings but excludes paths raw scheduling and evidence exceptions",()=>{
            var metrics=new CaptureMetrics(10,20,30,40,100,"SECRET_POLICY",PanelVerification:new("PAINTED_BODY_UNCONFIRMED",.89,.967,2148,.994,16,16));
            var sample=JsonSerializer.SerializeToElement(WindowObservation.Sample(Picture(metrics:metrics),1,101,"SECRET_PATH",null,null,"UnauthorizedAccessException: SECRET_ACCOUNT"));
            var d=sample.GetProperty("Diagnostics");var m=d.GetProperty("Metrics");Check(m.GetProperty("PullMapMs").GetDouble()==10&&m.GetProperty("TransportMs").GetDouble()==20&&m.GetProperty("RecognitionMs").GetDouble()==30&&m.GetProperty("ValidationOcrEvidenceMs").GetDouble()==40&&m.GetProperty("TotalMs").GetDouble()==100);
            Check(m.GetProperty("PanelVerification").GetProperty("Stage").GetString()=="PAINTED_BODY_UNCONFIRMED");
            Check(d.GetProperty("TimerEvidenceSaved").GetBoolean()&&!d.GetProperty("TimerEvidenceFailed").GetBoolean()&&d.GetProperty("EntryEvidenceFailed").GetBoolean()&&d.GetProperty("EntryEvidenceFailure").GetString()=="ACCESS_DENIED");
            Check(sample.GetProperty("recognized").GetBoolean()&&sample.GetProperty("actionsDispatched").GetInt32()==0&&!sample.GetRawText().Contains("SECRET"));
        });
        test("missing and nonfinite telemetry remains missing and never overrides recognition guards",()=>{
            var p=Picture();var empty=JsonSerializer.SerializeToElement(WindowObservation.Sample(p,1,100)).GetProperty("Diagnostics");Check(!empty.TryGetProperty("Metrics",out _)&&!empty.GetProperty("TimerEvidenceSaved").GetBoolean()&&!empty.GetProperty("TimerEvidenceFailed").GetBoolean());
            var invalid=ObservationDiagnostics.Create(new(double.NaN,double.PositiveInfinity,30,40,100,"SECRET"),null,"IOException: SECRET",null,"SECRET_OTHER");
            var m=invalid.GetProperty("Metrics");Check(!m.TryGetProperty("PullMapMs",out _)&&!m.TryGetProperty("TransportMs",out _)&&m.GetProperty("RecognitionMs").GetDouble()==30&&invalid.GetProperty("TimerEvidenceFailure").GetString()=="IO_ERROR"&&invalid.GetProperty("EntryEvidenceFailure").GetString()=="UNKNOWN"&&!invalid.GetRawText().Contains("SECRET"));
            Check(ObservationDiagnostics.Create(null,null,"InvalidDataException: SECRET",null,null).GetProperty("TimerEvidenceFailure").GetString()=="INVALID_DATA");
            var o=p.Observation;Check(WindowObservation.Recognized(o)&&WindowObservation.Recognized(o with{EntryContextConfirmed=false}));
            Check(JsonSerializer.SerializeToElement(WindowObservation.Sample(p with{Observation=o with{Source=double.NaN}},2,100)).GetProperty("sourceAgeMs").ValueKind==JsonValueKind.Null);
            foreach(var refused in new[]{o with{Now=3101},o with{Now=99},o with{Source=double.NaN},o with{Identity=false},o with{Geometry=false},o with{Count=2},o with{Location="NO_VERIFIED_CANDIDATE",Count=0}}){
                var row=JsonSerializer.SerializeToElement(WindowObservation.Sample(p with{Observation=refused},2,100,"SECRET",null));Check(!row.GetProperty("recognized").GetBoolean());
            }
            var panel=o with{Location="NO_VERIFIED_CANDIDATE",Count=0,EntryContextConfirmed=false,D2="VISIBLE",PanelLocated=true};Check(WindowObservation.Recognized(panel)&&!WindowObservation.Recognized(panel with{D2="UNKNOWN"})&&!WindowObservation.Recognized(panel with{PanelLocated=false}));
        });
        test("read-only sample diagnostics survive actual report preview and exact reviewed ZIP export",()=>{
            string root=Path.Combine(Path.GetTempPath(),"JX3Artist-observation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"logs"));
            try{
                var metrics=new CaptureMetrics(10,20,30,40,100,"SECRET_POLICY",PanelVerification:new("PAINTED_BODY_UNCONFIRMED",.89,.967,2148));
                var sample=WindowObservation.Sample(Picture(metrics:metrics),1,101,null,"IOException: SECRET_PATH");
                File.WriteAllText(Path.Combine(root,"logs/events.jsonl"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,kind="observation_cycle",data=sample}));
                string preview=SupportReport.Preview(root,new(Phase.Stopped,"SECRET_MESSAGE"));using var report=JsonDocument.Parse(preview);
                var diagnostics=report.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("Diagnostics");
                Check(diagnostics.GetProperty("Metrics").GetProperty("PanelVerification").GetProperty("MaskPixels").GetInt32()==2148&&diagnostics.GetProperty("TimerEvidenceFailure").GetString()=="IO_ERROR"&&!preview.Contains("SECRET"));
                string destination=Path.Combine(root,"report.zip");SupportReport.Export(destination,preview);using var zip=ZipFile.OpenRead(destination);Check(zip.Entries.Count==2);
                using var reader=new StreamReader(zip.GetEntry("report.json")!.Open());Check(reader.ReadToEnd()==preview);
            }finally{Directory.Delete(root,true);}
        });
    }
}
