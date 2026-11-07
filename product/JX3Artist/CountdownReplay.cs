using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;

internal static class CountdownReplay
{
    // This command accepts saved files only: it never constructs GameSession/Desktop/WPF windows.
    internal static int Run(string[] args)
    {
        string Value(string name)=>args[Array.IndexOf(args,name)+1];
        string list=Value("--frames"),output=Value("--out");var reader=new CountdownReader();var rows=new List<object>();int failed=0;
        bool crops=args.Contains("--timer-crops");byte[]? canvas=crops?new byte[3840*2088*4]:null;
        int dx=args.Contains("--dx")?int.Parse(Value("--dx")):0,dy=args.Contains("--dy")?int.Parse(Value("--dy")):0;
        if(crops && (dx!=0 || dy!=0))throw new ArgumentException("native crop replay is already normalized to canonical coordinates");
        foreach(var line in File.ReadLines(list).Where(s=>!string.IsNullOrWhiteSpace(s)))
        {
            var fields=line.Split('\t');string path=fields[0];int? expected=fields[1]=="UNKNOWN"?null:int.Parse(fields[1]);
            using var stream=File.OpenRead(path);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var source=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);int w=source.PixelWidth,h=source.PixelHeight;
            byte[] pixels=new byte[w*h*4];source.CopyPixels(pixels,w*4,0);
            if(crops)
            {
                if(w!=102 || h!=50)throw new ArgumentException("timer crop adapter requires original 102x50 crop");
                for(int y=0;y<50;y++)Buffer.BlockCopy(pixels,y*102*4,canvas!,((1455+y)*3840+1898)*4,102*4);
                pixels=canvas!;w=3840;h=2088;
            }
            var reading=reader.Read(pixels,w,h,CancellationToken.None,dx,dy).GetAwaiter().GetResult();bool passed=reading.Seconds==expected;if(!passed)failed++;
            rows.Add(new{path,expected,reading,passed});Console.WriteLine($"{Path.GetFileName(path)} expected={expected} actual={reading.Seconds} [{reading.Raw}] {reading.Reason}");
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{scope=crops?"saved native timer ROIs embedded in a blank 4K canvas; same production reader; no game eligibility/action claims":"saved-frame local OCR; no live capture or input",failed,rows},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:9;
    }
}
