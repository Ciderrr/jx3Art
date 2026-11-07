using System.IO;
using System.Text.Json;
namespace Int1;

internal static class ImageTransportTests
{
    private sealed class Scratch : IDisposable
    {
        public readonly string Root;
        public Scratch(string parent) { Root=Path.Combine(parent,"case-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            // The test created these exact flat directories; no recursive delete or user paths.
            foreach(var child in Directory.GetDirectories(Root))
            { foreach(var file in Directory.GetFiles(child)) File.Delete(file);Directory.Delete(child); }
            foreach(var file in Directory.GetFiles(Root))File.Delete(file);
            Directory.Delete(Root);
        }
    }
    public static List<Program.CaseResult> Run(string parent,PythonBridge bridge,string sourceFrame)
    {
        var cases=new List<Program.CaseResult>();
        void Check(string name,string expect,string actual,bool pass)=>cases.Add(new(){Name="transport: "+name,Expect=expect,Actual=actual,Pass=pass});
        byte[] Small(int w,int h) {var data=new byte[w*h*4];new Random(w*1000+h).NextBytes(data);return data;}
        using var probe=new ImageTransportBench.DecodeProbe();
        foreach(var (w,h) in new[]{(1,1),(2,3),(17,11),(100,7)})
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);byte[] data=Small(w,h);
            using var lease=transport.Stage(data,w,h,1);string path=lease.KeepLatest();using var decoded=probe.Decode(path);
            var root=decoded.RootElement;
            Check($"independent-bgr-pixels-{w}x{h}","OpenCV BGR digest exactly matches source",root.GetProperty("bgr_sha256").GetString(),
                root.GetProperty("ok").GetBoolean()&&root.GetProperty("width").GetInt32()==w&&root.GetProperty("height").GetInt32()==h
                &&root.GetProperty("bgr_sha256").GetString()==ImageTransportBench.BgrHash(data));
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);byte[] data=Small(2,3);bool bounded=true;
            for(int i=1;i<=8;i++)
            {
                string previous=transport.LastSnapshotPath;
                using var lease=transport.Stage(data,2,3,i);
                bounded &= Directory.GetFiles(transport.DirectoryPath,"*.bmp").Length <= 2;
                string current=lease.KeepLatest();
                bounded &= File.Exists(current)&&Directory.GetFiles(transport.DirectoryPath,"*.bmp").Length==1&&(previous==null||!File.Exists(previous));
            }
            Check("eight-frames-bounded-snapshots","one retained, at most two during staging",$"bounded={bounded}",bounded);
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);using var lease=transport.Stage(Small(2,3),2,3,1);bool refused=false;
            try{using var second=transport.Stage(Small(2,3),2,3,2);}catch(IOException){refused=true;}
            lease.Dispose();lease.Dispose();bool closed=false;try{lease.KeepLatest();}catch(InvalidOperationException){closed=true;}
            Check("active-lease-and-idempotent-release","second stage refused; pending removed; closed cannot publish",$"refused={refused};closed={closed}",
                refused&&closed&&Directory.GetFiles(transport.DirectoryPath).Length==0);
        }
        {
            using var scratch=new Scratch(parent);string existing=Path.Combine(scratch.Root,"existing.bmp");File.WriteAllText(existing,"sentinel");bool refused=false;
            try{BitmapFrameTransport.SaveBmp(Small(2,3),2,3,existing);}catch(IOException){refused=true;}
            Check("existing-file-not-overwritten","CreateNew preserves existing bytes",$"refused={refused}",refused&&File.ReadAllText(existing)=="sentinel");
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);int rejected=0;
            try{transport.Stage(new byte[3],2,3,1);}catch(ArgumentException){rejected++;}
            try{transport.Stage(Array.Empty<byte>(),-1,3,1);}catch(ArgumentException){rejected++;}
            try{transport.Stage(Small(2,3),2,3,0);}catch(ArgumentOutOfRangeException){rejected++;}
            Check("bad-input-no-files","three invalid requests rejected before file write",$"rejected={rejected}",rejected==3&&Directory.GetFiles(transport.DirectoryPath).Length==0);
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);byte[] data=Small(2,3);
            using(var first=transport.Stage(data,2,3,1))first.KeepLatest();
            bool cleanupFailed=false,blocked=false;
            using(var locked=new FileStream(transport.LastSnapshotPath,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                using var second=transport.Stage(data,2,3,2);
                try{second.KeepLatest();}catch(IOException){cleanupFailed=true;}
                try{transport.Stage(data,2,3,3);}catch(IOException){blocked=true;}
                Check("locked-snapshot-no-third-image","failed cleanup blocks growth",$"cleanupFailed={cleanupFailed};blocked={blocked}",
                    cleanupFailed&&blocked&&Directory.GetFiles(transport.DirectoryPath,"*.bmp").Length==2);
            }
        }
        byte[] full=new byte[MonitorCore.ExpectedW*MonitorCore.ExpectedH*4];
        PythonBridge.BridgeResult Fake(string path,bool? ready,int id,int timeout)=>new(){Ok=true,Document=JsonDocument.Parse($"{{\"ok\":true,\"id\":{id},\"d1\":{{\"state\":\"VISIBLE\"}},\"d2\":{{\"state\":\"NOT_VISIBLE\"}}}}")};
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);var gate=new ContentGate();gate.Set(true,"test:true");int calls=0;
            var core=new MonitorCore(null,gate,scratch.Root,detect:(path,ready,id,timeout)=>{calls++;return Fake(path,ready,id,timeout);},transport:transport);
            core.ProcessFrame(full,3840,2088,true,null,out var seed,render:false);bool had=seed!=null;
            File.WriteAllText(Path.Combine(transport.DirectoryPath,"staging.bmp"),"sentinel");
            core.ProcessFrame(full,3840,2088,true,null,out var after,render:false);
            Check("production-transfer-error-clears-previous-result","failed staging cannot hold prior VISIBLE",$"had={had};status={core.CaptureStatus};detects={calls}",
                had&&after==null&&core.LastResult==null&&!core.CanPublish&&core.CaptureStatus=="FRAME_TRANSFER_FAILED"&&calls==1
                &&File.ReadAllText(Path.Combine(transport.DirectoryPath,"staging.bmp"))=="sentinel");
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);var gate=new ContentGate();gate.Set(true,"test:true");int reads=0,calls=0;
            var core=new MonitorCore(null,gate,scratch.Root,()=>++reads>=3?13001:10000,
                (path,ready,id,timeout)=>{calls++;return Fake(path,ready,id,timeout);},transport);
            core.ProcessFrame(full,3840,2088,true,null,out var result,new FrameTiming(10000,10000,DateTime.UtcNow),false);
            Check("encoding-expiry-before-bridge","STALE and no dispatch; pending file removed",$"{core.CaptureStatus};calls={calls}",
                result==null&&core.CaptureStatus=="STALE"&&core.LastResult==null&&calls==0&&Directory.GetFiles(transport.DirectoryPath).Length==0&&core.LastTiming.SourceQpcMs==10000);
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);var gate=new ContentGate();gate.Set(true,"test:true");double clock=10000;
            var core=new MonitorCore(null,gate,scratch.Root,()=>clock,(path,ready,id,timeout)=>{clock=13001;return Fake(path,ready,id,timeout);},transport);
            core.ProcessFrame(full,3840,2088,true,null,out var result,new FrameTiming(10000,10000,DateTime.UtcNow),false);
            Check("post-bridge-expiry-preserved","snapshot is diagnostic only; result STALE",core.CaptureStatus,
                result==null&&core.CaptureStatus=="STALE"&&core.LastResult==null&&core.LastSnapshotPath!=null&&File.Exists(core.LastSnapshotPath));
        }
        {
            using var scratch=new Scratch(parent);var transport=new BitmapFrameTransport(scratch.Root);File.WriteAllText(Path.Combine(transport.DirectoryPath,"frame-000001.bmp"),"sentinel");
            var gate=new ContentGate();gate.Set(true,"test:true");var core=new MonitorCore(null,gate,scratch.Root,detect:Fake,transport:transport);
            core.ProcessFrame(full,3840,2088,true,null,out var result,render:false);
            Check("snapshot-publish-error-fails-closed","no overwrite and no accepted result",core.CaptureStatus,
                result==null&&core.LastResult==null&&core.CaptureStatus=="FRAME_TRANSFER_FAILED"&&core.LastSnapshotPath==null
                &&File.ReadAllText(Path.Combine(transport.DirectoryPath,"frame-000001.bmp"))=="sentinel"&&Directory.GetFiles(transport.DirectoryPath).Length==1);
        }
        {
            using var scratch=new Scratch(parent);string source=Path.GetFullPath(sourceFrame);
            var(data,w,h)=ImageTransportBench.DecodeSource(source);var transport=new BitmapFrameTransport(scratch.Root);
            using var lease=transport.Stage(data,w,h,1);string path=lease.KeepLatest();using var decoded=probe.Decode(path);
            Check("full-size-bounded-file-pixel-equality","3840x2088 OpenCV digest equals original",decoded.RootElement.GetProperty("bgr_sha256").GetString(),
                decoded.RootElement.GetProperty("bgr_sha256").GetString()==ImageTransportBench.BgrHash(data));
            string png=Path.Combine(scratch.Root,"reference.png");MonitorCore.SavePng(data,w,h,png);
            var a=bridge.Detect(png,true,810001,15000);var b=bridge.Detect(path,true,810002,15000);
            string Features(JsonDocument document,string key)=>JsonSerializer.Serialize(document.RootElement.GetProperty(key).EnumerateObject().Where(p=>p.Name!="detect_ms").OrderBy(p=>p.Name).ToDictionary(p=>p.Name,p=>p.Value.Clone()));
            bool equal=a.Ok&&b.Ok&&Features(a.Document,"d1")==Features(b.Document,"d1")&&Features(a.Document,"d2")==Features(b.Document,"d2");
            Check("real-bridge-png-bmp-feature-equivalence","all D1/D2 fields except timing equal",$"pngOk={a.Ok};bmpOk={b.Ok};equal={equal}",equal);
        }
        return cases;
    }
}
