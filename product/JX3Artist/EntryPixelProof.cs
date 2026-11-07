using System.IO;
using Jx3.OneClickTest;
namespace JX3Artist;
// ROI carrier for the frozen local verifier. Matches remains solely for the
// historical 40 regression; raw equality MUST NOT gate production input.
internal sealed record EntryPixelProof(Box Region,byte[] Pixels)
{
    internal static EntryPixelProof Create(byte[] data,int width,int height,Box icon,double scale)
    {
        var box=new Box(Math.Max(0,(int)Math.Floor(icon.X-180*scale)),Math.Max(0,(int)Math.Floor(icon.Y-40*scale)),Math.Min(width,(int)Math.Ceiling(icon.X+110*scale)),Math.Min(height,(int)Math.Ceiling(icon.Y+120*scale)));
        if(!double.IsFinite(scale)||scale is <.35 or >2||box.X<0||box.Y<0||box.X2>width||box.Y2>height||box.Width<1||box.Width>640||box.Height<1||box.Height>320||data.LongLength!=(long)width*height*4)throw new InvalidDataException("invalid entry proof");
        var pixels=new byte[box.Width*box.Height*4];for(int row=0;row<box.Height;row++)Buffer.BlockCopy(data,((box.Y+row)*width+box.X)*4,pixels,row*box.Width*4,box.Width*4);
        return new(box,pixels);
    }
    internal bool Matches(byte[] data,int width,int height)
    {
        if(data.LongLength!=(long)width*height*4||Region.X<0||Region.Y<0||Region.X2>width||Region.Y2>height)return false;
        for(int row=0;row<Region.Height;row++)if(!data.AsSpan(((Region.Y+row)*width+Region.X)*4,Region.Width*4).SequenceEqual(Pixels.AsSpan(row*Region.Width*4,Region.Width*4)))return false;
        return true;
    }
    internal static void AddTests(Action<string,Action> test)
    {
        test("background preparation reserves switch time and never treats weak or stale global evidence as ready",()=>{
            var o=new Observation(1000,2000,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true);
            if(!PreparedEntryPolicy.Ready(o,true,3500)||PreparedEntryPolicy.Ready(o,true,3501)||PreparedEntryPolicy.Ready(o,false,2000)||PreparedEntryPolicy.Ready(o with{Geometry=false},true,2000)||PreparedEntryPolicy.Ready(o with{PanelLocation="AMBIGUOUS"},true,2000)||PreparedEntryPolicy.Ready(o,true,double.NaN))throw new Exception("preparation policy");
        });
        test("legacy raw comparator detects pixel changes but is not an authorization gate",()=>{
            var data=new byte[800*600*4];new Random(40).NextBytes(data);var proof=Create(data,800,600,new(300,250,372,316),1);
            if(!proof.Matches(data,800,600))throw new Exception("unchanged proof");
            foreach(var point in new[]{(300,250),(140,240),(390,350)}){var changed=(byte[])data.Clone();changed[(point.Item2*800+point.Item1)*4]^=1;if(proof.Matches(changed,800,600))throw new Exception("changed control admitted");}
            if(proof.Matches(data,801,600))throw new Exception("geometry mismatch");
        });
        test("local verification failure has a bounded compatibility route",()=>{
            if(PreparedEntryPolicy.Compatibility(0,true)||PreparedEntryPolicy.Compatibility(1,true)||!PreparedEntryPolicy.Compatibility(2,true)||!PreparedEntryPolicy.Compatibility(0,false))throw new Exception("compatibility route");
            var o=new Observation(1000,2000,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:true);
            if(PreparedEntryPolicy.ReadyForRoute(o,true,false,3800)||!PreparedEntryPolicy.ReadyForRoute(o,true,true,4000)||PreparedEntryPolicy.ReadyForRoute(o,true,true,4001))throw new Exception("compatibility must preserve original 3s guard");
        });
    }
}
internal sealed record FastEntryCheck(bool Confirmed,double Source,double ElapsedMs,string Reason);
internal static class PreparedEntryPolicy
{
    internal static bool Compatibility(int failures,bool hasRegion)=>!hasRegion||failures>=2;
    internal static bool ReadyForRoute(Observation o,bool target,bool compatibility,double now)=>compatibility?target&&o.Identity&&o.Geometry&&EntryReadiness.Confirmed(o)&&double.IsFinite(now)&&double.IsFinite(o.Source)&&now>=o.Source&&now-o.Source<=Jx3.OneClickTest.ClickPolicy.MaxFrameAgeMs:Ready(o,target,now);
    internal static bool Ready(Observation o,bool pixels,double now)=>pixels&&o.Identity&&o.Geometry&&EntryReadiness.Confirmed(o)&&double.IsFinite(now)&&double.IsFinite(o.Source)&&now>=o.Source&&now-o.Source<=2500;
}
