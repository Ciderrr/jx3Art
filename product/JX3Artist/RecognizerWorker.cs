using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Jx3.OneClickTest;
namespace JX3Artist;

// Sequential current-frame requests only; no cached recognition or input APIs.
internal sealed class RecognizerWorker(string script):IDisposable
{
    Process? process;int sequence,uses;
    internal string Scheduling{get;private set;}="not_started";
    internal string Read(string path,Box content,double? hint,CancellationToken cancellation,bool entryPatch=false)
    {
        cancellation.ThrowIfCancellationRequested();
        try{
            if(process==null || process.HasExited || uses>=128){Dispose();var start=RecognizerProcess.ForFrame(script,path,content,hint);start.ArgumentList.Clear();start.ArgumentList.Add("-u");start.ArgumentList.Add("-B");start.ArgumentList.Add(script);start.ArgumentList.Add("--server");start.RedirectStandardInput=true;process=Process.Start(start)??throw new IOException("无法启动识别进程");Scheduling="python_policy_pending";uses=0;}
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(entryPatch?400:8000);
            int id=++sequence;
            string request=JsonSerializer.Serialize(new{id,frame=Path.GetFullPath(path),content=new[]{content.X,content.Y,content.X2,content.Y2},hint,entryPatch});
            process.StandardInput.WriteLineAsync(request.AsMemory(),timeout.Token).GetAwaiter().GetResult();process.StandardInput.FlushAsync(timeout.Token).GetAwaiter().GetResult();
            string line=process.StandardOutput.ReadLineAsync(timeout.Token).GetAwaiter().GetResult()??throw new IOException("识别进程未返回当前帧");
            if(line.Length>1048576)throw new IOException("识别回包超出预算");
            using var response=JsonDocument.Parse(line);if(response.RootElement.GetProperty("id").GetInt32()!=id)throw new IOException("识别回包帧号不符");
            if(response.RootElement.TryGetProperty("workerScheduling",out var policy))Scheduling=policy.GetRawText();
            uses++;return response.RootElement.GetProperty("result").GetRawText();
        }catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){Dispose();throw new TimeoutException("图像识别超时，未继续点击");}
        catch{Dispose();throw;}
    }
    public void Dispose(){if(process!=null){try{if(!process.HasExited){process.Kill(true);process.WaitForExit(3000);}}finally{process.Dispose();process=null;}}}
}
