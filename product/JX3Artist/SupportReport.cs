using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace JX3Artist;
// A projection of known structured fields, never a copy of raw logs or cache.
internal static class SupportReport
{
    static readonly HashSet<string> ContextEvents=new(new[]{"app_open","app_close","start_requested","stop_requested","approval_requested","mode_changed","settings_changed","problem_detected","observation_context","resource_sample","experience_status"});
    internal static bool Sanitize(string kind,JsonElement data,out JsonElement safe)
    {safe=default;if(!Events.Contains(kind)&&!ContextEvents.Contains(kind))return false;safe=JsonSerializer.SerializeToElement(Project(data));return true;}
    internal static bool KnownCode(string? code)=>code is "JX3-START-001" or "JX3-FOCUS-001" or "JX3-INPUT-001" or "JX3-PERMISSION-001" or "JX3-WINDOW-001" or "JX3-UNKNOWN-001";
    static readonly HashSet<string> Events=new(new[]{"session_started","session_stopped","state","paused","capture_failed","takeover_result","start_feedback","start_feedback_sample","startup_countdown","timer_diagnostic","click_intent","takeover_intent","entry_rejection_evidence","observation_cycle","takeover_deferred","takeover_pixel_prepare","entry_fast_foreground","preparation_foreground_request","observation_schedule","countdown_status","panel_scale_recovery"});
    static readonly HashSet<string> Numbers=new(new[]{"Starts","Returns","maxStarts","sourceAgeMs","foregroundBeforeClickMs","CountdownSeconds","Count","PullMapMs","TransportMs","RecognitionMs","ValidationOcrEvidenceMs","TotalMs","processingMs","ElapsedMs","failures","index","source","Source","Now","NowQpcMs","LastSource","AppliedAt","appliedAt","EndSourceMs","Dpi","BodyAgreement","LowerAgreement","MaskPixels","ClockCoreScore","ClockSearchWidth","ClockSearchHeight","Radius","Colour","Coverage","Precision","LowerColour","LowerCoverage","LowerPrecision","LegacyAgreement","RecoveryAttempts","OriginalPanelScale","SelectedPanelScale","width","height","visualScale","ExperienceEstimatedGain","ExperienceLifetimeGain","ExperienceObservedMs","ExperienceSaveErrors","workingSetBytes","privateBytes","cpuSeconds","handles","threads"});
    static readonly HashSet<string> Booleans=new(new[]{"Started","Restored","ControlTransferred","Deferred","cancelled","unattended","Identity","Geometry","EntryContextConfirmed","adopted","automaticTransaction","Confirmed","accepted","success","win32Zoomed","Requested","Activated","TimerEvidenceSaved","TimerEvidenceFailed","EntryEvidenceSaved","EntryEvidenceFailed","ClockRecovered","ExperienceHasUnobservedTime","ExperienceSaveFailure","ExperienceLifetimePartial"});
    static readonly HashSet<string> Enums=new(new[]{"Location","D2","CountdownQuality","PanelLocation","feedbackState","state","phase","origin","RecoveryStatus","Stage","TimerEvidenceFailure","EntryEvidenceFailure"});
    static readonly HashSet<string> Containers=new(new[]{"Metrics","samples","evidence","Restoration","result","PanelVerification","Diagnostics","SmallPanelEvidence"});
    static readonly HashSet<string> Reasons=new(new[]{"PIXEL_MAP_NOT_VERIFIED","TARGET_IDENTITY_LOST","WINDOW_MINIMIZED","FRAME_UNAVAILABLE","FRAME_POOL_RECREATED","WINDOW_BOUNDS_UNAVAILABLE","WINDOW_BOUNDS_CHANGED","WINDOW_SIZE_UNSUPPORTED","SURFACE_SIZE_UNSUPPORTED","CONTENT_SIZE_UNSUPPORTED","FOREGROUND_CHANGED","INPUT_TICK_CHANGED","CURSOR_MOVED","RELEASE_NOT_CONFIRMED","ORIGINAL_WINDOW_GONE","ORIGINAL_ACTIVATION_FAILED","POINTER_RESTORE_FAILED","RESTORE_CHECK_FAILED"});
    static readonly HashSet<string> Symbols=new(new[]{"UNIQUE_CANDIDATE","NO_VERIFIED_CANDIDATE","AMBIGUOUS","SEARCH_LIMIT","SCALE_CONFLICT","NOT_EVALUABLE","UNRESOLVED_PANEL_CANDIDATE","NO_VERIFIED_PANEL","UNIQUE_PANEL","NOT_CHECKED","VISIBLE","NOT_VISIBLE","UNKNOWN","DUAL_PASS_AGREEMENT","StartFeedbackObserved","FeedbackInsufficient","TimedOut","FeedbackUnknown","AwaitingFeedback","Cancelled","Aborted","InputRejected","action","watching","PAINTED_BODY_UNCONFIRMED","CLOCK_UNCONFIRMED","CONFIRMED","UNCONFIRMED","CONFLICT","BUDGET_LIMIT","button","shortcut","window_close","ui_error","preparation_revoked","INVALID_DATA","ACCESS_DENIED","IO_ERROR"});
    static object Project(JsonElement value,int depth=0)
    {
        var result=new Dictionary<string,object?>();if(value.ValueKind!=JsonValueKind.Object||depth>3)return result;
        foreach(var field in value.EnumerateObject()){
            var v=field.Value;
            if(Numbers.Contains(field.Name)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out double n)&&double.IsFinite(n))result[field.Name]=n;
            else if(Booleans.Contains(field.Name)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False)result[field.Name]=v.GetBoolean();
            else if(Enums.Contains(field.Name)&&v.ValueKind==JsonValueKind.String){string text=v.GetString()!;if(Enum.TryParse<Phase>(text,out var phase)&&Enum.IsDefined(phase)||Symbols.Contains(text))result[field.Name]=text;}
            else if(field.Name is "reasons" or "Reasons"&&v.ValueKind==JsonValueKind.Array)result[field.Name]=v.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String&&Reasons.Contains(x.GetString()!)).Take(12).Select(x=>x.GetString()).ToArray();
            else if(Containers.Contains(field.Name)&&depth<3){if(v.ValueKind==JsonValueKind.Object)result[field.Name]=Project(v,depth+1);else if(v.ValueKind==JsonValueKind.Array)result[field.Name]=v.EnumerateArray().Take(3).Select(x=>Project(x,depth+1)).ToArray();}
        }return result;
    }
    internal static string Preview(string dataRoot,ViewState view,SupportContext? automaticContext=null)
    {
        if(automaticContext!=null)automaticContext=SupportHistory.Review(automaticContext);
        var events=new List<object>();int discarded=0;string file=Path.Combine(dataRoot,"logs","events.jsonl");
        if(File.Exists(file)){
            if((File.GetAttributes(dataRoot)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(Path.GetDirectoryName(file)!)&FileAttributes.ReparsePoint)!=0)throw new IOException("诊断日志路径不可用");
            using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);long length=input.Length;long begin=Math.Max(0,length-2*1024*1024);input.Position=begin;var bytes=new byte[(int)(length-begin)];int count=0,read;while(count<bytes.Length&&(read=input.Read(bytes,count,bytes.Length-count))>0)count+=read;
            using var reader=new StringReader(Encoding.UTF8.GetString(bytes,0,count));if(begin>0)reader.ReadLine();string? line;
            while((line=reader.ReadLine())!=null){if(line.Length>65536){discarded++;continue;}try{using var doc=JsonDocument.Parse(line);var r=doc.RootElement;string kind=r.GetProperty("kind").GetString()!;if(!Events.Contains(kind))continue;
                    if(!DateTimeOffset.TryParse(r.GetProperty("utc").GetString(),out var time)){discarded++;continue;}
                    var data=r.GetProperty("data");string? code=null;if(kind=="paused"||data.TryGetProperty("state",out var state)&&state.ValueKind==JsonValueKind.String&&state.GetString()=="Paused"){
                        string reason=data.TryGetProperty("Message",out var message)&&message.ValueKind==JsonValueKind.String?message.GetString()!:data.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String?error.GetString()!:"";code=ProblemCatalog.Identify(reason).Code;}
                    events.Add(new{utc=time.ToUniversalTime(),kind,problemCode=code,data=Project(data)});if(events.Count>200)events.RemoveAt(0);
                }catch(JsonException){discarded++;}catch(InvalidOperationException){discarded++;}catch(KeyNotFoundException){discarded++;}}
        }
        ProblemInfo? problem=view.Phase==Phase.Paused?ProblemCatalog.Identify(view.Message):null;
        return JsonSerializer.Serialize(new{schema="jx3-support-v1",createdUtc=DateTime.UtcNow,version=typeof(SupportReport).Assembly.GetName().Version?.ToString(),runtime=Environment.Version.ToString(),osVersion=Environment.OSVersion.Version.ToString(),architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),current=new{state=view.Phase.ToString(),view.Starts,view.Returns,problem},automaticContext,discarded,scope="sanitized fields only; no screenshots, raw messages, OCR text, personal paths, account or process identifiers",events},new JsonSerializerOptions{WriteIndented=true});
    }
    internal static void Export(string destination,string reviewedJson)
    {
        using var doc=JsonDocument.Parse(reviewedJson);if(doc.RootElement.GetProperty("schema").GetString()!="jx3-support-v1")throw new InvalidDataException("报告格式无效");
        using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write);using var zip=new ZipArchive(output,ZipArchiveMode.Create);
        using(var writer=new StreamWriter(zip.CreateEntry("report.json").Open(),new UTF8Encoding(false)))writer.Write(reviewedJson);
        using(var writer=new StreamWriter(zip.CreateEntry("README.txt").Open(),new UTF8Encoding(false)))writer.Write("请向维护者提供本报告即可；补充说明为选填，无需回忆完整操作或另找日志。报告包含助手自身的有限操作及故障上下文，仅有预览内容，不包含截图或原始日志，不会自动上传；未分类错误码不代表已定位根因。较旧版本没有自动上下文时，报告会如实显示缺失。");
    }
}
