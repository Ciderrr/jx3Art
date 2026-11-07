using System.IO;
using System.Text.Json;
namespace JX3Artist;
internal static class ExperienceSessionTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("experience session assertion failed");}
        void Scratch(Action<string> action)
        {
            string root=Path.Combine(Path.GetTempPath(),"JX3Artist-exp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{action(root);}finally{foreach(string dir in Directory.GetDirectories(root)){foreach(string file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);}foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);}
        }
        Observation Panel(long source)=>new(source,source+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",PanelLocated:true,PanelLocation:"UNIQUE_PANEL");
        void Feed(ExperienceSession session,long source,int round=0)=>session.Observe(Panel(source),Phase.Performing,round,0,120000);
        test("application experience persists a closed record and reopens as a new session with history",()=>Scratch(root=>{
            using(var first=new ExperienceSession(root,clock:()=>0)){Feed(first,0);Feed(first,60000);first.Initialize(4,75);Check(first.Read().EstimatedGain==6);}
            string file=Path.Combine(root,"experience/session.json");var doc=JsonSerializer.Deserialize<ExperienceDocument>(File.ReadAllText(file))!;
            Check(doc.Current.Complete&&doc.Current.Estimate.Closed&&doc.Current.Estimate.EstimatedGain==6);
            using var next=new ExperienceSession(root,clock:()=>200000);Check(next.Read().EstimatedGain==0&&next.Read().Baseline is null);
            Check(next.Previous!.Estimate.EstimatedGain==6&&next.Previous.Complete&&next.SuggestedBaseline!.Level==4);
        }));
        test("incomplete previous application is retained without filling downtime or adopting its baseline",()=>Scratch(root=>{
            string path=Path.Combine(root,"experience/session.json");string interrupted;
            using(var original=new ExperienceSession(root,clock:()=>0)){Feed(original,0);Feed(original,60000);original.Initialize(2,19);Check(original.Flush());interrupted=File.ReadAllText(path);}
            File.WriteAllText(path,interrupted);
            using var next=new ExperienceSession(root,clock:()=>1000000);Check(next.Previous!.Complete==false&&next.Read().EstimatedGain==0&&next.Read().Baseline is null&&next.SuggestedBaseline!.Experience==19);
        }));
        test("assistance stop restart and controller round transitions keep one application aggregate",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);
            Feed(session,0);Feed(session,60000);session.Suspend();Feed(session,600000);Feed(session,660000);
            Check(session.Read().EstimatedGain==12);Feed(session,700000,1);Feed(session,760000,1);Check(session.Read().EstimatedGain==18);
        }));
        test("pending first panel is counted only after controller confirmation and unknown countdown is allowed",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);
            session.Observe(Panel(0),Phase.Watching,0,0,1000);Check(!session.HasObservation);
            session.Observe(Panel(1000),Phase.Performing,0,0,120000);Feed(session,60000);Check(session.Read().EstimatedGain==6&&session.Read().ConfirmedGain is null);
            session.Observe(Panel(120000) with{D2="UNKNOWN"},Phase.WaitingVisibility,0,0,5000);
            Feed(session,180000);Check(session.Read().EstimatedGain==6&&session.Read().HasUnobservedTime);
        }));
        test("unconfirmed identity geometry panel and source time never produce experience",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);
            foreach(var bad in new[]{Panel(0) with{Identity=false},Panel(60000) with{Geometry=false},Panel(120000) with{PanelLocated=false},Panel(180000) with{PanelLocation="AMBIGUOUS"},Panel(240000) with{Now=244000}})session.Observe(bad,Phase.Performing,0,0,120000);
            Check(session.Read().EstimatedGain==0&&!session.HasObservation&&session.Read().ConfirmedGain is null);
        }));
        test("corrupt or unowned experience files are preserved and cannot block memory-only estimates",()=>Scratch(root=>{
            string folder=Path.Combine(root,"experience");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"session.json");File.WriteAllText(path,"sentinel");
            using(var session=new ExperienceSession(root,clock:()=>0)){Feed(session,0);Feed(session,60000);Check(session.SaveError!=null&&session.Read().EstimatedGain==6);}
            Check(File.ReadAllText(path)=="sentinel");
            File.WriteAllText(Path.Combine(folder,".owner"),"JX3ArtistExperienceV1");
            using(var session=new ExperienceSession(root,clock:()=>0)){Check(session.SaveError!=null);session.Initialize(3,9);}
            Check(File.ReadAllText(path)=="sentinel");
        }));
        test("locked experience save failure is visible and can recover without touching automation",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,clock:()=>0);string path=Path.Combine(root,"experience/session.json");Check(session.Flush());
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){session.Initialize(3,30);Check(SpinWait.SpinUntil(()=>session.SaveErrors>0,3000));Check(session.SaveError!=null);}
            session.Initialize(3,31);Check(session.Flush());Check(session.Read().Baseline!.Experience==31);
        }));
        test("manual initialization parser accepts whole values and rejects malformed or overflowing inputs",()=>{
            Check(ExperienceUi.Parse(" 5 ","42")==new ExperienceBaseline(5,42,0));
            foreach(var(level,xp) in new[]{("0","1"),("1","-1"),("1.5","2"),("五","2"),("1","9223372036854775808"),("2147483648","0")}){
                bool rejected=false;try{ExperienceUi.Parse(level,xp);}catch(ArgumentException){rejected=true;}Check(rejected);
            }
        });
        test("a missing estimated value in a saved record is unknown rather than a restored zero",()=>Scratch(root=>{
            string path=Path.Combine(root,"experience/session.json");using(var prior=new ExperienceSession(root,clock:()=>0)){Feed(prior,0);Feed(prior,60000);}
            var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;node["Current"]!["Estimate"]!.AsObject().Remove("EstimatedGain");string damaged=node.ToJsonString();File.WriteAllText(path,damaged);
            using(var next=new ExperienceSession(root,clock:()=>100000)){Check(next.SaveError!=null&&next.Previous is null);}
            Check(File.ReadAllText(path)==damaged);
        }));
        test("experience status report keeps estimates and errors but excludes manual experience and personal data",()=>{
            var input=JsonSerializer.SerializeToElement(new{ExperienceEstimatedGain=12,ExperienceLifetimeGain=60,ExperienceLifetimePartial=false,ExperienceObservedMs=120000,ExperienceHasUnobservedTime=true,ExperienceSaveErrors=2,ExperienceSaveFailure=true,Level=5,Experience=42,Account="SECRET",Path="SECRET_PATH"});
            Check(SupportReport.Sanitize("experience_status",input,out var safe));string text=safe.GetRawText();
            Check(safe.GetProperty("ExperienceEstimatedGain").GetInt32()==12&&safe.GetProperty("ExperienceLifetimeGain").GetInt32()==60&&!safe.GetProperty("ExperienceLifetimePartial").GetBoolean()&&safe.GetProperty("ExperienceSaveFailure").GetBoolean());
            Check(!safe.TryGetProperty("Level",out _)&&!safe.TryGetProperty("Experience",out _)&&!text.Contains("SECRET"));
        });
        test("invalid observation scheduling metadata cannot expand experience continuity",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);Feed(session,0);
            session.Observe(Panel(60000),Phase.Performing,0,0,int.MaxValue);Check(session.Read().EstimatedGain==0&&session.Read().HasUnobservedTime);
        }));
        test("late second-panel confirmation cannot backfill a missed initial observation interval",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);
            session.Observe(Panel(0),Phase.Watching,0,0,1000);
            session.Observe(Panel(60000),Phase.Performing,0,0,120000);
            Check(session.Read().EstimatedGain==0&&session.Read().ObservedMilliseconds==0);
        }));
        test("a changed countdown epoch splits partial performances before controller reanchor confirmation",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,memoryOnly:true,clock:()=>0);
            void Timer(long source,int seconds)=>session.Observe(Panel(source) with{CountdownSeconds=seconds,CountdownQuality="DUAL_PASS_AGREEMENT"},Phase.Performing,0,0,120000);
            Timer(0,1799);Timer(40000,1759);Timer(50000,1799);Timer(90000,1759);
            Check(session.Read().EstimatedGain==0&&session.Read().ObservedMilliseconds==80000&&session.Read().HasUnobservedTime);
        }));
    }
}
