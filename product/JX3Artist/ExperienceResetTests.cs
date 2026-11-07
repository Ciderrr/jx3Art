using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace JX3Artist;
internal static class ExperienceResetTests
{
    internal static void Add(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("experience reset assertion failed");}
        void Scratch(Action<string> action){string root=Path.Combine(Path.GetTempPath(),"JX3Artist-reset-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{action(root);}finally{Directory.Delete(root,true);}}
        void Feed(ExperienceSession session,long source)=>session.Observe(new(source,source+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",PanelLocated:true,PanelLocation:"UNIQUE_PANEL"),Phase.Performing,0,0,120000);
        string FileFor(string root)=>Path.Combine(root,"experience/session.json");
        test("experience reset preserves raw session and historical gains while reopening only post-reset increments",()=>Scratch(root=>{
            using(var first=new ExperienceSession(root,clock:()=>0)){Feed(first,0);Feed(first,60000);}
            using(var next=new ExperienceSession(root,clock:()=>200000)){Feed(next,200000);Feed(next,260000);Check(next.Read().EstimatedGain==6&&next.LifetimeGain==12);var old=next.Read();var previous=next.Previous;next.ResetLifetime();Check(next.LifetimeGain==0&&next.Read()==old&&next.Previous==previous);Feed(next,320000);Check(next.Read().EstimatedGain==12&&next.LifetimeGain==6);Check(next.Flush());}
            using var reopened=new ExperienceSession(root,clock:()=>500000);Check(reopened.LifetimeGain==6&&reopened.Read().EstimatedGain==0&&reopened.Previous!.Estimate.EstimatedGain==12);Check(reopened.Flush());var saved=JsonSerializer.Deserialize<ExperienceDocument>(File.ReadAllText(FileFor(root)))!;Check(saved.History[1].Estimate.EstimatedGain==6);
        }));
        test("experience reset fails atomically with a locked record and succeeds after retry",()=>Scratch(root=>{
            using var session=new ExperienceSession(root,clock:()=>0);Feed(session,0);Feed(session,60000);Check(session.Flush());string before=File.ReadAllText(FileFor(root));
            using(var held=new FileStream(FileFor(root),FileMode.Open,FileAccess.Read,FileShare.None)){try{session.ResetLifetime();throw new Exception("locked reset accepted");}catch(InvalidOperationException){}Check(session.LifetimeGain==6&&session.Read().EstimatedGain==6&&session.SaveError!=null);}
            Check(File.ReadAllText(FileFor(root))==before);session.ResetLifetime();Check(session.LifetimeGain==0&&session.Read().EstimatedGain==6&&session.SaveError==null&&session.Flush());
        }));
        test("v2 migration backs up original bytes before enabling lifetime reset",()=>Scratch(root=>{
            using(var seed=new ExperienceSession(root,clock:()=>0)){Feed(seed,0);Feed(seed,60000);}
            var old=JsonNode.Parse(File.ReadAllText(FileFor(root)))!.AsObject();old["Schema"]="jx3-experience-estimate-v2";old.Remove("LifetimeResetOffset");string before=old.ToJsonString();File.WriteAllText(FileFor(root),before);
            using(var session=new ExperienceSession(root,clock:()=>200000)){Check(session.LifetimeGain==6);session.ResetLifetime();Check(session.LifetimeGain==0&&session.Flush());}
            Check(Directory.GetFiles(Path.Combine(root,"experience"),"v2-*.json").Any(x=>File.ReadAllText(x)==before));using var reopened=new ExperienceSession(root,clock:()=>400000);Check(reopened.LifetimeGain==0&&reopened.Previous!.Estimate.EstimatedGain==0);
        }));
        test("invalid or missing reset offsets retain original file and reject clearing a broken record",()=>{
            foreach(string value in new[]{"missing","null","-6","12","1"})Scratch(root=>{using(var seed=new ExperienceSession(root,clock:()=>0)){Feed(seed,0);Feed(seed,60000);}var node=JsonNode.Parse(File.ReadAllText(FileFor(root)))!.AsObject();if(value=="missing")node.Remove("LifetimeResetOffset");else node["LifetimeResetOffset"]=JsonNode.Parse(value);string original=node.ToJsonString();File.WriteAllText(FileFor(root),original);using(var session=new ExperienceSession(root,clock:()=>200000)){Check(session.SaveError!=null&&session.LifetimeGain==null);try{session.ResetLifetime();throw new Exception("invalid record cleared");}catch(InvalidOperationException){}}Check(File.ReadAllText(FileFor(root))==original);});
        });
        test("queued checkpoints and concurrent observation cannot resurrect a committed reset",()=>Scratch(root=>{
            using(var session=new ExperienceSession(root,clock:()=>0)){Feed(session,0);var observer=Task.Run(()=>{for(int i=1;i<=25;i++)Feed(session,i*60000);});var resetter=Task.Run(()=>{for(int i=0;i<20;i++)session.ResetLifetime();});Task.WaitAll(observer,resetter);Check(session.Read().EstimatedGain==150);session.ResetLifetime();Check(session.LifetimeGain==0&&session.Read().EstimatedGain==150&&session.Flush());}
            using var reopened=new ExperienceSession(root,clock:()=>2000000);Check(reopened.LifetimeGain==0&&reopened.Previous!.Estimate.EstimatedGain==150);
        }));
        test("explicit reset establishes a new known zero without pretending unknown historical totals were known",()=>Scratch(root=>{
            using(var seed=new ExperienceSession(root,clock:()=>0)){Feed(seed,0);Feed(seed,60000);}var node=JsonNode.Parse(File.ReadAllText(FileFor(root)))!;node["LifetimeBeforeCurrent"]=null;node["LifetimePartial"]=true;File.WriteAllText(FileFor(root),node.ToJsonString());
            using(var session=new ExperienceSession(root,clock:()=>200000)){Check(session.LifetimeGain==null&&session.LifetimePartial);var prior=session.Previous;session.ResetLifetime();Check(session.LifetimeGain==0&&!session.LifetimePartial&&session.Previous==prior);Feed(session,200000);Feed(session,260000);Check(session.LifetimeGain==6);}
            using var reopened=new ExperienceSession(root,clock:()=>400000);Check(reopened.LifetimeGain==6&&!reopened.LifetimePartial);
        }));
        test("visibility guidance distinguishes unconfirmed entry context from absent evidence without hiding stale geometry",()=>{
            string partial=StatusSummary.VisibilityHint(new(Phase.WaitingVisibility,"已找到入口，但入口周围控件尚未确认；等待界面恢复，继续观察，不会点击"));Check(partial.Contains("完整")&&!partial.Contains("金色")&&partial.Contains("不会据此点击"));
            string absent=StatusSummary.VisibilityHint(new(Phase.WaitingVisibility,"画面证据不足"));Check(absent.Contains("遮挡")&&absent.Contains("是否已开启")&&!absent.Contains("已确认被遮挡"));
            string stale="游戏窗口坐标已变化，等待新画面重新定位；不会点击";Check(StatusSummary.VisibilityHint(new(Phase.WaitingVisibility,stale))==stale);
        });
    }
}
