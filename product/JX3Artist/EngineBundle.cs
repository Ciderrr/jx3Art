using System.IO;
namespace JX3Artist;
internal static class EngineBundle
{
    internal static readonly string[] Files={"diagnostics/one_click/locate.py","int1/calibration/frame_geometry.py","int1/calibration/portable_probe.py","int1/calibration/tiled_probe.py","int1/calibration/portable_locator.py","int1/calibration/appearance_features.py","int1/calibration/d1_profile.py","int1/calibration/entry_context.py","int1/calibration/location_guard.py","int1/calibration/panel_locator.py","int1/calibration/assets/panel_painted_v1.png","int1/calibration/assets/panel_painted_mask_v1.png","int1/calibration/assets/panel_clock_v1.png","int1/calibration/assets/panel_roses_v1.png","int1/calibration/assets/start_bar_gold_v1.png","vis2/detector/detector_lib.py","vis2/template/d1_icon_template.png"};
    internal static string[] Missing(string root)=>Files.Where(file=>{var p=Path.Combine(root,file);return !File.Exists(p)||new FileInfo(p).Length==0;}).ToArray();
    internal static void Validate(string root){var missing=Missing(root);if(missing.Length>0)throw new IOException("识别组件不完整："+string.Join("，",missing));}
    internal static void AddTests(Action<string,Action> test)
    {
        test("published recognizer bundle contains all required nonempty files",()=>Validate(Path.Combine(AppContext.BaseDirectory,"engine")));
        test("published recognizer runs from unrelated working directory without game",()=>{
            var root=Path.Combine(AppContext.BaseDirectory,"engine");Validate(root);var file=Path.Combine(Path.GetTempPath(),"jx3-bundle-"+Guid.NewGuid().ToString("N")+".bmp");
            try{
                var frame=System.Windows.Media.Imaging.BitmapSource.Create(3840,2088,96,96,System.Windows.Media.PixelFormats.Bgr32,null,new byte[3840*2088*4],3840*4);
                var encoder=new System.Windows.Media.Imaging.BmpBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));using(var output=File.Create(file))encoder.Save(output);
                var start=RecognizerProcess.ForFrame(Path.Combine(root,Files[0]),file);start.WorkingDirectory=Path.GetTempPath();using var process=System.Diagnostics.Process.Start(start)??throw new IOException("recognizer did not launch");
                var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(10000)){process.Kill(true);process.WaitForExit();throw new TimeoutException("bundle smoke timed out");}
                var text=stdout.GetAwaiter().GetResult();var error=stderr.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException(error);
                using var json=System.Text.Json.JsonDocument.Parse(text);if(json.RootElement.GetProperty("location").GetProperty("status").GetString()!="NO_VERIFIED_CANDIDATE"||json.RootElement.GetProperty("entry_context").GetProperty("confirmed").GetBoolean())throw new Exception("blank frame authorized");
            }finally{if(File.Exists(file))File.Delete(file);}
        });
        test("missing recognizer bundle fails closed",()=>{var missing=Missing(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")));if(missing.Length!=Files.Length)throw new Exception("missing component not detected");});
        test("empty recognizer files fail validation",()=>{var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{Directory.CreateDirectory(Path.Combine(root,"diagnostics/one_click"));File.WriteAllText(Path.Combine(root,Files[0]),"");if(!Missing(root).Contains(Files[0]))throw new Exception("empty accepted");}finally{Directory.Delete(root,true);}});
    }
}
