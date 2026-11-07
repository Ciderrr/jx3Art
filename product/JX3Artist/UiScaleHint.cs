namespace JX3Artist;
// A hint only chooses where to look. Every frame still runs full classifiers.
internal sealed class UiScaleHint
{
    WindowSnapshot? window;double? scale;
    internal double? For(WindowSnapshot current)=>current==window?scale:null;
    internal void Observe(WindowSnapshot current,double value,bool confirmed)
    {
        if(confirmed&&double.IsFinite(value)&&value is >=.35 and <=2){window=current;scale=value;}
        else{window=null;scale=null;}
    }
}
