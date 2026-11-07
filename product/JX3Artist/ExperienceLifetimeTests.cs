using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace JX3Artist;
internal static class ExperienceLifetimeTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("experience lifetime assertion failed");}
        void Scratch(Action<string> action)
        {
            string root=Path.Combine(Path.GetTempPath(),"JX3Artist-life-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{action(root);}finally{foreach(string dir in Directory.GetDirectories(root)){foreach(string file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);}foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);}
        }
        void Earn(ExperienceSession session,long start)
        {
            Observation Panel(long source)=>new(source,source+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",PanelLocated:true,PanelLocation:"UNIQUE_PANEL");
            session.Observe(Panel(start),Phase.Performing,0,0,120000);session.Observe(Panel(start+60000),Phase.Performing,0,0,120000);
        }
        string PathFor(string root)=>Path.Combine(root,"experience/session.json");
        void Seed(string root,int count){for(int i=0;i<count;i++){using var session=new ExperienceSession(root,clock:()=>i*200000L);Earn(session,i*200000L);}}
        void Legacy(string root)
        {var node=JsonNode.Parse(File.ReadAllText(PathFor(root)))!.AsObject();node["Schema"]="jx3-experience-estimate-v1";node.Remove("LifetimeBeforeCurrent");node.Remove("LifetimePartial");File.WriteAllText(PathFor(root),node.ToJsonString());}
        test("lifetime includes current session exactly once across saves and application reopens",()=>Scratch(root=>{
            Seed(root,1);
            using(var second=new ExperienceSession(root,clock:()=>200000)){Check(second.Read().EstimatedGain==0&&second.LifetimeGain==6);Earn(second,200000);Check(second.LifetimeGain==12);Check(second.Flush());Check(second.Flush());Check(second.LifetimeGain==12);}
            using var third=new ExperienceSession(root,clock:()=>400000);Check(third.Read().EstimatedGain==0&&third.LifetimeGain==12);
        }));
        test("lifetime survives more than ten retained session details",()=>Scratch(root=>{
            Seed(root,15);using var next=new ExperienceSession(root,clock:()=>4000000);Check(next.LifetimeGain==90&&!next.LifetimePartial);Check(next.Flush());
            var doc=JsonSerializer.Deserialize<ExperienceDocument>(File.ReadAllText(PathFor(root)))!;Check(doc.History.Length==10&&doc.LifetimeBeforeCurrent==90);
        }));
        test("legacy migration adds estimates not manually entered experience and preserves the original record",()=>Scratch(root=>{
            Seed(root,2);using(var prior=new ExperienceSession(root,clock:()=>400000)){Earn(prior,400000);prior.Initialize(5,999999);}
            Legacy(root);string original=File.ReadAllText(PathFor(root));
            using(var next=new ExperienceSession(root,clock:()=>600000)){Check(next.LifetimeGain==18&&!next.LifetimePartial&&next.Read().Baseline is null);Earn(next,600000);Check(next.LifetimeGain==24);}
            Check(Directory.GetFiles(Path.Combine(root,"experience"),"legacy-*.json").Any(file=>File.ReadAllText(file)==original));
            using var reopened=new ExperienceSession(root,clock:()=>800000);Check(reopened.LifetimeGain==24);
        }));
        test("a truncated legacy history is marked partial rather than invented as complete lifetime",()=>Scratch(root=>{
            Seed(root,12);Legacy(root);using var next=new ExperienceSession(root,clock:()=>3000000);Check(next.LifetimeGain==66&&next.LifetimePartial);
        }));
        test("missing v2 lifetime metadata is rejected rather than defaulting its completeness flag",()=>Scratch(root=>{
            Seed(root,1);var node=JsonNode.Parse(File.ReadAllText(PathFor(root)))!.AsObject();node["Schema"]="jx3-experience-estimate-v2";node.Remove("LifetimeResetOffset");node.Remove("LifetimePartial");string original=node.ToJsonString();File.WriteAllText(PathFor(root),original);
            using(var next=new ExperienceSession(root,clock:()=>200000)){Check(next.SaveError!=null&&next.LifetimeGain is null);}
            Check(File.ReadAllText(PathFor(root))==original);
        }));
        test("explicitly unknown lifetime remains unknown after restart while session estimates still work",()=>Scratch(root=>{
            Seed(root,1);var node=JsonNode.Parse(File.ReadAllText(PathFor(root)))!.AsObject();node["LifetimeBeforeCurrent"]=null;node["LifetimePartial"]=true;File.WriteAllText(PathFor(root),node.ToJsonString());
            using(var next=new ExperienceSession(root,clock:()=>200000)){Check(next.LifetimeGain is null);Earn(next,200000);Check(next.Read().EstimatedGain==6&&next.LifetimeGain is null);}
            using var reopened=new ExperienceSession(root,clock:()=>400000);Check(reopened.LifetimeGain is null&&reopened.LifetimePartial);
        }));
        test("duplicated legacy session identities cannot be counted twice",()=>Scratch(root=>{
            Seed(root,2);Legacy(root);var node=JsonNode.Parse(File.ReadAllText(PathFor(root)))!;node["History"]![0]!["Id"]=node["Current"]!["Id"]!.DeepClone();string original=node.ToJsonString();File.WriteAllText(PathFor(root),original);
            using(var next=new ExperienceSession(root,clock:()=>400000)){Check(next.SaveError!=null&&next.LifetimeGain is null);}
            Check(File.ReadAllText(PathFor(root))==original);
        }));
        test("lifetime overflow becomes unknown without wrapping or clearing current estimates",()=>Scratch(root=>{
            using(var seed=new ExperienceSession(root,clock:()=>0)){}
            var node=JsonNode.Parse(File.ReadAllText(PathFor(root)))!;node["LifetimeBeforeCurrent"]=long.MaxValue/6*6;File.WriteAllText(PathFor(root),node.ToJsonString());
            using(var next=new ExperienceSession(root,clock:()=>200000)){Earn(next,200000);Check(next.LifetimeGain is null&&next.Read().EstimatedGain==6);}
            using var reopened=new ExperienceSession(root,clock:()=>400000);Check(reopened.LifetimeGain is null);
        }));
        test("an interrupted saved session contributes its checkpoint once without filling downtime",()=>Scratch(root=>{
            string interrupted;using(var prior=new ExperienceSession(root,clock:()=>0)){Earn(prior,0);Check(prior.Flush());interrupted=File.ReadAllText(PathFor(root));}
            File.WriteAllText(PathFor(root),interrupted);
            using(var next=new ExperienceSession(root,clock:()=>10000000)){Check(next.LifetimeGain==6&&next.LifetimePartial);Earn(next,10000000);Check(next.LifetimeGain==12);}
            using var reopened=new ExperienceSession(root,clock:()=>20000000);Check(reopened.LifetimeGain==12);
        }));
    }
}
