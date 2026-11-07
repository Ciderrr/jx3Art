using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Int1;
namespace JX3Artist;
internal static class BitmapTransportTests
{
 internal static void Add(Action<string,Action> test)
 {
  void Check(bool value){if(!value)throw new Exception("bitmap transfer assertion failed");}
  void Scratch(Action<string> action)
  {
   string root=Path.Combine(Path.GetTempPath(),"JX3Artist-bmp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   try{action(root);}finally
   {
    // Only the flat files/subdirectories created by this isolated test.
    foreach(string dir in Directory.GetDirectories(root)){foreach(string file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);}
    foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);
   }
  }
  test("BMP preserves top-down BGR pixels, odd widths and arbitrary unused alpha",()=>Scratch(root=>
  {
   foreach(var(w,h) in new[]{(1,1),(2,3),(17,11),(1284,767),(2560,1440),(3840,2160)})
   {
    byte[] source=new byte[w*h*4];new Random(w+h).NextBytes(source);byte[] before=(byte[])source.Clone();string path=Path.Combine(root,$"{w}x{h}.bmp");
    BitmapFrameTransport.SaveBmp(source,w,h,path);
    using var stream=File.OpenRead(path);var decoded=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
    Check(decoded.PixelWidth==w&&decoded.PixelHeight==h);var bitmap=new FormatConvertedBitmap(decoded,PixelFormats.Bgra32,null,0);byte[] actual=new byte[source.Length];bitmap.CopyPixels(actual,w*4,0);
    for(int p=0;p<source.Length;p+=4)Check(source[p]==actual[p]&&source[p+1]==actual[p+1]&&source[p+2]==actual[p+2]);
    Check(source.AsSpan().SequenceEqual(before));
   }
  }));
  test("BMP refuses invalid buffers and overflow before file creation",()=>Scratch(root=>
  {
   string target=Path.Combine(root,"invalid.bmp");int rejected=0;
   foreach(var(w,h,data) in new[]{(2,3,new byte[3]),(0,3,Array.Empty<byte>()),(-1,3,Array.Empty<byte>()),(int.MaxValue,2,Array.Empty<byte>())})
   {try{BitmapFrameTransport.SaveBmp(data,w,h,target);}catch(ArgumentException){rejected++;}catch(OverflowException){rejected++;}}
   Check(rejected==4&&!File.Exists(target));
  }));
  test("BMP CreateNew failure never overwrites or deletes an existing file",()=>Scratch(root=>
  {
   string target=Path.Combine(root,"existing.bmp");File.WriteAllText(target,"sentinel");bool refused=false;
   try{BitmapFrameTransport.SaveBmp(new byte[24],2,3,target);}catch(IOException){refused=true;}
   Check(refused&&File.ReadAllText(target)=="sentinel");
  }));
  test("BMP failed staging does not own an existing directory and can recover",()=>Scratch(root=>
  {
   var transport=new BitmapFrameTransport(root);string occupied=Path.Combine(transport.DirectoryPath,"staging.bmp");Directory.CreateDirectory(occupied);bool refused=false;
   try{transport.Stage(new byte[24],2,3,1);}catch(UnauthorizedAccessException){refused=true;}catch(IOException){refused=true;}
   Check(refused&&Directory.Exists(occupied));Directory.Delete(occupied);
   using var lease=transport.Stage(new byte[24],2,3,2);Check(File.Exists(lease.KeepLatest()));
  }));
  test("BMP locked previous frame blocks a third snapshot without losing latest",()=>Scratch(root=>
  {
   var transport=new BitmapFrameTransport(root);using(var first=transport.Stage(new byte[24],2,3,1))first.KeepLatest();bool failed=false,blocked=false;
   using(var locked=new FileStream(transport.LastSnapshotPath,FileMode.Open,FileAccess.Read,FileShare.None))
   {
    using var second=transport.Stage(new byte[24],2,3,2);try{second.KeepLatest();}catch(IOException){failed=true;}
    try{transport.Stage(new byte[24],2,3,3);}catch(IOException){blocked=true;}
    Check(failed&&blocked&&File.Exists(transport.LastSnapshotPath)&&Directory.GetFiles(transport.DirectoryPath).Length==2);
   }
  }));
 }
}
