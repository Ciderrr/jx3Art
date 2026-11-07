using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace JX3Artist;

internal sealed class Journal(string folder,Action<string>? onCloseError=null,Action<string,object>? observer=null):IDisposable
{
    StreamWriter? writer;bool ownerChecked;
    internal void Write(string kind,object data)
    {
        try{observer?.Invoke(kind,data);}catch{} // Diagnostics cannot grant or block input.
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,"events.jsonl");
        if(!ownerChecked)
        {
            string marker=Path.Combine(folder,".owner");
            if(!File.Exists(marker)){if(Directory.EnumerateFileSystemEntries(folder).Any())throw new IOException("日志目录存在其他内容，未覆盖");File.WriteAllText(marker,"JX3ArtistLogV1");}
            if(File.ReadAllText(marker)!="JX3ArtistLogV1")throw new IOException("日志目录归属校验失败");ownerChecked=true;
        }
        if(writer!=null && writer.BaseStream.Length>2*1024*1024)
        {writer.Dispose();writer=null;string old=path+".2";if(File.Exists(old))File.Delete(old);if(File.Exists(path+".1"))File.Move(path+".1",old);File.Move(path,path+".1");}
        writer??=new StreamWriter(path,true){AutoFlush=true};writer.WriteLine(JsonSerializer.Serialize(new{utc=DateTime.UtcNow,kind,data}));
    }
    public void Dispose(){try{writer?.Dispose();}catch(Exception ex){onCloseError?.Invoke(ex.Message);}}
}
internal static class Cache
{
    internal static void Prepare(string folder)
    {
        string root=Path.GetFullPath(folder);Directory.CreateDirectory(root);string marker=Path.Combine(root,".owner");
        if(!File.Exists(marker))
        {if(Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("缓存目录存在非本软件内容，未清理");File.WriteAllText(marker,"JX3ArtistCacheV1");}
        if(File.ReadAllText(marker)!="JX3ArtistCacheV1")throw new IOException("缓存归属校验失败");
        foreach(string directory in Directory.EnumerateDirectories(root))
        {
            if(!Regex.IsMatch(Path.GetFileName(directory),@"^transport-[a-f0-9]{32}$"))continue;
            string full=Path.GetFullPath(directory);if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("缓存路径越界");
            foreach(string file in Directory.EnumerateFiles(full))
                if(Regex.IsMatch(Path.GetFileName(file),@"^(frame-\d{6}|staging)\.bmp$"))File.Delete(file);
            if(!Directory.EnumerateFileSystemEntries(full).Any())Directory.Delete(full,false);
        }
        if(Directory.EnumerateFiles(root,"*.bmp",SearchOption.AllDirectories).Any())throw new IOException("旧缓存被占用，未创建更多快照");
    }
}
internal sealed class SessionTrace(Journal journal)
{
    bool started,ended;
    internal void Begin(object data){if(started)throw new InvalidOperationException("session already started");journal.Write("session_started",data);started=true;}
    internal void End(object data){if(!started || ended)return;journal.Write("session_stopped",data);ended=true;}
}
