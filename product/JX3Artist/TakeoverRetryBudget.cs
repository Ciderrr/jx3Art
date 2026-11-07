namespace JX3Artist;
internal sealed class TakeoverRetryBudget
{
    internal int Attempts{get;private set;}
    internal double Expires{get;private set;}
    bool pending;
    internal bool CanResume(double now)=>pending&&double.IsFinite(now)&&now<Expires;
    internal void Begin(double now){pending=false;Attempts=0;Expires=now+30000;}
    internal void Grant(double now){Begin(now);pending=true;}
    internal bool Defer(double now){pending=++Attempts<3&&double.IsFinite(now)&&now<Expires;return pending;}
    internal void Clear(){pending=false;Attempts=0;Expires=0;}
    internal static void AddTests(Action<string,Action> test)
    {
        test("retained takeover authorization expires and never leaks into a new cycle",()=>{
            var budget=new TakeoverRetryBudget();budget.Begin(1000);
            if(!budget.Defer(2000)||!budget.CanResume(3000)||!budget.Defer(4000)||budget.Defer(5000)||budget.CanResume(5000))throw new Exception("retry count budget");
            budget.Begin(1000);budget.Defer(2000);if(budget.CanResume(31000)||budget.CanResume(double.NaN))throw new Exception("expired grant");
            budget.Begin(1000);budget.Defer(2000);budget.Clear();if(budget.CanResume(2500))throw new Exception("grant leaked across performance");
            budget.Grant(1000);if(!budget.CanResume(2000)||budget.CanResume(31000))throw new Exception("preparation lost approval or retained expired approval");
        });
    }
}
