using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;

// Three owned slots, one rejected-entry crop per capture session. No full frames.
internal sealed class EntryEvidenceStore(string directory)
{
    bool recorded;
    internal static Int32Rect CropFor(Jx3.OneClickTest.Box icon,int width,int height,double scale)
    {
        if(!WindowFramePolicy.SizeSupported(width,height)||!double.IsFinite(scale)||scale<.35||scale>2||icon.Width<=0||icon.Height<=0||icon.X<0||icon.Y<0||icon.X2>width||icon.Y2>height||icon.Width>320||icon.Height>180)throw new InvalidDataException("Invalid entry diagnostic geometry");
        int w=Math.Min(width,Math.Max(icon.Width,Math.Min(320,(int)Math.Round(270*scale))));
        int h=Math.Min(height,Math.Max(icon.Height,Math.Min(180,(int)Math.Round(130*scale))));
        int left=(int)Math.Round((w-icon.Width)*170d/198),top=(int)Math.Round((h-icon.Height)*20d/64);
        return new(Math.Clamp(icon.X-left,0,width-w),Math.Clamp(icon.Y-top,0,height-h),w,h);
    }
    internal string? Record(byte[] bgra,int width,int height,Int32Rect crop,string recognition,double source)
    {
        if(recorded)return null;
        if(!WindowFramePolicy.SizeSupported(width,height)||bgra.LongLength!=(long)width*height*4||crop.X<0||crop.Y<0||crop.Width<=0||crop.Height<=0||crop.Width>320||crop.Height>180||(long)crop.X+crop.Width>width||(long)crop.Y+crop.Height>height)throw new InvalidDataException("Invalid entry evidence crop");
        Directory.CreateDirectory(directory);string owner=Path.Combine(directory,".owner");
        if(File.Exists(owner)){if(File.ReadAllText(owner)!="jx3-entry-evidence-v1")throw new IOException("Entry evidence directory not owned");}
        else{if(Directory.EnumerateFileSystemEntries(directory).Any())throw new IOException("Entry evidence directory not empty");File.WriteAllText(owner,"jx3-entry-evidence-v1");}
        var slots=Enumerable.Range(0,3).Select(i=>Path.Combine(directory,$"entry-{i}.png")).ToArray();var image=slots.FirstOrDefault(p=>!File.Exists(p))??slots.OrderBy(File.GetLastWriteTimeUtc).First();
        var pixels=new byte[crop.Width*crop.Height*4];for(int row=0;row<crop.Height;row++)Buffer.BlockCopy(bgra,((crop.Y+row)*width+crop.X)*4,pixels,row*crop.Width*4,crop.Width*4);
        var frame=BitmapSource.Create(crop.Width,crop.Height,96,96,PixelFormats.Bgra32,null,pixels,crop.Width*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(frame));
        using var buffer=new MemoryStream();encoder.Save(buffer);var bytes=buffer.ToArray();
        var metadata=JsonSerializer.Serialize(new{utc=DateTime.UtcNow,source,frameWidth=width,frameHeight=height,crop,imageSha256=Convert.ToHexString(SHA256.HashData(bytes)),recognition=JsonSerializer.Deserialize<JsonElement>(recognition)});
        File.WriteAllBytes(image,bytes);File.WriteAllText(Path.ChangeExtension(image,".json"),metadata);recorded=true;return image;
    }
}
