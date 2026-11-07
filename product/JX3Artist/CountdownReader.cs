using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace JX3Artist;

internal sealed record CountdownReading(int? Seconds,string Quality,string Raw,string Reason)
{
    internal static CountdownReading Unknown(string reason,string raw="")=>new(null,"UNKNOWN",raw,reason);
}

// Independent numeric reader. The frozen D2 detector and its coordinates are unchanged.
// Geometry scope is deliberately limited to original 3840x2088 / original UI size.
internal sealed class CountdownReader
{
    OcrEngine? engine,fallbackEngine;bool initialized,fallbackInitialized;
    internal static int? Parse(string text)
    {
        // Windows OCR can split adjacent digit tokens. Remove horizontal spacing only;
        // never repair glyphs, punctuation, missing digits, or merge multiple text lines.
        string normalized=Regex.Replace(text.Trim(),@"[\p{Zs}\t]+","");
        var match=Regex.Match(normalized,@"\A([0-9]{2})[:：]([0-9]{2})\z",RegexOptions.CultureInvariant);
        if(!match.Success)return null;
        int minutes=int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture),seconds=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
        return seconds<60?minutes*60+seconds:null;
    }
    internal static bool Eligible(Observation o)=>o.Identity && o.Geometry && o.D2=="VISIBLE" && o.Location=="NO_VERIFIED_CANDIDATE" && o.Count==0;
    internal static string SearchText(IEnumerable<string> lines,string fallback)
    {
        var valid=lines.Where(line=>Parse(line).HasValue).ToArray();return valid.Length>1?"__AMBIGUOUS_TIME_LINES__":valid.Length==1?valid[0]:fallback;
    }
    internal static CountdownReading Merge(CountdownReading first,CountdownReading second)
    {
        if(first.Reason is "ocr_valid_passes_conflict" or "ocr_ambiguous_time_lines" or "cross_region_conflict")return first;
        var values=first.Raw.Split(" | ").Select(Parse).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
        if(second.Seconds.HasValue && values.Any(value=>value!=second.Seconds.Value))return CountdownReading.Unknown("cross_region_conflict",first.Raw+" | "+second.Raw);
        return second with{Raw=first.Raw+" | "+second.Raw};
    }
    internal static CountdownReading Combine(IEnumerable<string> passes,string language)
    {
        string[] text=passes.ToArray();if(text.Contains("__AMBIGUOUS_TIME_LINES__"))return CountdownReading.Unknown("ocr_ambiguous_time_lines",string.Join(" | ",text));int[] values=text.Select(Parse).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
        string raw=string.Join(" | ",text);
        return values.Length>=2 && values.Distinct().Count()==1?
            new(values[0],"DUAL_PASS_AGREEMENT",raw,$"consistent_valid_passes={values.Length}/{text.Length};language={language};not_probability"):
            CountdownReading.Unknown(values.Distinct().Count()>1?"ocr_valid_passes_conflict":"ocr_insufficient_valid_passes",raw);
    }
    internal async Task<CountdownReading> Read(byte[] bgra,int width,int height,CancellationToken token,int dx=0,int dy=0)
    {
        token.ThrowIfCancellationRequested();
        if(width!=3840 || height!=2088 || bgra.Length!=width*height*4)return CountdownReading.Unknown("unsupported_geometry");
        if((long)1912+dx<0 || (long)1470+dy<0 || (long)1980+dx>width || (long)1495+dy>height)return CountdownReading.Unknown("timer_out_of_frame");
        return await ReadRegion(bgra,width,height,new Jx3.OneClickTest.Box(1912+dx,1470+dy,1980+dx,1495+dy),token).ConfigureAwait(false);
    }
    internal async Task<CountdownReading> ReadRegion(byte[] bgra,int width,int height,Jx3.OneClickTest.Box region,CancellationToken token,bool search=false)
    {
        token.ThrowIfCancellationRequested();
        if(width<=0||height<=0||(long)width*height>32000000||bgra.LongLength!=(long)width*height*4||region.Width<12||region.Width>(search?512:256)||region.Height<5||region.Height>(search?256:96)||region.X<0||region.Y<0||region.X2>width||region.Y2>height)return CountdownReading.Unknown("timer_out_of_frame");
        try
        {
            if(!initialized)
            {
                initialized=true;
                engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))??OcrEngine.TryCreateFromLanguage(new Language("en-US"))??OcrEngine.TryCreateFromUserProfileLanguages();
            }
            if(engine==null)return CountdownReading.Unknown("local_windows_ocr_unavailable");
            // Tight text crop excludes the decorative frame that confused punctuation OCR.
            int h=search?Math.Clamp(region.Height,25,128):25;int w=search?(int)Math.Clamp(Math.Round((double)h*region.Width/region.Height),34,256):68;
            async Task<string> Pass(OcrEngine recognizer,int mode,int scale,Jx3.OneClickTest.Box? crop=null)
            {
                // Only the timer crop is enlarged, not the full 4K frame.
                var sample=crop??region;
                byte[] pixels=new byte[w*h*scale*scale*4];int stride=w*scale*4;
                for(int row=0;row<h*scale;row++)for(int col=0;col<w*scale;col++)
                {
                    int p=((sample.Y+(row/scale)*sample.Height/h)*width+sample.X+(col/scale)*sample.Width/w)*4,d=row*stride+col*4;
                    byte b=bgra[p],g=bgra[p+1],r=bgra[p+2];
                    if(mode==1){byte v=(byte)(255-((r*77+g*150+b*29)>>8));b=g=r=v;}
                    if(mode==2){byte v=Math.Min(r,Math.Min(g,b))>=155?(byte)0:(byte)255;b=g=r=v;}
                    pixels[d]=b;pixels[d+1]=g;pixels[d+2]=r;pixels[d+3]=255;
                }
                using var bitmap=new SoftwareBitmap(BitmapPixelFormat.Bgra8,w*scale,h*scale,BitmapAlphaMode.Ignore);
                bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(pixels));
                var result=await recognizer.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
                if(!search)return result.Text;
                return SearchText(result.Lines.Select(line=>line.Text),result.Text);
            }
            var passes=new List<string>{await Pass(engine,0,4).ConfigureAwait(false),await Pass(engine,1,4).ConfigureAwait(false),
                await Pass(engine,2,3).ConfigureAwait(false),await Pass(engine,2,4).ConfigureAwait(false)};
            // All syntactically valid results must agree. No majority override of a conflict.
            string language=engine.RecognizerLanguage.LanguageTag;var reading=Combine(passes,language);
            if(search && reading.Reason=="ocr_insufficient_valid_passes" && w*6<=OcrEngine.MaxImageDimension && h*6<=OcrEngine.MaxImageDimension)
            {passes.Add(await Pass(engine,0,6).ConfigureAwait(false));passes.Add(await Pass(engine,1,6).ConfigureAwait(false));passes.Add(await Pass(engine,2,6).ConfigureAwait(false));reading=Combine(passes,language);}
            // Small physical glyphs can straddle rounded mapping edges. Extra
            // pixel evidence is allowed; never repair missing OCR digits/text.
            Jx3.OneClickTest.Box? expanded=region.Width<68?new(Math.Max(0,region.X-1),Math.Max(0,region.Y-1),Math.Min(width,region.X2+2),Math.Min(height,region.Y2+1)):null;
            if(expanded!=null && reading.Reason=="ocr_insufficient_valid_passes")
            {
                passes.Add(await Pass(engine,0,4,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,1,4,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,2,3,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,2,4,expanded).ConfigureAwait(false));reading=Combine(passes,language);
            }
            // Preserve clear-background English recognition when the local Chinese model
            // yields too few complete tokens. Conflicts never trigger a majority fallback.
            if(reading.Seconds==null && reading.Reason=="ocr_insufficient_valid_passes" && language!="en-US")
            {
                if(!fallbackInitialized){fallbackInitialized=true;fallbackEngine=OcrEngine.TryCreateFromLanguage(new Language("en-US"));}
                if(fallbackEngine!=null)
                {
                    passes.Add(await Pass(fallbackEngine,0,4).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,1,4).ConfigureAwait(false));
                    passes.Add(await Pass(fallbackEngine,2,3).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,2,4).ConfigureAwait(false));
                    if(expanded!=null){passes.Add(await Pass(fallbackEngine,0,4,expanded).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,1,4,expanded).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,2,3,expanded).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,2,4,expanded).ConfigureAwait(false));}
                    if(search && w*6<=OcrEngine.MaxImageDimension && h*6<=OcrEngine.MaxImageDimension){passes.Add(await Pass(fallbackEngine,0,6).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,1,6).ConfigureAwait(false));passes.Add(await Pass(fallbackEngine,2,6).ConfigureAwait(false));}
                    reading=Combine(passes,language+"+en-US");
                }
            }
            return reading;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){return CountdownReading.Unknown("ocr_error:"+ex.GetType().Name);}
    }
}
