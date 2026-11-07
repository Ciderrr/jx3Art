using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;
internal static class IconChoices
{
    internal static readonly string[] Ids={"01-yunxiu","02-jiangshan","03-qingshang","05-fuxian","06-huadeng","retained"};
    internal static string Resolve(string root,string id){if(Ids.Contains(id))return Path.Combine(AppContext.BaseDirectory,"Assets","Icons",id+".ico");if(id.StartsWith("custom-",StringComparison.Ordinal)&&Guid.TryParseExact(id[7..],"N",out _))return Path.Combine(root,"icons",id+".ico");throw new ArgumentException("未知图标");}
    internal static string Preview(string file){var decoder=new IconBitmapDecoder(new Uri(file),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var frame=decoder.Frames.OrderBy(f=>Math.Abs(f.PixelWidth-64)).First();var png=new PngBitmapEncoder();png.Frames.Add(frame);using var bytes=new MemoryStream();png.Save(bytes);return "data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray());}
    internal static string Import(string root,string source){
        if(new FileInfo(source).Length>8*1024*1024)throw new ArgumentException("请选择8MB以内的PNG、JPG或ICO图标。");
        var decoder=BitmapDecoder.Create(new Uri(source),BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames.OrderByDescending(f=>f.PixelWidth).First();if(frame.PixelWidth<1||frame.PixelHeight<1||frame.PixelWidth>4096||frame.PixelHeight>4096)throw new ArgumentException("图片尺寸须在1至4096像素之间。");
        var id="custom-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.Combine(root,"icons"));string target=Resolve(root,id),temp=target+".tmp";int[] sizes={16,20,24,32,40,48,64,128,256};var images=new List<byte[]>();
        foreach(int size in sizes){var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){double ratio=Math.Min((double)size/frame.PixelWidth,(double)size/frame.PixelHeight);double w=frame.PixelWidth*ratio,h=frame.PixelHeight*ratio;dc.DrawImage(frame,new Rect((size-w)/2,(size-h)/2,w,h));}var bitmap=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var bytes=new MemoryStream();encoder.Save(bytes);images.Add(bytes.ToArray());}
        try{using(var output=new BinaryWriter(File.Create(temp))){output.Write((ushort)0);output.Write((ushort)1);output.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((ushort)0);output.Write((ushort)1);output.Write((ushort)32);output.Write(images[i].Length);output.Write(offset);offset+=images[i].Length;}foreach(var image in images)output.Write(image);}File.Move(temp,target);}finally{if(File.Exists(temp))File.Delete(temp);}return id;
    }
    internal static void SyncShortcut(string file,string? newTarget=null){string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"剑网3 艺人助手.lnk");if(!File.Exists(path))return;var type=Type.GetTypeFromProgID("WScript.Shell");if(type==null)return;dynamic shell=Activator.CreateInstance(type)!;dynamic link=shell.CreateShortcut(path);if(!string.Equals((string)link.TargetPath,Path.Combine(AppContext.BaseDirectory,"JX3Artist.exe"),StringComparison.OrdinalIgnoreCase))return;if(newTarget!=null){link.TargetPath=newTarget;link.WorkingDirectory=Path.GetDirectoryName(newTarget)!;}link.IconLocation=file+",0";link.Save();}
}
