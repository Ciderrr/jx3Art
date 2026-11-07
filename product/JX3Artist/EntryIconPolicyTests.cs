using System.Text.Json;
namespace JX3Artist;
internal static class EntryIconPolicyTests
{
    internal static void Add(Action<string,Action> test)
    {
        Observation Icon(double source)=>new(source,source+100,true,true,"UNIQUE_CANDIDATE",1,"UNKNOWN",EntryContextConfirmed:false,PanelLocation:"NO_VERIFIED_PANEL");
        test("icon-only policy denies missing ambiguous stale held-conflict and active-panel evidence",()=>{
            Func<Observation,Observation>[] invalid={o=>o with{Location="NO_VERIFIED_CANDIDATE",Count=0},o=>o with{Location="AMBIGUOUS",Count=2},o=>o with{Location="SEARCH_LIMIT"},o=>o with{Count=2},o=>o with{D2="VISIBLE"},o=>o with{D2="NOT_EVALUABLE"},o=>o with{PanelLocation="AMBIGUOUS"},o=>o with{PanelLocation="SEARCH_LIMIT"},o=>o with{PanelLocation="UNRESOLVED_PANEL_CANDIDATE"},o=>o with{PanelLocation="SCALE_CONFLICT"},o=>o with{PanelLocated=true,PanelLocation="UNIQUE_PANEL"},o=>o with{Identity=false},o=>o with{Geometry=false},o=>o with{Now=o.Source+3001},o=>o with{Now=o.Source-1},o=>o with{Source=double.NaN}};
            foreach(var change in invalid){var core=new AutomationController(3,1000);core.Observe(change(Icon(2000)));core.Observe(change(Icon(3000)));if(core.WantsStart)throw new Exception("non-icon guard weakened");}
        });
        test("two unique icon observations without gold pass production JSON parsing while native stop still revokes",()=>{
            const string json="{\"location\":{\"status\":\"UNIQUE_CANDIDATE\",\"candidates\":[{\"bbox\":[420,189,492,255]}]},\"d2\":{\"state\":\"UNKNOWN\"},\"d2_location\":{\"status\":\"NO_VERIFIED_PANEL\",\"verified\":false},\"entry_context\":{\"confirmed\":false}}";
            Observation Parse(double source)=>GameSession.ReadObservation(json,source,source+100,true,true,"isolated",out _);
            var core=new AutomationController(3,1000);core.Observe(Parse(2000));if(core.WantsStart)throw new Exception("single frame authorized");core.Observe(Parse(3000));if(!core.WantsStart||core.Starts!=0)throw new Exception("icon-only entry rejected or fabricated start");core.Stop();core.Observe(Parse(4000));if(core.WantsStart||core.State!=Phase.Stopped)throw new Exception("stop resurrected");
        });
    }
}
