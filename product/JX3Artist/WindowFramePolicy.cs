using System.IO;
using System.Text.Json;
using Jx3.OneClickTest;
namespace JX3Artist;
internal sealed record PanelVerification(string Stage,double? BodyAgreement=null,double? LowerAgreement=null,int? MaskPixels=null,double? ClockCoreScore=null,int? ClockSearchWidth=null,int? ClockSearchHeight=null);
internal sealed record SmallPanelEvidence(bool Confirmed,int Radius,double Colour,double Coverage,double Precision,double LowerColour,double LowerCoverage,double LowerPrecision,double? LegacyAgreement,bool ClockRecovered);
internal static class WindowFramePolicy
{
    internal static SmallPanelEvidence? SmallEvidence(string text)
    {
        using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
        if(!root.TryGetProperty("d2",out var d2)||d2.ValueKind!=JsonValueKind.Object||!d2.TryGetProperty("painted_shape",out var shape)||shape.ValueKind!=JsonValueKind.Object||!shape.TryGetProperty("confirmed",out var confirmed)||confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||!shape.TryGetProperty("radius",out var radius)||radius.ValueKind!=JsonValueKind.Number||!radius.TryGetInt32(out int pixels)||pixels is <2 or >3)return null;
        var values=new List<double>();
        foreach(string part in new[]{"total","lower"}){
            if(!shape.TryGetProperty(part,out var section)||section.ValueKind!=JsonValueKind.Object)return null;
            foreach(string key in new[]{"colour","coverage","precision"}){if(!section.TryGetProperty(key,out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out double number)||!double.IsFinite(number)||number<0||number>1)return null;values.Add(number);}
        }
        double? legacy=null;if(d2.TryGetProperty("painted_body",out var body)&&body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("agreement",out var agreement)&&agreement.ValueKind==JsonValueKind.Number&&agreement.TryGetDouble(out double old)&&double.IsFinite(old)&&old>=0&&old<=1)legacy=old;
        bool clock=d2.TryGetProperty("small_clock_core",out var core)&&core.ValueKind==JsonValueKind.Object&&core.TryGetProperty("confirmed",out var clockConfirmed)&&clockConfirmed.ValueKind==JsonValueKind.True;
        return new(confirmed.GetBoolean(),pixels,values[0],values[1],values[2],values[3],values[4],values[5],legacy,clock);
    }
    internal static PanelVerification? Verification(string text)
    {
        using var doc=JsonDocument.Parse(text);var root=doc.RootElement;JsonElement body=default,core=default;string? stage=null;int? width=null,height=null;
        if(root.TryGetProperty("d2_location",out var loc)&&loc.ValueKind==JsonValueKind.Object&&loc.TryGetProperty("rejected_clocks",out var rejected)&&rejected.ValueKind==JsonValueKind.Array&&rejected.GetArrayLength()==1){
            var item=rejected[0];if(item.ValueKind!=JsonValueKind.Object)return null;
            if(item.TryGetProperty("verification_stage",out var st)&&st.ValueKind==JsonValueKind.String)stage=st.GetString();
            if(stage is not ("PAINTED_BODY_UNCONFIRMED" or "CLOCK_UNCONFIRMED"))return null;
            item.TryGetProperty("painted_body",out body);item.TryGetProperty("clock_core",out core);
            if(item.TryGetProperty("region",out var rect)&&rect.ValueKind==JsonValueKind.Array&&rect.GetArrayLength()==4&&rect.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.Number&&x.TryGetInt32(out _))){var a=rect.EnumerateArray().Select(x=>x.GetInt32()).ToArray();long w=(long)a[2]-a[0],h=(long)a[3]-a[1];if(w>0&&w<=512&&h>0&&h<=256){width=(int)w;height=(int)h;}}
        }
        else if(root.TryGetProperty("d2",out var d2)&&d2.ValueKind==JsonValueKind.Object&&d2.TryGetProperty("state",out var state)&&state.ValueKind==JsonValueKind.String&&state.GetString()=="UNKNOWN"&&d2.TryGetProperty("painted_body",out body)&&body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("confirmed",out var confirmed)&&confirmed.ValueKind==JsonValueKind.False)stage="PAINTED_BODY_UNCONFIRMED";
        if(stage==null)return null;
        double? Ratio(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out double number)&&double.IsFinite(number)&&number>=0&&number<=1?number:null;
        int? count=body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("mask_pixels",out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetInt32(out int pixels)&&pixels>0&&pixels<=36000?pixels:null;
        return new(stage,Ratio(body,"agreement"),Ratio(body,"lower_agreement"),count,Ratio(core,"score"),width,height);
    }
    internal static (string? Status,int? Attempts,double? Original,double? Selected) Recovery(string text)
    {
        using var doc=JsonDocument.Parse(text);if(!doc.RootElement.TryGetProperty("panel_scale_recovery",out var value)||value.ValueKind!=JsonValueKind.Object)return (null,null,null,null);
        string? status=value.TryGetProperty("status",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString():null;if(status is not ("CONFIRMED" or "UNCONFIRMED" or "CONFLICT" or "BUDGET_LIMIT"))return (null,null,null,null);
        int? count=value.TryGetProperty("attempts",out var a)&&a.ValueKind==JsonValueKind.Array?a.GetArrayLength():null;if(count>2)return (null,null,null,null);
        double? Scale(string key)=>value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out double n)&&double.IsFinite(n)&&n>=.35&&n<=2?n:null;
        return (status,count,Scale("original_scale"),Scale("selected_scale"));
    }
    internal static System.Windows.Int32Rect? DiagnosticRegion(string text,WindowFrameMap map)
    {
        using var doc=JsonDocument.Parse(text);var panel=doc.RootElement.GetProperty("d2_location");
        if(!panel.TryGetProperty("diagnostic_region",out var region))return null;
        var v=region.EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(v.Length!=4)throw new IOException("invalid anchor diagnostic region");
        var b=new Box(v[0],v[1],v[2],v[3]);map.ScreenCenter(b,map.Snapshot);
        if(b.Width<1||b.Width>512||b.Height<1||b.Height>256)throw new IOException("anchor diagnostic budget exceeded");
        return new(b.X,b.Y,b.Width,b.Height);
    }
    internal static bool SizeSupported(int w,int h)=>w>=800&&w<=4096&&h>=600&&h<=2304;
    internal static double ReadScale(string text){using var doc=JsonDocument.Parse(text);return doc.RootElement.GetProperty("frame_profile").GetProperty("ui_scale").GetDouble();}
    static bool ScaledSize(Box b,int w,int h,double scale)=>b.Width>=Math.Floor(w*scale)&&b.Width<=Math.Ceiling(w*scale)+1&&b.Height>=Math.Floor(h*scale)&&b.Height<=Math.Ceiling(h*scale)+1;
    internal static Box? SearchRegion(string text,WindowFrameMap map)
    {
        using var doc=JsonDocument.Parse(text);var panel=doc.RootElement.GetProperty("d2_location");
        if(!panel.GetProperty("verified").GetBoolean()||!panel.TryGetProperty("text_search_region",out var region))return null;
        var v=region.EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(v.Length!=4)throw new IOException("invalid text search");var b=new Box(v[0],v[1],v[2],v[3]);map.ScreenCenter(b,map.Snapshot);
        if(b.Width<12||b.Width>512||b.Height<5||b.Height>256)throw new IOException("text search budget exceeded");return b;
    }
    internal static Box? Validate(string text,WindowFrameMap map)
    {
        using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
        if(!root.TryGetProperty("frame_profile",out var profile)||profile.GetProperty("schema").GetString() is not ("window-original-ui-v1" or "window-ui-v2" or "window-ui-v3")||!profile.GetProperty("verified").GetBoolean()||!double.IsFinite(profile.GetProperty("ui_scale").GetDouble())||profile.GetProperty("ui_scale").GetDouble() is <.35 or >2||profile.GetProperty("width").GetInt32()!=map.Width||profile.GetProperty("height").GetInt32()!=map.Height||!SizeSupported(map.Width,map.Height)||profile.GetProperty("tile_count").GetInt32() is <1 or >9)throw new IOException("识别画面与窗口布局不一致");
        if(profile.GetProperty("schema").GetString()=="window-ui-v3")
            foreach(string key in new[]{"entry_scale","panel_scale"}){double value=profile.GetProperty(key).GetDouble();if(!double.IsFinite(value)||value is <.35 or >2)throw new IOException("invalid target scale");}
        Box BoxOf(JsonElement value){var v=value.EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(v.Length!=4)throw new IOException("识别坐标格式无效");return new(v[0],v[1],v[2],v[3]);}
        if(BoxOf(profile.GetProperty("content"))!=map.ClientInFrame)throw new IOException("识别客户区与来源帧不一致");
        double scale=profile.TryGetProperty("entry_scale",out var entryScale)?entryScale.GetDouble():profile.GetProperty("ui_scale").GetDouble();
        if(!double.IsFinite(scale)||scale is <.35 or >2)throw new IOException("invalid entry scale");
        foreach(var item in root.GetProperty("location").GetProperty("candidates").EnumerateArray()){var candidate=BoxOf(item.GetProperty("bbox"));if(!ScaledSize(candidate,72,66,scale))throw new IOException("入口尺寸与缩放不一致");map.ScreenCenter(candidate,map.Snapshot);}
        var panel=root.GetProperty("d2_location");
        foreach(var candidate in panel.GetProperty("candidates").EnumerateArray())map.ScreenCenter(BoxOf(candidate.GetProperty("source_region")),map.Snapshot);
        if(!panel.GetProperty("verified").GetBoolean())return null;
        if(profile.GetProperty("schema").GetString()=="window-ui-v3"&&!panel.TryGetProperty("text_status",out _))throw new IOException("missing text evidence");
        if(panel.TryGetProperty("text_status",out var textStatus))
        {
            if(textStatus.GetString()!="UNIQUE_TEXT_LINE")return null;
            var search=BoxOf(panel.GetProperty("text_search_region"));map.ScreenCenter(search,map.Snapshot);
            var textBox=BoxOf(panel.GetProperty("timer_frame_bbox"));map.ScreenCenter(textBox,map.Snapshot);
            if(textBox.X<search.X||textBox.Y<search.Y||textBox.X2>search.X2||textBox.Y2>search.Y2||textBox.Width<12||textBox.Width>256||textBox.Height<5||textBox.Height>96)throw new IOException("invalid text region");return textBox;
        }
        var box=BoxOf(panel.GetProperty("timer_frame_bbox"));if(!ScaledSize(box,68,25,scale))throw new IOException("倒计时区域大小不受支持");map.ScreenCenter(box,map.Snapshot);return box;
    }
}
