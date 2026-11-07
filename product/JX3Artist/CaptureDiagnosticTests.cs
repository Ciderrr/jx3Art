using System.Text.Json;
using Jx3.OneClickTest;
namespace JX3Artist;
internal static class CaptureDiagnosticTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool condition){if(!condition)throw new Exception("capture diagnostic assertion failed");}
        var b=new Box(0,0,3840,2088);
        var valid=new CaptureEvidence("after_pull",true,false,b,b,true,false,3840,2088,3840,2088);
        test("capture diagnostics: accepted stable frame remains accepted",()=>CaptureDiagnostics.Ensure(valid));
        foreach(var row in new (string Code,CaptureEvidence Value)[]{
            ("TARGET_IDENTITY_LOST",valid with{Identity=false}),
            ("WINDOW_MINIMIZED",new("before_pull",true,true)),
            ("FRAME_UNAVAILABLE",valid with{FrameOk=false,EngineError="no-frame"}),
            ("FRAME_POOL_RECREATED",valid with{Recreated=true}),
            ("WINDOW_BOUNDS_UNAVAILABLE",valid with{After=null,BoundsError="unavailable"}),
            ("WINDOW_BOUNDS_CHANGED",valid with{After=new(10,10,3850,2098)}),
            ("WINDOW_SIZE_UNSUPPORTED",valid with{Before=new(0,0,1920,1080),After=new(0,0,1920,1080)}),
            ("SURFACE_SIZE_UNSUPPORTED",valid with{SurfaceW=1920}),
            ("CONTENT_SIZE_UNSUPPORTED",valid with{ContentH=1080})})
            test("capture diagnostics: "+row.Code,()=>
            {
                try{CaptureDiagnostics.Ensure(row.Value);throw new Exception("accepted invalid capture");}
                catch(CaptureFailureException ex){Check(ex.Reasons.Contains(row.Code) && ex.Evidence==row.Value && ex.Message.Contains(CaptureDiagnostics.Describe(row.Code)));}
            });
        test("capture diagnostics: no frame does not invent measured geometry",()=>
        {
            var reasons=CaptureDiagnostics.Reasons(valid with{FrameOk=false,SurfaceW=0,SurfaceH=0,ContentW=0,ContentH=0});
            Check(reasons.SequenceEqual(new[]{"FRAME_UNAVAILABLE"}));
        });
        test("capture diagnostics: independent failures retained in structured JSON",()=>
        {
            var e=valid with{Recreated=true,SurfaceW=1920,ContentH=1080,EngineError="test engine detail",Source=12345};
            try{CaptureDiagnostics.Ensure(e);throw new Exception("accepted");}
            catch(CaptureFailureException ex)
            {
                using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{ex.Reasons,ex.Evidence}));
                Check(json.RootElement.GetProperty("Reasons").GetArrayLength()==3);
                Check(json.RootElement.GetProperty("Evidence").GetProperty("SurfaceW").GetInt32()==1920 && json.RootElement.GetProperty("Evidence").GetProperty("EngineError").GetString()=="test engine detail");
            }
        });
        test("capture diagnostics: all guard combinations preserve rejection contract",()=>
        {
            for(int bits=0;bits<128;bits++)
            {
                var e=valid with{Identity=(bits&1)==0,FrameOk=(bits&2)==0,Recreated=(bits&4)!=0,
                    After=(bits&8)==0?b:new(1,0,3841,2088),Before=(bits&16)==0?b:new(0,0,1920,1080),SurfaceW=(bits&32)==0?3840:1920,ContentH=(bits&64)==0?2088:1080};
                bool previous=!e.Identity || e.FrameOk!=true || e.Recreated==true || e.After!=e.Before || e.Before.Width!=3840 || e.Before.Height!=2088 || e.SurfaceW!=3840 || e.SurfaceH!=2088 || e.ContentW!=3840 || e.ContentH!=2088;
                Check(previous==(CaptureDiagnostics.Reasons(e).Length>0));
            }
        });
        test("capture diagnostics: recovery evaluated from new frame only",()=>
        {
            Check(CaptureDiagnostics.Reasons(valid with{Recreated=true}).Length>0);
            CaptureDiagnostics.Ensure(valid);
        });
    }
}
