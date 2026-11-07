using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
namespace JX3Artist;
internal static class TimerEvidenceTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Assert(bool yes){if(!yes)throw new Exception("timer evidence assertion");}
        var pixels=new byte[800*600*4];var crop=new Int32Rect(200,100,200,60);
        test("timer evidence is cropped hashed and source annotated",()=>{
            var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));try{var path=new TimerEvidenceStore(dir).Record(pixels,800,600,crop,"timer_text_region_unresolved","{}","",1000)!;using var stream=File.OpenRead(path);var image=BitmapDecoder.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);Assert(image.Frames[0].PixelWidth==200&&image.Frames[0].PixelHeight==60);using var meta=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,".json")));Assert(meta.RootElement.GetProperty("source").GetDouble()==1000&&meta.RootElement.GetProperty("imageSha256").GetString()==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));}finally{Directory.Delete(dir,true);}
        });
        test("timer evidence minute throttle reserves first failure and bounds four slots",()=>{
            var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));try{var store=new TimerEvidenceStore(dir);store.Record(pixels,800,600,crop,"first","{}","",0);Assert(store.Record(pixels,800,600,crop,"other","{}","",59999)==null);for(int i=1;i<9;i++)store.Record(pixels,800,600,crop,"latest","{}","",i*60000);Assert(Directory.GetFiles(dir,"*.png").Length==4&&Directory.GetFiles(dir,"*.json").Length==4);using var first=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"timer-0.json")));Assert(first.RootElement.GetProperty("reason").GetString()=="first");}finally{Directory.Delete(dir,true);}
        });
        test("timer evidence preserves unowned directory",()=>{
            var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"user.txt"),"keep");try{try{new TimerEvidenceStore(dir).Record(pixels,800,600,crop,"failure","{}","",0);throw new Exception("unowned accepted");}catch(IOException){}Assert(File.ReadAllText(Path.Combine(dir,"user.txt"))=="keep"&&!File.Exists(Path.Combine(dir,".owner")));}finally{Directory.Delete(dir,true);}
        });
        test("timer evidence rejects full frame overflow and oversized crop",()=>{
            foreach(var rect in new[]{new Int32Rect(0,0,800,600),new Int32Rect(790,0,200,60),new Int32Rect(0,0,600,100),new Int32Rect(0,0,0,0)}){try{new TimerEvidenceStore("not-used").Record(pixels,800,600,rect,"failure","{}","",0);throw new Exception("invalid accepted");}catch(InvalidDataException){}}
        });
    }
}
