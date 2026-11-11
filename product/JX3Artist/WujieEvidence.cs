using System.IO;
using System.Text.Json;
using Jx3.OneClickTest;

namespace JX3Artist;


internal record VisualEvidence(string State, Box? Entry, Box? Panel, Box? Timer);

internal static class ObservationEvidence
{
    internal const string InvalidEntrySize="极速端开始图标尺寸与试用配置不符。";
    internal const string InvalidRecognitionBounds="极速端识别区域超出已确认的窗口范围。";
    internal static VisualEvidence Read(string json, WindowFrameMap map)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = root.GetProperty("entry_boxes");
        string? entry = root.GetProperty("entry").GetString();
        string? panel = root.GetProperty("panel").GetString();
        string? performance = root.GetProperty("performance").GetString();
        int stops = root.GetProperty("stop_proposals").GetInt32();
        bool stopConfirmed = root.GetProperty("stop_confirmed").GetBoolean();
        if (entry == "UNIQUE_CANDIDATE" && entries.GetArrayLength() == 1 && stops == 0 && !stopConfirmed
            && panel == "NO_VERIFIED_PANEL" && performance == "UNKNOWN")
        {
            var box = ReadBox(entries[0], map.ClientInFrame);
            if (box.Width != 96 || box.Height != 96) throw new IOException(InvalidEntrySize);
            return new("ENTRY", box, null, null);
        }
        if (entry == "NO_VERIFIED_CANDIDATE" && entries.GetArrayLength() == 0)
        {
            if (panel == "UNIQUE_PANEL" && performance == "VISIBLE_EXPANDED" && stops is 0 or 1)
            {
                var box = ReadBox(root.GetProperty("panel_box"), map.ClientInFrame);
                return new("PERFORMING_EXPANDED", null, box, ReadBox(root.GetProperty("timer_region"), box));
            }
            if (panel == "NO_VERIFIED_PANEL" && performance == "VISIBLE_STOP_BUTTON" && stops == 1 && stopConfirmed)
                return new("PERFORMING_STOP_BUTTON", null, null, null);
        }
        return new("UNKNOWN", null, null, null);
    }

    static Box ReadBox(JsonElement element, Box bounds)
    {
        var xy = element.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (xy.Length != 4 || xy[0] < bounds.X || xy[1] < bounds.Y || xy[2] > bounds.X2 || xy[3] > bounds.Y2
            || xy[2] <= xy[0] || xy[3] <= xy[1]) throw new IOException(InvalidRecognitionBounds);
        return new(xy[0], xy[1], xy[2], xy[3]);
    }
}
