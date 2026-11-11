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
    internal static int? Parse(string text,bool singleMinute=false)
    {
        // Windows OCR can split adjacent digit tokens. Remove horizontal spacing only;
        // never repair glyphs, punctuation, missing digits, or merge multiple text lines.
        string normalized=Regex.Replace(text.Trim(),@"[\p{Zs}\t]+","");
        var pattern=singleMinute?@"\A([0-9])[:：]([0-9]{2})\z":@"\A([0-9]{2})[:：]([0-9]{2})\z";
        var match=Regex.Match(normalized,pattern,RegexOptions.CultureInvariant);
        if(!match.Success)return null;
        int minutes=int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture),seconds=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
        return seconds<60?minutes*60+seconds:null;
    }
    internal static bool Eligible(Observation o)=>o.Identity && o.Geometry && o.D2=="VISIBLE" && o.Location=="NO_VERIFIED_CANDIDATE" && o.Count==0;
    internal static string SearchText(IEnumerable<string> lines,string fallback,bool singleMinute=false)
    {
        var valid=lines.Where(line=>Parse(line,singleMinute).HasValue).ToArray();return valid.Length>1?"__AMBIGUOUS_TIME_LINES__":valid.Length==1?valid[0]:fallback;
    }
    internal static CountdownReading Merge(CountdownReading first,CountdownReading second)
    {
        if(first.Reason is "ocr_valid_passes_conflict" or "ocr_ambiguous_time_lines" or "cross_region_conflict")return first;
        var values=first.Raw.Split(" | ").Select(text=>Parse(text)).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
        if(second.Seconds.HasValue && values.Any(value=>value!=second.Seconds.Value))return CountdownReading.Unknown("cross_region_conflict",first.Raw+" | "+second.Raw);
        return second with{Raw=first.Raw+" | "+second.Raw};
    }
    internal static CountdownReading Combine(IEnumerable<string> passes,string language,bool singleMinute=false)
    {
        string[] text=passes.ToArray();if(text.Contains("__AMBIGUOUS_TIME_LINES__"))return CountdownReading.Unknown("ocr_ambiguous_time_lines",string.Join(" | ",text));int[] values=text.Select(value=>Parse(value,singleMinute)).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
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
    internal async Task<CountdownReading> ReadWujieRegion(byte[] bgra,int width,int height,Jx3.OneClickTest.Box region,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(width<=0||height<=0||(long)width*height>32000000||bgra.LongLength!=(long)width*height*4||
            region.X<0||region.Y<0||region.X2>width||region.Y2>height||region.X2<=region.X||region.Y2<=region.Y||
            region.Width is <12 or >160||region.Height is <5 or >64)
            return CountdownReading.Unknown("timer_out_of_frame");

        // Keep the native glyph proportions. The original flagship path resizes
        // to its historical text box and remains unchanged.
        int w=region.Width+8,h=region.Height+8;
        var mask=new byte[w*h*4];int greenPixels=0;
        for(int y=0;y<region.Height;y++)
        for(int x=0;x<region.Width;x++)
        {
            int source=((region.Y+y)*width+region.X+x)*4;
            int b=bgra[source],g=bgra[source+1],r=bgra[source+2];
            if(g<150||g-Math.Max(r,b)<35)continue;
            int target=((y+4)*w+x+4)*4;
            mask[target]=mask[target+1]=mask[target+2]=255;greenPixels++;
        }
        if(greenPixels==0)return CountdownReading.Unknown("wujie_green_text_missing");
        var spans=WujieGlyphSpans(mask,w,h);
        bool singleMinute=spans.Count==4&&spans[0].Start is >=8 and <=21&&
            spans[1].End-spans[1].Start is >=2 and <=7&&spans[1].Start-spans[0].Start is >=12 and <=23;
        if(spans.Count==4&&!singleMinute)return CountdownReading.Unknown("wujie_timer_layout_unconfirmed");
        var native=await ReadRegion(mask,w,h,new(0,0,w,h),token,search:true,nativePixels:true,singleMinute:singleMinute).ConfigureAwait(false);
        return native.Seconds is int seconds&&!WujieStrokesAgree(mask,w,h,seconds,spans,singleMinute)
            ?CountdownReading.Unknown("wujie_digit_shape_conflict",native.Raw):native;
    }

    static List<(int Start,int End)> WujieGlyphSpans(byte[] mask,int width,int height)
    {
        var columns=new bool[width];
        for(int x=0;x<width;x++)
        for(int y=0;y<height;y++)columns[x]|=mask[(y*width+x)*4]!=0;
        var spans=new List<(int Start,int End)>(5);
        for(int x=0;x<width;x++)
        {
            if(!columns[x])continue;
            int start=x;
            while(x+1<width&&columns[x+1])x++;
            spans.Add((start,x+1));
        }
        return spans;
    }

    // A native-font 3 can read as 5 in every OCR pass. Reject this pair when
    // the upper stroke is on the wrong side, without substituting any digit.
    static bool WujieStrokesAgree(byte[] mask,int width,int height,int seconds,List<(int Start,int End)> spans,bool singleMinute)
    {
        int[] digits=singleMinute?[seconds/60,-1,seconds%60/10,seconds%10]:[seconds/600,seconds/60%10,-1,seconds%60/10,seconds%10];
        if(!digits.Any(digit=>digit is 3 or 5))return true;
        bool Ink(int x,int y)=>mask[(y*width+x)*4]!=0;
        if(spans.Count!=digits.Length)return false;
        for(int index=0;index<digits.Length;index++)
        {
            int digit=digits[index];
            if(digit is not (3 or 5))continue;
            var (left,right)=spans[index];
            int top=height,bottom=-1;
            for(int x=left;x<right;x++)
            for(int y=0;y<height;y++)
                if(Ink(x,y)){top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            int glyphHeight=bottom-top+1;
            if(right-left<8||glyphHeight<12)return false;
            int middle=(left+right)/2,leftInk=0,rightInk=0;
            for(int y=top+glyphHeight/5;y<top+glyphHeight*2/5;y++)
            for(int x=left;x<right;x++)
            {
                if(!Ink(x,y))continue;
                if(x<middle)leftInk++;
                else rightInk++;
            }
            if(digit==3?rightInk<leftInk*2+3:leftInk<rightInk*2+3)return false;
        }
        return true;
    }

    internal async Task<CountdownReading> ReadRegion(byte[] bgra,int width,int height,Jx3.OneClickTest.Box region,CancellationToken token,bool search=false,bool nativePixels=false,bool singleMinute=false)
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
            int h=nativePixels?region.Height:search?Math.Clamp(region.Height,25,128):25;
            int w=nativePixels?region.Width:search?(int)Math.Clamp(Math.Round((double)h*region.Width/region.Height),34,256):68;
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
                return SearchText(result.Lines.Select(line=>line.Text),result.Text,singleMinute);
            }
            var passes=new List<string>{await Pass(engine,0,4).ConfigureAwait(false),await Pass(engine,1,4).ConfigureAwait(false),
                await Pass(engine,2,3).ConfigureAwait(false),await Pass(engine,2,4).ConfigureAwait(false)};
            // All syntactically valid results must agree. No majority override of a conflict.
            string language=engine.RecognizerLanguage.LanguageTag;var reading=Combine(passes,language,singleMinute);
            if(search && reading.Reason=="ocr_insufficient_valid_passes" && w*6<=OcrEngine.MaxImageDimension && h*6<=OcrEngine.MaxImageDimension)
            {passes.Add(await Pass(engine,0,6).ConfigureAwait(false));passes.Add(await Pass(engine,1,6).ConfigureAwait(false));passes.Add(await Pass(engine,2,6).ConfigureAwait(false));reading=Combine(passes,language,singleMinute);}
            // Small physical glyphs can straddle rounded mapping edges. Extra
            // pixel evidence is allowed; never repair missing OCR digits/text.
            Jx3.OneClickTest.Box? expanded=region.Width<68?new(Math.Max(0,region.X-1),Math.Max(0,region.Y-1),Math.Min(width,region.X2+2),Math.Min(height,region.Y2+1)):null;
            if(expanded!=null && reading.Reason=="ocr_insufficient_valid_passes")
            {
                passes.Add(await Pass(engine,0,4,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,1,4,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,2,3,expanded).ConfigureAwait(false));passes.Add(await Pass(engine,2,4,expanded).ConfigureAwait(false));reading=Combine(passes,language,singleMinute);
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
                    reading=Combine(passes,language+"+en-US",singleMinute);
                }
            }
            return reading;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){return CountdownReading.Unknown("ocr_error:"+ex.GetType().Name);}
    }
}
