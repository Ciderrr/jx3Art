using System.Text.Json;
namespace JX3Artist;

internal sealed record WebUiCommand(long Id,long Epoch,string Type,JsonElement Payload);
internal static class WebUiProtocol
{
    internal const string Origin="https://jx3artist.local";
    internal static readonly HashSet<string> Commands=new(StringComparer.Ordinal){"checkUpdate","createShortcut","setIcon","uploadIcon","requestExperienceReset","cancelExperienceReset","confirmExperienceReset","minimize","maximize","closeWindow","ready","heartbeat","uiContext","capture","requestStart","confirmStart","cancelPreparation","uiFailure","stop","approve","setAutomatic","setPlan","setReminder","setSkin","setHotkeys","copyEmail","openProfile","openIssues","previewReport","saveReport","records"};
    internal static bool Trusted(string source)=>Uri.TryCreate(source,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Host=="jx3artist.local"&&u.IsDefaultPort&&u.AbsolutePath=="/index.html"&&string.IsNullOrEmpty(u.Query)&&string.IsNullOrEmpty(u.Fragment)&&string.IsNullOrEmpty(u.UserInfo);
    internal static WebUiCommand Parse(string source,string json)
    {
        if(!Trusted(source)||json.Length>16384)throw new ArgumentException("不受信任的界面请求");
        using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=12});var r=doc.RootElement;
        if(r.GetProperty("schema").GetInt32()!=1||!r.TryGetProperty("id",out var id)||!id.TryGetInt64(out long value)||value<=0||value>9007199254740991)throw new ArgumentException("请求编号无效");
        string type=r.GetProperty("type").GetString()??"";if(!Commands.Contains(type))throw new ArgumentException("未知界面操作");
        long epoch=r.GetProperty("epoch").GetInt64();var payload=r.TryGetProperty("payload",out var p)?p.Clone():JsonSerializer.SerializeToElement(new{});
        if(payload.ValueKind!=JsonValueKind.Object)throw new ArgumentException("操作参数无效");return new(value,epoch,type,payload);
    }
    internal static bool RequiresCurrentEpoch(string type)=>type is "requestExperienceReset" or "cancelExperienceReset" or "confirmExperienceReset" or "requestStart" or "confirmStart" or "approve" or "uiContext" or "capture";
    internal static bool Boolean(JsonElement p,string key)=>p.GetProperty(key).ValueKind switch{JsonValueKind.True=>true,JsonValueKind.False=>false,_=>throw new ArgumentException("选项必须为布尔值")};
    internal static int Plan(JsonElement p){int value=p.GetProperty("limit").GetInt32();if(value<0)throw new ArgumentException("计划必须为正整数或持续运行");return value;}
    internal static void AddTests(Action<string,Action> test)
    {
        test("web UI protocol rejects foreign origins malformed ids and generic native calls",()=>{
            string good="{\"schema\":1,\"id\":1,\"epoch\":0,\"type\":\"stop\",\"payload\":{}}";
            if(Parse(Origin+"/index.html",good).Type!="stop")throw new Exception();
            foreach(var pair in new[]{("https://jx3artist.local.evil/index.html",good),(Origin+"/other.html",good),(Origin+"/index.html",good.Replace("\"stop\"","\"sendInput\"")),(Origin+"/index.html",good.Replace("\"id\":1","\"id\":0")),(Origin+"/index.html",good.Replace("\"id\":1","\"id\":9007199254740992"))}){
                try{Parse(pair.Item1,pair.Item2);}catch(Exception){continue;}throw new Exception("untrusted request accepted");}
        });
        test("web UI protocol validates boolean and bounded plan types",()=>{
            foreach(string raw in new[]{"{\"limit\":-1}","{\"limit\":2147483648}","{\"limit\":1.1}"}){using var doc=JsonDocument.Parse(raw);try{Plan(doc.RootElement);}catch(Exception){continue;}throw new Exception("bad plan accepted");}
            using var valid=JsonDocument.Parse("{\"limit\":0,\"enabled\":false}");if(Plan(valid.RootElement)!=0||Boolean(valid.RootElement,"enabled"))throw new Exception();
        });
    }
}
