using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
namespace JX3Artist;

// Saved-file investigation only. It does not select a production recognition result.
internal static class CountdownOcrDiagnostics
{
    internal static int Run(string[] args)
    {
        string Value(string n)=>args[Array.IndexOf(args,n)+1];
        string file=Value("--frame");using var input=File.OpenRead(file);
        var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var source=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);
        int width=source.PixelWidth,height=source.PixelHeight;byte[] bgra=new byte[width*height*4];source.CopyPixels(bgra,width*4,0);
        bool crop=width==102 && height==50;if(!crop && (width!=3840 || height!=2088))throw new ArgumentException("unsupported saved image");
        var rows=new List<object>();var languages=OcrEngine.AvailableRecognizerLanguages.Select(x=>x.LanguageTag).ToArray();
        foreach(string language in languages)
        {
            var engine=OcrEngine.TryCreateFromLanguage(new Language(language));if(engine==null)continue;
            foreach(var rect in new[]{(1912,1470,68,25),(1908,1467,78,32),(1898,1455,102,50)})
            foreach(int scale in new[]{3,4})foreach(string mode in new[]{"raw","invert","binary"})
            {
                var (rx,ry,w,h)=rect;int x=rx-(crop?1898:0),y=ry-(crop?1455:0);byte[] pixels=new byte[w*h*scale*scale*4];
                for(int row=0;row<h*scale;row++)for(int col=0;col<w*scale;col++)
                {
                    int p=((y+row/scale)*width+x+col/scale)*4,d=(row*w*scale+col)*4;
                    byte b=bgra[p],g=bgra[p+1],r=bgra[p+2];
                    if(mode=="invert"){byte v=(byte)(255-((r*77+g*150+b*29)>>8));b=g=r=v;}
                    if(mode=="binary"){byte v=Math.Min(r,Math.Min(g,b))>=155?(byte)0:(byte)255;b=g=r=v;}
                    pixels[d]=b;pixels[d+1]=g;pixels[d+2]=r;pixels[d+3]=255;
                }
                using var bitmap=new SoftwareBitmap(BitmapPixelFormat.Bgra8,w*scale,h*scale,BitmapAlphaMode.Ignore);bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(pixels));
                var result=engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                rows.Add(new{language,rx,ry,w,h,scale,mode,result.Text,parsed=CountdownReader.Parse(result.Text),words=result.Lines.SelectMany(l=>l.Words).Select(q=>new{q.Text,x=q.BoundingRect.X,y=q.BoundingRect.Y,w=q.BoundingRect.Width,h=q.BoundingRect.Height}).ToArray()});
            }
        }
        File.WriteAllText(Value("--out"),JsonSerializer.Serialize(new{scope="saved-image OCR variants, not production decisions",file,languages,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Saved OCR diagnostic variants={rows.Count}, languages={string.Join(',',languages)}");return 0;
    }
}
