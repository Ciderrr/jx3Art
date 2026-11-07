using System.Windows;
using System.Windows.Media;
namespace JX3Artist;
internal static class UiTheme
{
    internal static readonly Dictionary<string,string[]> Palettes=new()
    {
        ["幕间"] = new[]{"#fdfdfc","#21201c","#63635e","#dad9d6","#f1f0ef","#a18072","#7d5e54"},
        ["绛台"] = new[]{"#fdfcfc","#21201c","#63635e","#dfcdc5","#f6edea","#8c333a","#8c333a"},
        ["候场"] = new[]{"#f9f9f8","#21201c","#63635e","#dad9d6","#f1f0ef","#957468","#7d5e54"},
        ["墨场"] = new[]{"#1c1c1e","#f2f2f7","#b4b4b4","#48484a","#2c2c2e","#a4c4e0","#a4c4e0"},
        ["素幕"] = new[]{"#fcfcfc","#202020","#646464","#d9d9d9","#f0f0f0","#3e63dd","#3a5bc7"},
        ["续章"] = new[]{"#f4fbf7","#1d3b31","#5f6563","#acdec8","#e6f7ed","#208368","#208368"},
        ["清笺"] = new[]{"#fdfdfc","#21201c","#63635e","#dad9d6","#f1f0ef","#208368","#208368"},
        ["听雨"] = new[]{"#f7f9ff","#1f2d5c","#60646c","#c1d0ff","#edf2fe","#3e63dd","#3a5bc7"},
        ["流光"] = new[]{"#fcfcfd","#1c2024","#60646c","#d9d9e0","#f0f0f3","#087f8c","#087f8c"},
        ["夜演"] = new[]{"#1c1c1e","#f2f2f7","#b4b4b4","#48484a","#2c2c2e","#c2c2c4","#c2c2c4"},
        ["朱弦"] = new[]{"#fdfdfc","#21201c","#63635e","#dad9d6","#f1f0ef","#8d3528","#8d3528"},
        ["青岚"] = new[]{"#f4fbf7","#1d3b31","#5f6563","#acdec8","#e6f7ed","#208368","#208368"},
    };
    internal static void Apply(Window window,string name)
    {
        var colors=Palettes.GetValueOrDefault(name)??Palettes["幕间"];
        var keys=new[]{"Paper","Ink","Muted","Line","Surface","Accent","AccentText"};

        var stopColors=name is "墨场" or "夜演"?new[]{"#3b1219","#ffd1d9","#8c333a"}:new[]{"#feebec","#641723","#f4a9aa"};
        for(int i=0;i<3;i++){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(stopColors[i]));brush.Freeze();window.Resources[new[]{"StopFill","StopInk","StopEdge"}[i]]=brush;}
        for(int i=0;i<keys.Length;i++){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));brush.Freeze();window.Resources[keys[i]]=brush;}
    }
}
