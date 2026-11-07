using System.Text.Json;
using System.IO;
namespace Int1;

internal static class UserClickCalibrationTests
{
    public static List<Program.CaseResult> Run()
    {
        var cases=new List<Program.CaseResult>();
        void EdgeCheck(string name,bool pass)=>cases.Add(new(){Name="user-click: "+name,Expect="true",Actual=pass.ToString(),Pass=pass});
        var button=new UserClickCalibration.ButtonEdge(true);
        EdgeCheck("held button on startup is not a click",!button.Sample(true));
        EdgeCheck("release rearms without generating click",!button.Sample(false));
        EdgeCheck("new press is detected",button.Sample(true));
        EdgeCheck("holding does not duplicate",!button.Sample(true));
        button.Sample(false);EdgeCheck("next distinct press detected",button.Sample(true));
        var bounds=new Win32RO.RECT{Left=-3840,Top=100,Right=0,Bottom=2188};
        var frame=new UserClickCalibration.Snapshot(Array.Empty<byte>(),3840,2088,1000,bounds);
        void Check(string name,string expected,UserClickCalibration.Selection result,bool pass) => cases.Add(new(){Name="user-click: "+name,Expect=expected,Actual=$"{result.Error??"OK"} ({result.X},{result.Y})",Pass=pass});
        var ok=UserClickCalibration.Map(frame,bounds,-1089,1603,1100);
        Check("physical point with negative monitor origin","2751,1503",ok,ok.Error==null && ok.X==2751 && ok.Y==1503);
        var moved=bounds;moved.Left+=94;moved.Right+=94;
        var changed=UserClickCalibration.Map(frame,moved,-1089,1603,1100);
        Check("geometry changed since frame","reject",changed,changed.Error=="window_geometry_changed");
        foreach(var t in new[]{999d,1500.01,double.NaN,double.PositiveInfinity})
        { var r=UserClickCalibration.Map(frame,bounds,-1089,1603,t);Check("stale/future/nonfinite "+t,"reject",r,r.Error=="pre_click_frame_not_fresh"); }
        var missing=UserClickCalibration.Map(null,bounds,0,0,1100);Check("no pre-click frame","reject",missing,missing.Error=="no_pre_click_frame");
        foreach(var point in new[]{(-3841,100),(0,100),(-3840,99),(-3840,2188)})
        {var r=UserClickCalibration.Map(frame,bounds,point.Item1,point.Item2,1100);Check("outside "+point,"reject",r,r.Error=="click_outside_capture");}
        var last=UserClickCalibration.Map(frame,bounds,-1,2187,1500);Check("last pixel and freshness boundary","3839,2087",last,last.Error==null && last.X==3839 && last.Y==2087);
        var scaled=frame with{Width=2560,Height=1392};var scale=UserClickCalibration.Map(scaled,bounds,-1089,1603,1100);
        Check("logical/physical mismatch never scale guessed","reject",scale,scale.Error=="capture_bounds_unverified_or_unsupported");
        var smallBounds=new Win32RO.RECT{Left=0,Top=0,Right=2560,Bottom=1392};
        var small=scaled with{Bounds=smallBounds};var unsupported=UserClickCalibration.Map(small,smallBounds,100,100,1100);
        Check("unsupported capture dimensions","reject",unsupported,unsupported.Error=="capture_bounds_unverified_or_unsupported");
        var rebound=frame with{Bounds=moved};var recovered=UserClickCalibration.Map(rebound,moved,-995,1603,1100);
        Check("fresh geometry after move maps same target pixel","2751,1503",recovered,recovered.Error==null && recovered.X==2751 && recovered.Y==1503);
        return cases;
    }
    public static int Command(string[] args)
    {
        var cases=Run();string output="output/user_click_offline";int i=Array.IndexOf(args,"--out");if(i>=0 && i+1<args.Length)output=args[i+1];
        Directory.CreateDirectory(output);int passed=cases.Count(c=>c.Pass);
        File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed,total=cases.Count,cases},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"user-click offline {passed}/{cases.Count}");return passed==cases.Count?0:9;
    }
}
