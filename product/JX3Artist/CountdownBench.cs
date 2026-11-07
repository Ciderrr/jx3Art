using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace JX3Artist;
internal static class CountdownBench
{
    internal static int Run(string[] args)
    {
        string Value(string name)=>args[Array.IndexOf(args,name)+1];
        using var stream=File.OpenRead(Value("--frame"));var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var source=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);int w=source.PixelWidth,h=source.PixelHeight;
        byte[] pixels=new byte[w*h*4];source.CopyPixels(pixels,w*4,0);
        using var process=Process.GetCurrentProcess();process.Refresh();long baselineWs=process.WorkingSet64,baselinePrivate=process.PrivateMemorySize64;
        var reader=new CountdownReader();var rows=new List<object>();var durations=new List<double>();var cpuBefore=process.TotalProcessorTime;var total=Stopwatch.StartNew();bool passed=true;
        for(int i=0;i<10;i++)
        {
            var time=Stopwatch.StartNew();var r=reader.Read(pixels,w,h,CancellationToken.None).GetAwaiter().GetResult();time.Stop();process.Refresh();
            passed&=r.Seconds==1792;durations.Add(time.Elapsed.TotalMilliseconds);
            rows.Add(new{i,ms=time.Elapsed.TotalMilliseconds,r.Seconds,workingSetMiB=process.WorkingSet64/1048576d,privateMiB=process.PrivateMemorySize64/1048576d});
        }
        total.Stop();process.Refresh();var warm=durations.Skip(1).OrderBy(x=>x).ToArray();
        var report=new{scope="offline OCR only, one decoded saved 4K panel reused ten times; includes WPF decoder; no WGC/GPU/live/input; not product total or long-run memory proof",passed,baselineWorkingSetMiB=baselineWs/1048576d,baselinePrivateMiB=baselinePrivate/1048576d,finalWorkingSetMiB=process.WorkingSet64/1048576d,finalPrivateMiB=process.PrivateMemorySize64/1048576d,peakWorkingSetMiB=process.PeakWorkingSet64/1048576d,coldMs=durations[0],warmMedianMs=warm[warm.Length/2],totalMs=total.Elapsed.TotalMilliseconds,processCpuMs=(process.TotalProcessorTime-cpuBefore).TotalMilliseconds,rows};
        File.WriteAllText(Value("--out"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"OCR benchmark passed={passed} cold={durations[0]:0.0}ms warmMedian={warm[warm.Length/2]:0.0}ms baselineWS={baselineWs/1048576d:0.0}MiB finalWS={process.WorkingSet64/1048576d:0.0}MiB");return passed?0:9;
    }
}
