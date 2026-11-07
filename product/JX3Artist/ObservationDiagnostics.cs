using System.Text.Json;
using System.Text.Json.Serialization;
namespace JX3Artist;

// Optional diagnostic data: never recognition evidence or an input permission.
internal static class ObservationDiagnostics
{
    static readonly JsonSerializerOptions Numbers=new(){NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    internal static JsonElement Create(CaptureMetrics? metrics,string? timerPath,string? timerError,string? entryPath,string? entryError)
    {
        var value=JsonSerializer.SerializeToElement(new{Metrics=metrics,
            TimerEvidenceSaved=timerPath!=null,TimerEvidenceFailed=timerError!=null,TimerEvidenceFailure=Failure(timerError),
            EntryEvidenceSaved=entryPath!=null,EntryEvidenceFailed=entryError!=null,EntryEvidenceFailure=Failure(entryError)},Numbers);
        if(!SupportReport.Sanitize("observation_cycle",value,out var safe))throw new InvalidOperationException("observation diagnostic schema unavailable");
        return safe;
    }
    static string? Failure(string? error)=>error switch {
        null=>null,
        var s when s.StartsWith("InvalidDataException:",StringComparison.Ordinal)=>"INVALID_DATA",
        var s when s.StartsWith("UnauthorizedAccessException:",StringComparison.Ordinal)=>"ACCESS_DENIED",
        var s when s.StartsWith("IOException:",StringComparison.Ordinal)=>"IO_ERROR",
        _=>"UNKNOWN"
    };
}
