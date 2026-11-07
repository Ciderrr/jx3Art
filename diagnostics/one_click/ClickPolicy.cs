namespace Jx3.OneClickTest;

internal record Box(int X,int Y,int X2,int Y2)
{
    internal int Width=>X2-X;internal int Height=>Y2-Y;
}
internal record Check(bool Identity,bool Foreground,bool HitTarget,bool Geometry,bool Cancelled,bool ButtonHeld,
    string Privilege,string Location,int CandidateCount,int FrameWidth,int FrameHeight,double AgeMs,Box? Candidate,string D2,bool WindowFrame=false,double UiScale=1);
internal static class ClickPolicy
{
    internal const double MaxFrameAgeMs=3000;
    internal static string? Refusal(Check c)
    {
        if(c.Cancelled)return "CANCELLED";
        if(!c.Identity)return "TARGET_IDENTITY_CHANGED";
        if(c.Privilege!="INTEGRITY_COMPATIBLE_NOT_INPUT_VERIFIED")return "PRIVILEGE_NOT_VERIFIED";
        if(!c.Foreground)return "GAME_NOT_FOREGROUND";
        if(!c.Geometry || (c.WindowFrame?c.FrameWidth<800||c.FrameWidth>4096||c.FrameHeight<600||c.FrameHeight>2304:c.FrameWidth!=3840||c.FrameHeight!=2088))return "GEOMETRY_NOT_VERIFIED";
        if(!double.IsFinite(c.AgeMs) || c.AgeMs<0 || c.AgeMs>MaxFrameAgeMs)return "STALE_FRAME";
        if(c.Location!="UNIQUE_CANDIDATE" || c.CandidateCount!=1)return "NO_UNIQUE_CURRENT_TARGET";
        if(c.Candidate is not {} b || b.X<0 || b.Y<0 || b.X2>c.FrameWidth || b.Y2>c.FrameHeight)return "INVALID_TARGET_BOX";
        bool exact=!c.WindowFrame||c.UiScale==1;
        if(exact?(b.Width!=72||b.Height!=66):(!double.IsFinite(c.UiScale)||c.UiScale is <.35 or >2||b.Width<Math.Floor(72*c.UiScale)||b.Width>Math.Ceiling(72*c.UiScale)+1||b.Height<Math.Floor(66*c.UiScale)||b.Height>Math.Ceiling(66*c.UiScale)+1))return "INVALID_TARGET_BOX";
        if(c.D2=="VISIBLE")return "COUNTDOWN_ALREADY_VISIBLE";
        if(c.ButtonHeld)return "MOUSE_BUTTON_HELD";
        if(!c.HitTarget)return "TARGET_POINT_OCCLUDED";
        return null;
    }
    internal static (int X,int Y) Normalize(int x,int y,Box desktop)
    {
        if(desktop.Width<=1 || desktop.Height<=1 || x<desktop.X || y<desktop.Y || x>=desktop.X2 || y>=desktop.Y2)throw new ArgumentException("point outside virtual desktop");
        return ((int)Math.Round(((long)x-desktop.X)*65535d/(desktop.Width-1)),(int)Math.Round(((long)y-desktop.Y)*65535d/(desktop.Height-1)));
    }
}
// One DOWN attempt, no retry; release exactly once even on cancellation/partial return.
internal sealed class OneShot
{
    internal bool Consumed{get;private set;}
    internal (uint PressSent,uint ReleaseSent) Submit(Check check,Func<uint> press,Action hold,Func<uint> release)
    {
        if(Consumed)throw new InvalidOperationException("attempt already consumed");
        string? reason=ClickPolicy.Refusal(check);if(reason!=null)throw new InvalidOperationException(reason);
        Consumed=true;
        uint sent=0,released=0;
        try {sent=press();if(sent==2)hold();}
        finally {released=release();}
        return (sent,released);
    }
}
