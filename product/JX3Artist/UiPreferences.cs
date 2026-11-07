using System.IO;
using System.Text.Json;
namespace JX3Artist;
internal sealed record UiPreferences(int maxStarts=3,string skin="幕间",bool skipReminder=false,bool unattended=false,string startHotkey="F7",string stopHotkey="F8",int preparationRevision=0,string appIcon="01-yunxiu")
{
    internal const int CurrentPreparationRevision=1;
    internal bool NeedsPreparation=>!skipReminder||preparationRevision<CurrentPreparationRevision;
    internal static int ParseLimit(string value)=>int.TryParse(value,out int n)&&n>0?n:throw new ArgumentException("请输入1到2147483647之间的整数");
    internal static UiPreferences Load(string path)
    {
        if(!File.Exists(path))return new();
        var p=JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(path))??new();
        if(p.maxStarts<0)throw new InvalidDataException("运行次数设置无效");return UiTheme.Palettes.ContainsKey(p.skin)?p:p with{skin="幕间"};
    }
    internal void Save(string path)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
