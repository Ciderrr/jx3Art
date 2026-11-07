using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jx3.OneClickTest;
namespace JX3Artist;
// Saved-image diagnostic: synthetic geometry, never a live click permission.
internal static class WindowReplay
{
    internal static int Run(string[] args)
    {
        string Value(string key){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException(key);}
        string path=Path.GetFullPath(Value("--frame")),output=Path.GetFullPath(Value("--out"));Directory.CreateDirectory(output);
        using var input=File.OpenRead(path);var decoded=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];var bitmap=new FormatConvertedBitmap(decoded,PixelFormats.Bgra32,null,0);
        int w=bitmap.PixelWidth,h=bitmap.PixelHeight;if((long)w*h>32000000)throw new IOException("frame budget");var bgra=new byte[checked(w*h*4)];bitmap.CopyPixels(bgra,w*4,0);
        int at=Array.IndexOf(args,"--content");int[] rect=at<0?new[]{0,0,w,h}:args.Skip(at+1).Take(4).Select(int.Parse).ToArray();if(rect.Length!=4)throw new ArgumentException("content");var content=new Box(rect[0],rect[1],rect[2],rect[3]);
        var snapshot=new WindowSnapshot(new(0,0,w,h),new(0,0,w,h),content,96,false,true);var map=new WindowFrameMap(snapshot,snapshot,w,h,w,h,bgra.LongLength);
        var start=RecognizerProcess.ForFrame(Path.Combine(AppContext.BaseDirectory,"engine/diagnostics/one_click/locate.py"),path,content);using var child=System.Diagnostics.Process.Start(start)??throw new IOException("recognizer unavailable");var textTask=child.StandardOutput.ReadToEndAsync();var errorTask=child.StandardError.ReadToEndAsync();if(!child.WaitForExit(12000)){child.Kill(true);child.WaitForExit();throw new TimeoutException("replay timeout");}if(child.ExitCode!=0)throw new IOException(errorTask.GetAwaiter().GetResult());string text=textTask.GetAwaiter().GetResult();
        var tight=WindowFramePolicy.Validate(text,map);bool forceSearch=args.Contains("--search-text");var region=forceSearch?WindowFramePolicy.SearchRegion(text,map):tight??WindowFramePolicy.SearchRegion(text,map);var observation=GameSession.ReadObservation(text,1000,1100,true,true,path,out _);CountdownReading? reading=null;
        if(region!=null && CountdownReader.Eligible(observation))reading=new CountdownReader().ReadRegion(bgra,w,h,region,CancellationToken.None,search:forceSearch||tight==null).GetAwaiter().GetResult();
        if(!forceSearch&&tight!=null&&reading?.Reason=="ocr_insufficient_valid_passes")
        {var search=WindowFramePolicy.SearchRegion(text,map);if(search!=null&&search!=tight)reading=CountdownReader.Merge(reading,new CountdownReader().ReadRegion(bgra,w,h,search,CancellationToken.None,search:true).GetAwaiter().GetResult());}
        int expectedAt=Array.IndexOf(args,"--expected-seconds");int? expected=expectedAt>=0?int.Parse(args[expectedAt+1]):null;bool passed=observation.D2=="VISIBLE"&&(!expected.HasValue||reading?.Seconds==expected);
        File.WriteAllText(Path.Combine(output,"recognition.json"),text);File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed,expectedSeconds=expected,scope="saved screenshot replay; supplied content and synthetic mapping, no live geometry or input",actionsDispatched=0,uiScale=WindowFramePolicy.ReadScale(text),observation.D2,observation.PanelLocation,timerRegion=region,reading},new JsonSerializerOptions{WriteIndented=true}));return passed?0:9;
    }
}
