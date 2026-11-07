using System.IO;
using Jx3.OneClickTest;
namespace JX3Artist;
internal static class WindowGeometryTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool ok){if(!ok)throw new Exception("geometry assertion");}
        void Reject(Action action){try{action();}catch(IOException){return;}throw new Exception("invalid map accepted");}
        var v=new Box(100,50,2020,1130);var s=new WindowSnapshot(new(92,42,2028,1138),v,new(108,80,2012,1122),144,false,true);
        WindowFrameMap Map(WindowSnapshot? a=null,WindowSnapshot? b=null,int w=1920,int h=1080,long bytes=8294400)=>new(a??s,b??s,w,h,w,h,bytes);
        test("geometry: bordered window maps client and physical click without DPI multiplication",()=>{
            var m=Map();Check(m.ClientInFrame==new Box(8,30,1912,1072));Check(m.ScreenCenter(new(500,300,572,366),s)==new Pointer(636,383));});
        test("geometry: negative monitor and maximized origin preserve physical point",()=>{
            var n=new WindowSnapshot(new(-1928,-8,8,1088),new(-1920,0,0,1080),new(-1920,0,0,1080),192,true,true);
            var m=Map(n,n);Check(m.ScreenCenter(new(10,10,82,76),n)==new Pointer(-1874,43));});
        test("geometry: desktop resolution never sets game UI scale",()=>{
            foreach(uint dpi in new uint[]{96,120,144,192,240}){var n=s with{Dpi=dpi};Check(Map(n,n).ScreenCenter(new(500,300,572,366),n)==new Pointer(636,383));}});
        test("geometry: frame padding titlebar and client edge cannot authorize click",()=>{
            var m=Map();foreach(var b in new[]{new Box(0,0,72,66),new Box(1900,500,1972,566),new Box(20,1070,92,1136),new Box(10,40,10,106),new Box(-1,40,71,106)})Reject(()=>m.ScreenCenter(b,s));});
        test("geometry: move resize DPI mode or client change invalidates before and after map",()=>{
            foreach(var n in new[]{s with{Visible=new(101,50,2021,1130)},s with{Client=new(108,81,2012,1122)},s with{Outer=new(91,42,2028,1138)},s with{Dpi=192},s with{Maximized=true},s with{Physical=false}}){Reject(()=>Map(s,n));Reject(()=>Map().ScreenCenter(new(500,300,572,366),n));}});
        test("geometry: invalid physical frame or byte geometry rejected",()=>{
            foreach(var n in new[]{s with{Dpi=0},s with{Physical=false},s with{Client=new(99,50,2012,1122)},s with{Visible=new(2020,50,100,1130)}})Reject(()=>Map(n,n));
            Reject(()=>Map(w:1919));Reject(()=>Map(bytes:8294399));Reject(()=>new WindowFrameMap(s,s,1921,1080,1920,1080,8294400));});
        test("geometry: fresh map after display change recovers but old map stays invalid",()=>{
            var m=Map();var n=s with{Dpi=192};Reject(()=>m.ScreenCenter(new(500,300,572,366),n));Check(Map(n,n).ScreenCenter(new(500,300,572,366),n)==new Pointer(636,383));});
        test("geometry: pre-dispatch change prevents DOWN and still completes release path",()=>{
            var n=new WindowSnapshot(new(0,0,3840,2088),new(0,0,3840,2088),new(0,0,3840,2088),96,true,true);var m=new WindowFrameMap(n,n,3840,2088,3840,2088,3840L*2088*4);var b=new Box(500,300,572,366);int down=0,up=0;
            var check=new Check(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED","UNIQUE_CANDIDATE",1,3840,2088,0,b,"UNKNOWN");
            Reject(()=>new OneShot().Submit(check,()=>{m.EnsureCurrent(n with{Dpi=192});down++;return 2;},()=>{},()=>{up++;return 0;}));Check(down==0&&up==1);
        });
        test("geometry: diagnostic snapshots remain serializable without pixels",()=>{
            var evidence=new CaptureEvidence("after_pull",true,false,s.Visible,s.Visible,true,false,1920,1080,1920,1080,BeforeGeometry:s,AfterGeometry:s with{Dpi=192});
            using var json=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(evidence));Check(json.RootElement.GetProperty("BeforeGeometry").GetProperty("Dpi").GetUInt32()==144 && json.RootElement.GetProperty("AfterGeometry").GetProperty("Dpi").GetUInt32()==192);
        });
        test("geometry: 2160 coordinates map but existing click guard remains closed",()=>{
            var n=new WindowSnapshot(new(0,0,3840,2160),new(0,0,3840,2160),new(0,0,3840,2160),96,true,true);var m=new WindowFrameMap(n,n,3840,2160,3840,2160,3840L*2160*4);var b=new Box(3500,2070,3572,2136);
            Check(m.ScreenCenter(b,n)==new Pointer(3536,2103));Check(ClickPolicy.Refusal(new(true,true,true,true,false,false,"INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED","UNIQUE_CANDIDATE",1,3840,2160,0,b,"UNKNOWN"))=="GEOMETRY_NOT_VERIFIED");});
    }
}
