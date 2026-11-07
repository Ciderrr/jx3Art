using System.Windows.Input;
using WpfKey=System.Windows.Input.Key;
namespace JX3Artist;
internal sealed record AssistantHotkey(uint Modifiers,uint Key,string Label)
{
    internal static AssistantHotkey Parse(string text)
    {
        KeyGesture gesture;
        try{gesture=(KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(text)!;}catch{throw new ArgumentException("请输入功能键或组合键，例如 F7、Ctrl+Alt+S");}
        if(gesture==null||gesture.Key is WpfKey.None or WpfKey.F12||gesture.Modifiers.HasFlag(ModifierKeys.Windows)||gesture.Modifiers==ModifierKeys.None&&gesture.Key is not (>=WpfKey.F1 and <=WpfKey.F24))throw new ArgumentException("请选择F1～F24（F12除外），或Ctrl/Alt组合键");
        if(gesture.Key is WpfKey.LeftCtrl or WpfKey.RightCtrl or WpfKey.LeftAlt or WpfKey.RightAlt or WpfKey.LeftShift or WpfKey.RightShift)throw new ArgumentException("请选择完整组合键");
        if(gesture.Key is not (>=WpfKey.F1 and <=WpfKey.F24)&&!gesture.Modifiers.HasFlag(ModifierKeys.Control)&&!gesture.Modifiers.HasFlag(ModifierKeys.Alt))throw new ArgumentException("普通按键须搭配Ctrl或Alt");
        uint modifiers=0x4000;if(gesture.Modifiers.HasFlag(ModifierKeys.Alt))modifiers|=1;if(gesture.Modifiers.HasFlag(ModifierKeys.Control))modifiers|=2;if(gesture.Modifiers.HasFlag(ModifierKeys.Shift))modifiers|=4;
        return new(modifiers,(uint)KeyInterop.VirtualKeyFromKey(gesture.Key),new KeyGestureConverter().ConvertToInvariantString(gesture)!);
    }
}
internal sealed class AssistantHotkeys(Func<int,AssistantHotkey,bool> register,Action<int> unregister):IDisposable
{
    internal const int StartId=0x4A57,StopId=0x4A58;
    readonly Dictionary<int,AssistantHotkey> active=new();
    internal bool Registered(int id)=>active.ContainsKey(id);
    void Clear(){foreach(int id in active.Keys.ToArray())unregister(id);active.Clear();}
    bool Add(int id,AssistantHotkey key){if(!register(id,key))return false;active[id]=key;return true;}
    internal string? Apply(string start,string stop)
    {
        var a=AssistantHotkey.Parse(start);var b=AssistantHotkey.Parse(stop);
        if(a.Key==b.Key&&a.Modifiers==b.Modifiers)return "开始与停止快捷键不能相同";
        var old=active.ToArray();Clear();
        if(Add(StartId,a)&&Add(StopId,b))return null;
        if(old.Length==0){Clear();Add(StopId,b);Add(StartId,a);return "部分快捷键已被占用，请使用按钮或重新设置";}
        Clear();bool restored=true;foreach(var pair in old)restored&=Add(pair.Key,pair.Value);
        return restored?"快捷键已被占用，请选择其他按键；原快捷键保持不变":"快捷键设置失败，部分原快捷键也未能恢复，请使用按钮并重新设置";
    }
    public void Dispose()=>Clear();
    internal static void AddTests(Action<string,Action> test)
    {
        test("hotkeys reject duplicate reserved and plain typing keys",()=>{
            foreach(var key in new[]{"F12","A","Shift+A","Win+S","bad"}){try{AssistantHotkey.Parse(key);}catch(ArgumentException){continue;}throw new Exception("unsafe hotkey accepted: "+key);}
            var pair=new AssistantHotkeys((_,_)=>true,_=>{});if(pair.Apply("F7","F7")==null)throw new Exception("duplicate accepted");
            if(AssistantHotkey.Parse("Ctrl+Alt+S").Modifiers!=0x4003)throw new Exception("modifier ABI");
        });
        test("hotkey conflict rolls back both registrations and releases only owned IDs",()=>{
            var native=new Dictionary<int,AssistantHotkey>();bool deny=false;var pair=new AssistantHotkeys((id,key)=>{if(deny&&key.Key==0x79)return false;native.Add(id,key);return true;},id=>{if(!native.Remove(id))throw new Exception("unregister not owned");});
            if(pair.Apply("F7","F8")!=null)throw new Exception("initial registration");deny=true;if(pair.Apply("F9","F10")==null||native.Count!=2||native[StartId].Key!=0x76||native[StopId].Key!=0x77)throw new Exception("rollback failed");
            deny=false;if(pair.Apply("F8","F7")!=null||native[StopId].Key!=0x76)throw new Exception("swap failed");pair.Dispose();pair.Dispose();if(native.Count!=0)throw new Exception("hotkey leak");
        });
    }
}
