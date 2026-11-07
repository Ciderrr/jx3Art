using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;
internal static class AppIcon
{
    internal static string? SelectedPath;
    internal static string FilePath=>SelectedPath??Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.ico");
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint LoadImageW(nint instance,string file,uint type,int cx,int cy,uint flags);
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern uint ExtractIconExW(string file,int index,[Out]nint[]? large,[Out]nint[]? small,uint count);
    [DllImport("user32.dll")]static extern nint SendMessageW(nint window,uint message,nint wParam,nint lParam);
    internal static BitmapFrame WindowSource()=>BitmapFrame.Create(new Uri(FilePath),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
    internal static nint LoadNative(int size){nint icon=LoadImageW(0,FilePath,1,size,size,0x10);return icon!=0?icon:throw new IOException("助手图标资源未能加载，请保留完整程序目录。");}
    internal static void Release(nint icon){if(icon!=0)DestroyIcon(icon);}
    static byte[] Pixels(nint icon){var source=Imaging.CreateBitmapSourceFromHIcon(icon,System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);byte[] pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);return pixels;}
    internal static bool WindowMatchesSource(nint window,out int selectedSize){selectedSize=0;nint actual=SendMessageW(window,0x7f,1,0);if(actual==0)return false;selectedSize=Imaging.CreateBitmapSourceFromHIcon(actual,System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()).PixelWidth;nint expected=LoadNative(selectedSize);try{return Pixels(actual).SequenceEqual(Pixels(expected));}finally{Release(expected);}}
    internal static void AddTests(Action<string,Action> test)
    {
        test("icon choices exclude opera persist choice and normalize bounded local uploads",()=>{
            string root=Path.Combine(Path.GetTempPath(),"JX3Icon-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{if(IconChoices.Ids.Length!=6||IconChoices.Ids.Contains("04-liyuan")||new UiPreferences().appIcon!="01-yunxiu")throw new Exception("icon catalog");foreach(string id in IconChoices.Ids){string path=IconChoices.Resolve(root,id);if(!File.Exists(path)||!IconChoices.Preview(path).StartsWith("data:image/png;base64,"))throw new Exception("missing icon");var pref=new UiPreferences(appIcon:id);pref.Save(Path.Combine(root,"settings.json"));if(UiPreferences.Load(Path.Combine(root,"settings.json")).appIcon!=id)throw new Exception("choice not persisted");}foreach(string id in new[]{"../bad","04-liyuan","custom-../x"}){try{IconChoices.Resolve(root,id);throw new Exception("unsafe icon accepted");}catch(ArgumentException){}}string custom=IconChoices.Import(root,Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon-64.png"));if(new IconBitmapDecoder(new Uri(IconChoices.Resolve(root,custom)),BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames.Count!=9)throw new Exception("upload size ladder");File.WriteAllText(Path.Combine(root,"bad.png"),"bad");try{IconChoices.Import(root,Path.Combine(root,"bad.png"));throw new Exception("bad image accepted");}catch(Exception e)when(e.Message!="bad image accepted"){} }finally{foreach(string f in Directory.GetFiles(root,"*",SearchOption.AllDirectories))File.Delete(f);foreach(string d in Directory.GetDirectories(root))Directory.Delete(d);Directory.Delete(root);}
        });
        test("app icon has a complete size ladder and the compiled EXE matches the tray source",()=>{
            var decoder=new IconBitmapDecoder(new Uri(FilePath),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);int[] sizes={16,20,24,32,40,48,64,128,256};if(!sizes.All(size=>decoder.Frames.Any(f=>f.PixelWidth==size&&f.PixelHeight==size)))throw new Exception("ICO missing size");
            nint fileIcon=0;nint[] large={0},small={0};try{if(ExtractIconExW(Path.Combine(AppContext.BaseDirectory,"JX3Artist.exe"),0,large,null,1)!=1||ExtractIconExW(Path.Combine(AppContext.BaseDirectory,"JX3Artist.exe"),0,null,small,1)!=1||large[0]==0||small[0]==0)throw new Exception("EXE icon resource absent");int selectedSize=Imaging.CreateBitmapSourceFromHIcon(large[0],System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()).PixelWidth;fileIcon=LoadNative(selectedSize);if(!Pixels(fileIcon).SequenceEqual(Pixels(large[0])))throw new Exception($"EXE and tray source differ at {selectedSize}px");}finally{Release(fileIcon);Release(large[0]);Release(small[0]);}
        });
    }
}
