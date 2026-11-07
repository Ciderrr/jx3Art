using System.IO;
using System.Text.Json;
using Jx3.OneClickTest;
namespace JX3Artist;
internal static class WindowFramePolicyTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Assert(bool yes){if(!yes)throw new Exception("window frame assertion");}
        void Reject(Action action){try{action();}catch(IOException){return;}throw new Exception("invalid window contract accepted");}
        test("anchor failure diagnostic crop is bounded and never recognition permission",()=>{
            var n=new WindowSnapshot(new(0,0,1920,1080),new(0,0,1920,1080),new(0,0,1920,1080),96,false,true);var m=new WindowFrameMap(n,n,1920,1080,1920,1080,1920L*1080*4);
            string Payload(int[] region)=>JsonSerializer.Serialize(new{d2_location=new{verified=false,diagnostic_region=region}});
            Assert(WindowFramePolicy.DiagnosticRegion(Payload(new[]{300,400,344,442}),m)==new System.Windows.Int32Rect(300,400,44,42));
            Assert(WindowFramePolicy.DiagnosticRegion("{\"d2_location\":{}}",m)==null);
            Reject(()=>WindowFramePolicy.DiagnosticRegion(Payload(new[]{300,400,900,442}),m));
            Reject(()=>WindowFramePolicy.DiagnosticRegion(Payload(new[]{1900,400,1944,442}),m));
        });
        WindowFrameMap Map(int w,int h){var n=new WindowSnapshot(new(-10,20,w-10,h+20),new(-10,20,w-10,h+20),new(-2,50,w-18,h+12),144,false,true);return new(n,n,w,h,w,h,(long)w*h*4);}
        string Json(WindowFrameMap m,int w=1920,int x=1600,bool panel=false,double scale=1,int tileCount=1)=>JsonSerializer.Serialize(new{frame_profile=new{schema="window-original-ui-v1",verified=true,width=w,height=m.Height,content=new[]{8,30,m.Width-8,m.Height-8},ui_scale=scale,tile_count=tileCount},location=new{candidates=new[]{new{bbox=new[]{x,900,x+72,966}}}},d2_location=new{verified=panel,candidates=Array.Empty<object>(),timer_frame_bbox=new[]{500,300,568,325}}});
        test("window frames: portable capture matrix accepts matching surfaces",()=>{
            foreach(var (w,h) in new[]{(1920,1080),(2560,1440),(3840,2088),(3840,2160)}){var m=Map(w,h);var b=m.Snapshot.Visible;CaptureDiagnostics.Ensure(new("after_pull",true,false,b,b,true,false,w,h,w,h,WindowFrame:true));Assert(WindowFramePolicy.Validate(Json(m,w),m)==null);}
        });
        test("window frames: mismatched size scale client and work budget rejected",()=>{
            var m=Map(1920,1080);Reject(()=>WindowFramePolicy.Validate(Json(m,w:1919),m));Reject(()=>WindowFramePolicy.Validate(Json(m,scale:.34),m));Reject(()=>WindowFramePolicy.Validate(Json(m,tileCount:10),m));Reject(()=>WindowFramePolicy.Validate(Json(m,x:1900),m));Reject(()=>WindowFramePolicy.Validate(Json(m).Replace("30","31"),m));
        });
        test("window frames: source-relative numeric crop is validated",()=>{var m=Map(1920,1080);Assert(WindowFramePolicy.Validate(Json(m,panel:true),m)==new Box(500,300,568,325));Reject(()=>WindowFramePolicy.Validate(Json(m,panel:true).Replace("568","570"),m));});
        test("window frames: old checks remain closed and portable checks keep every input guard",()=>{
            var c=new Check(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED","UNIQUE_CANDIDATE",1,1920,1080,0,new(1600,900,1672,966),"UNKNOWN");Assert(ClickPolicy.Refusal(c)=="GEOMETRY_NOT_VERIFIED");c=c with{WindowFrame=true};Assert(ClickPolicy.Refusal(c)==null);
            foreach(var invalid in new[]{c with{Geometry=false},c with{Foreground=false},c with{Identity=false},c with{Cancelled=true},c with{ButtonHeld=true},c with{HitTarget=false},c with{AgeMs=4000},c with{CandidateCount=2},c with{D2="VISIBLE"},c with{Candidate=new(1800,1030,1872,1096)}})Assert(ClickPolicy.Refusal(invalid)!=null);
        });
        test("window frames: resized and mismatched capture surface rejected",()=>{
            var m=Map(1920,1080);var b=m.Snapshot.Visible;var e=new CaptureEvidence("after_pull",true,false,b,b,true,false,1920,1080,1920,1080,WindowFrame:true);
            foreach(var bad in new[]{e with{SurfaceW=3840},e with{ContentH=1079},e with{Recreated=true},e with{After=new(-9,20,1911,1100)}})Assert(CaptureDiagnostics.Reasons(bad).Length>0);
        });
        test("window frames: numeric region rejects border overflow and cancellation before OCR",()=>{
            var r=new CountdownReader().ReadRegion(new byte[1920*1080*4],1920,1080,new(1890,100,1958,125),CancellationToken.None).GetAwaiter().GetResult();Assert(r.Seconds==null&&r.Reason=="timer_out_of_frame");
            using var cancel=new CancellationTokenSource();cancel.Cancel();try{new CountdownReader().ReadRegion(new byte[4],1,1,new(0,0,68,25),cancel.Token).GetAwaiter().GetResult();throw new Exception("ignored cancellation");}catch(OperationCanceledException){}
        });
        test("window scale hint is invalidated by movement DPI mode and unconfirmed frame",()=>{
            var n=Map(1920,1080).Snapshot;var hint=new UiScaleHint();hint.Observe(n,.53,true);Assert(hint.For(n)==.53);Assert(hint.For(n with{Dpi=192})==null);Assert(hint.For(n with{Maximized=true})==null);hint.Observe(n,.53,false);Assert(hint.For(n)==null);
        });
        test("scaled portable click requires actual scaled target and all existing gates",()=>{
            var c=new Check(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED","UNIQUE_CANDIDATE",1,1920,1080,100,new(500,500,539,535),"UNKNOWN",WindowFrame:true,UiScale:.53);
            Assert(ClickPolicy.Refusal(c)==null);foreach(var bad in new[]{c with{UiScale=1},c with{UiScale=.34},c with{ButtonHeld=true},c with{Geometry=false},c with{Candidate=new(500,500,572,566)}})Assert(ClickPolicy.Refusal(bad)!=null);
        });
        test("text region may change size independently but cannot escape semantic search",()=>{
            var m=Map(1920,1080);string value=Json(m,panel:true).Replace("\"timer_frame_bbox\":[500,300,568,325]","\"text_status\":\"UNIQUE_TEXT_LINE\",\"text_search_region\":[480,280,650,370],\"timer_frame_bbox\":[500,300,610,350]");
            Assert(WindowFramePolicy.Validate(value,m)==new Box(500,300,610,350));Reject(()=>WindowFramePolicy.Validate(value.Replace("610,350","670,350"),m));
            Assert(WindowFramePolicy.Validate(value.Replace("UNIQUE_TEXT_LINE","TEXT_UNRESOLVED"),m)==null);
        });
        test("published portable recognizer detects synthetic entry and validates client map",()=>{
            var root=Path.Combine(AppContext.BaseDirectory,"engine");string file=Path.Combine(Path.GetTempPath(),"jx3-window-smoke-"+Guid.NewGuid().ToString("N")+".bmp");
            try{
                var visual=new System.Windows.Media.DrawingVisual();using(var draw=visual.RenderOpen()){
                    draw.DrawRectangle(System.Windows.Media.Brushes.Black,null,new System.Windows.Rect(0,0,1920,1080));
                    var icon=new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(root,"vis2/template/d1_icon_template.png")));var gold=new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(root,"int1/calibration/assets/start_bar_gold_v1.png")));
                    draw.DrawImage(icon,new System.Windows.Rect(1600,900,72,66));draw.DrawImage(gold,new System.Windows.Rect(1461,896,129,79));
                }
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1920,1080,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.BmpBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var stream=File.Create(file))encoder.Save(stream);
                var map=Map(1920,1080);var start=RecognizerProcess.ForFrame(Path.Combine(root,"diagnostics/one_click/locate.py"),file,map.ClientInFrame);start.WorkingDirectory=Path.GetTempPath();using var child=System.Diagnostics.Process.Start(start)??throw new IOException("no child");var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();if(!child.WaitForExit(10000)){child.Kill(true);child.WaitForExit();throw new TimeoutException("portable smoke timeout");}if(child.ExitCode!=0)throw new IOException(error.GetAwaiter().GetResult());var text=output.GetAwaiter().GetResult();Assert(WindowFramePolicy.Validate(text,map)==null);var o=GameSession.ReadObservation(text,1000,1100,true,true,file,out var box);Assert(EntryReadiness.Confirmed(o)&&box==new Box(1600,900,1672,966));
            }finally{if(File.Exists(file))File.Delete(file);}
        });
    }
}
