using System.Diagnostics;
namespace JX3Artist;

internal static class RecognizerProcess
{
    internal static ProcessStartInfo ForFrame(string script,string path,Jx3.OneClickTest.Box? content=null,double? scaleHint=null)
    {
        var start=new ProcessStartInfo("py"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        // NumPy initializes OpenBLAS at import. Cap only this short-lived child;
        // no global/user environment changes, no need for a large BLAS thread pool.
        start.Environment["OPENBLAS_NUM_THREADS"]="1";
        start.ArgumentList.Add("-B");start.ArgumentList.Add(script);start.ArgumentList.Add("--frame");start.ArgumentList.Add(path);
        if(content!=null){start.ArgumentList.Add("--portable");start.ArgumentList.Add("--content");foreach(int value in new[]{content.X,content.Y,content.X2,content.Y2})start.ArgumentList.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));}
        if(scaleHint.HasValue){start.ArgumentList.Add("--scale-hint");start.ArgumentList.Add(scaleHint.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));}
        return start;
    }
}
