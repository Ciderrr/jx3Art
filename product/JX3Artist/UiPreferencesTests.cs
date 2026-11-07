using System.IO;
namespace JX3Artist;
internal static class UiPreferencesTests
{
    internal static void Add(Action<string,Action> test)
    {
        test("UI preferences: old opt-out gets one revised preparation and confirmed choice survives reload",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var path=Path.Combine(root,"settings.json");File.WriteAllText(path,"{\"maxStarts\":37,\"skipReminder\":true,\"unattended\":true}");var old=UiPreferences.Load(path);if(!old.NeedsPreparation||!old.skipReminder||old.maxStarts!=37||!old.unattended)throw new Exception("old opt-out bypassed revised preparation or changed business settings");var confirmed=old with{preparationRevision=UiPreferences.CurrentPreparationRevision};confirmed.Save(path);if(UiPreferences.Load(path).NeedsPreparation)throw new Exception("confirmed opt-out not retained");(confirmed with{skipReminder=false}).Save(path);if(!UiPreferences.Load(path).NeedsPreparation)throw new Exception("enabled reminder suppressed");}finally{Directory.Delete(root,true);}
        });
        test("UI preferences: legacy settings retain count with reminder on",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var path=Path.Combine(root,"settings.json");File.WriteAllText(path,"{\"maxStarts\":0}");var value=UiPreferences.Load(path);if(value.maxStarts!=0||value.skipReminder||value.unattended||value.skin!="幕间"||value.startHotkey!="F7"||value.stopHotkey!="F8")throw new Exception("legacy migration");}finally{Directory.Delete(root,true);}
        });
        test("UI preferences: persist and read all settings",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var path=Path.Combine(root,"settings.json");var value=new UiPreferences(37,"幕间",true,true,"Ctrl+Alt+S","Ctrl+Alt+Q");value.Save(path);if(UiPreferences.Load(path)!=value)throw new Exception("roundtrip");value.Save(path);if(Directory.GetFiles(root).Length!=1)throw new Exception("temp leak");}finally{Directory.Delete(root,true);}
        });
        test("UI preferences: supported themes retain settings and unknown theme falls back",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var path=Path.Combine(root,"settings.json");foreach(var skin in new[]{"素幕","绛台","墨场","候场","夜演","续章","unknown"}){var old=new UiPreferences(37,skin,true,true,"Ctrl+Alt+S","Ctrl+Alt+Q");old.Save(path);if(UiPreferences.Load(path)!=(UiTheme.Palettes.ContainsKey(skin)?old:old with{skin="幕间"}))throw new Exception("retired theme migration");}}finally{Directory.Delete(root,true);}
        });
        test("UI preferences: custom positive int boundary",()=>{if(UiPreferences.ParseLimit("37")!=37||UiPreferences.ParseLimit(int.MaxValue.ToString())!=int.MaxValue)throw new Exception();});
        test("UI preferences: invalid values rejected",()=>{foreach(var value in new[]{"0","-1","2.5","abc","2147483648"}){try{UiPreferences.ParseLimit(value);}catch(ArgumentException){continue;}throw new Exception(value+" accepted");}});
        test("UI preferences: failed save cannot replace old record",()=>{
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{var path=Path.Combine(root,"settings.json");new UiPreferences().Save(path);using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){try{new UiPreferences(37).Save(path);throw new Exception("unexpected save");}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}}if(UiPreferences.Load(path).maxStarts!=3||Directory.GetFiles(root).Length!=1)throw new Exception("old data lost");}finally{Directory.Delete(root,true);}
        });
        test("UI themes: twelve named profiles have complete semantic colors",()=>{if(UiTheme.Palettes.Count!=12||!UiTheme.Palettes.ContainsKey("幕间")||UiTheme.Palettes.Values.Any(p=>p.Length!=7))throw new Exception();});
    }
}
