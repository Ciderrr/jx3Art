using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;
// Four owned PNG/JSON slots; first failure retained, three rolling latest.
internal sealed class TimerEvidenceStore(string directory)
{
    double last=double.NegativeInfinity;
    internal string? Record(byte[] pixels,int width,int height,Int32Rect crop,string reason,string recognition,string raw,double source)
    {
        if(!double.IsFinite(source)||source<=last||source-last<60000)return null;
        if(width<=0||height<=0||(long)width*height>32000000||pixels.LongLength!=(long)width*height*4||crop.X<0||crop.Y<0||crop.Width<1||crop.Height<1||crop.Width>512||crop.Height>256||crop.Width==width&&crop.Height==height||(long)crop.X+crop.Width>width||(long)crop.Y+crop.Height>height)throw new InvalidDataException("invalid timer crop");
        Directory.CreateDirectory(directory);var owner=Path.Combine(directory,".owner");
        if(File.Exists(owner)){if(File.ReadAllText(owner)!="jx3-timer-evidence-v1")throw new IOException("timer evidence ownership");}
        else{if(Directory.EnumerateFileSystemEntries(directory).Any())throw new IOException("timer evidence directory occupied");File.WriteAllText(owner,"jx3-timer-evidence-v1");}
        string first=Path.Combine(directory,"timer-0.png");var rolling=Enumerable.Range(1,3).Select(i=>Path.Combine(directory,$"timer-{i}.png")).ToArray();string path=!File.Exists(first)?first:rolling.FirstOrDefault(p=>!File.Exists(p))??rolling.OrderBy(File.GetLastWriteTimeUtc).First();
        var small=new byte[crop.Width*crop.Height*4];for(int row=0;row<crop.Height;row++)Buffer.BlockCopy(pixels,((crop.Y+row)*width+crop.X)*4,small,row*crop.Width*4,crop.Width*4);
        var image=BitmapSource.Create(crop.Width,crop.Height,96,96,PixelFormats.Bgra32,null,small,crop.Width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var buffer=new MemoryStream();encoder.Save(buffer);byte[] data=buffer.ToArray();
        string meta=JsonSerializer.Serialize(new{utc=DateTime.UtcNow,source,reason,width,height,crop,raw,imageSha256=Convert.ToHexString(SHA256.HashData(data)),recognition=JsonSerializer.Deserialize<JsonElement>(recognition)});
        File.WriteAllBytes(path,data);File.WriteAllText(Path.ChangeExtension(path,".json"),meta);last=source;return path;
    }
}
