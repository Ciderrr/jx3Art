using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
namespace JX3Artist;
internal static class EntryEvidenceTests
{
    internal static void Add(Action<string,Action> test)
    {
        var pixels=new byte[3840*2088*4];var crop=new Int32Rect(20,30,270,130);
        test("entry diagnostic crop covers independent scales and frame borders within budget",()=>{
            foreach(double scale in new[]{.35,1d,2d})foreach(var point in new[]{(0,0),(1100,500)}){
                var icon=new Jx3.OneClickTest.Box(point.Item1,point.Item2,point.Item1+(int)Math.Round(72*scale),point.Item2+(int)Math.Round(66*scale));var rect=EntryEvidenceStore.CropFor(icon,1280,720,scale);
                if(rect.Width>320||rect.Height>180||rect.X<0||rect.Y<0||rect.X+rect.Width>1280||rect.Y+rect.Height>720||rect.X>icon.X||rect.Y>icon.Y||rect.X+rect.Width<icon.X2||rect.Y+rect.Height<icon.Y2)throw new Exception("diagnostic crop excludes current target or exceeds budget");
            }
            var original=EntryEvidenceStore.CropFor(new(200,100,272,166),3840,2088,1);if(original!=new Int32Rect(30,80,270,130))throw new Exception("canonical diagnostic region changed");
            try{EntryEvidenceStore.CropFor(new(1270,700,1300,740),1280,720,1);throw new Exception("invalid target cropped");}catch(InvalidDataException){}
        });
        test("entry evidence supports current maximized and ordinary-window frame sizes",()=>{
            foreach(var size in new[]{(3840,2160),(1920,1080),(1280,720),(2560,1440)}){
                var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
                try{var path=new EntryEvidenceStore(root).Record(new byte[size.Item1*size.Item2*4],size.Item1,size.Item2,crop,"{}",123)!;using var json=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,".json")));if(json.RootElement.GetProperty("frameHeight").GetInt32()!=size.Item2)throw new Exception("source dimensions lost");}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
            }
        });
        test("entry evidence captures only local crop once per session",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
            try{var store=new EntryEvidenceStore(root);var path=store.Record(pixels,3840,2088,crop,"{\"entry_context\":{\"confirmed\":false}}",123)!;using(var stream=File.OpenRead(path)){var image=BitmapDecoder.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);if(image.Frames[0].PixelWidth!=270||image.Frames[0].PixelHeight!=130)throw new Exception("not cropped");}if(store.Record(pixels,3840,2088,crop,"{}",124)!=null||Directory.GetFiles(root,"*.png").Length!=1)throw new Exception("repeated write");using var json=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,".json")));if(json.RootElement.GetProperty("source").GetDouble()!=123)throw new Exception("wrong source");}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        });
        test("entry evidence retains at most three slots across sessions",()=>{var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));try{for(int i=0;i<5;i++)new EntryEvidenceStore(root).Record(pixels,3840,2088,crop,"{}",i);if(Directory.GetFiles(root,"*.png").Length!=3||Directory.GetFiles(root,"*.json").Length!=3)throw new Exception("unbounded");}finally{if(Directory.Exists(root))Directory.Delete(root,true);}});
        test("entry evidence refuses unowned directory and preserves user files",()=>{var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"notes.txt"),"keep");try{try{new EntryEvidenceStore(root).Record(pixels,3840,2088,crop,"{}",0);throw new Exception("unowned accepted");}catch(IOException){}if(File.ReadAllText(Path.Combine(root,"notes.txt"))!="keep"||File.Exists(Path.Combine(root,".owner")))throw new Exception("user data changed");}finally{Directory.Delete(root,true);}});
        test("entry evidence rejects full frame and invalid crop",()=>{foreach(var rect in new[]{new Int32Rect(0,0,3840,2088),new Int32Rect(3830,0,270,130),new Int32Rect(0,0,0,0)}){try{new EntryEvidenceStore("not-used").Record(pixels,3840,2088,rect,"{}",0);throw new Exception("invalid crop accepted");}catch(InvalidDataException){}}});
    }
}
