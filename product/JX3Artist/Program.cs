using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
namespace JX3Artist;

internal static class Program
{
    [DllImport("user32.dll",SetLastError=true)]static extern nint SetThreadDpiAwarenessContext(nint context);
    [STAThread]
    static int Main(string[] args)
    {
        // dotnet-hosted offline UI checks do not inherit the apphost manifest.
        // Establish the same physical-pixel context; retain the geometry guard.
        if(args.Contains("--ui-interaction-test")||args.Contains("--ui-offscreen")||args.Contains("--demo")||args.Contains("--web-ui-test"))
        {
            if(!Int1.Win32RO.AreDpiAwarenessContextsEqual(Int1.Win32RO.GetThreadDpiAwarenessContext(),Int1.Win32RO.PMv2))
            {
                Int1.Win32RO.SetProcessDpiAwarenessContext(Int1.Win32RO.PMv2);
                SetThreadDpiAwarenessContext(Int1.Win32RO.PMv2);
            }
            if(!Int1.Win32RO.AreDpiAwarenessContextsEqual(Int1.Win32RO.GetThreadDpiAwarenessContext(),Int1.Win32RO.PMv2))return 9;
        }
        if(args.Contains("--self-test"))return ProductTests.Run(args);
        if(args.Contains("--web-ui-test")||args.Contains("--ui-offscreen"))return WebUiDiagnostics.Run(args);
        if(args.Contains("--preparation-foreground-smoke"))
        {
            int i=Array.IndexOf(args,"--out");if(i<0||i+1>=args.Length)return 2;string output=Path.GetFullPath(args[i+1]);
            var app=new Application();var window=new MainWindow(Path.Combine(output,"data"),"not-used",true){ShowActivated=false};
            window.Loaded+=(_,_)=>window.BeginPreparationForegroundSmoke(output);return app.Run(window);
        }
        if(args.Contains("--resource-smoke"))
        {
            int outputIndex=Array.IndexOf(args,"--out");if(outputIndex<0 || outputIndex+1>=args.Length)return 2;
            string root=Path.GetFullPath(args[outputIndex+1]);
            using(var recording=new ResourceRecording(Path.Combine(root,"session"),()=>ResourceRecording.ReadCounters(root),TimeSpan.FromSeconds(1)))
            {
                Thread.Sleep(3200);if(recording.Health.Samples<3 || recording.Health.Errors!=0)return 3;
            }
            return 0; // Own-process counters and filesystem only: no GUI/capture/input.
        }
        if(args.Contains("--window-replay"))return WindowReplay.Run(args);
        if(args.Contains("--observe-window"))return WindowObservation.Run(args);
        if(args.Contains("--observe-countdown"))return CountdownObservation.Run(args);
        if(args.Contains("--countdown-replay"))return CountdownReplay.Run(args);
        if(args.Contains("--diagnose-countdown"))return CountdownOcrDiagnostics.Run(args);
        if(args.Contains("--countdown-benchmark"))return CountdownBench.Run(args);
        if(args.Contains("--legacy-ui-offscreen"))
        {
            int i=Array.IndexOf(args,"--out");string path=Path.GetFullPath(args[i+1]);Directory.CreateDirectory(path);
            var app=new Application();var window=new MainWindow(Path.Combine(path,"data"),"not-used",true);
            window.SaveOffscreen(Path.Combine(path,"tracking.png"),new(Phase.Performing,"已接管正在进行的表演：只观察，自然结束后再续演，不重复点击",CountdownEnd:Int1.FrameTiming.NowMs()+1199000,CountdownStatus:"倒计时已确认"));
            window.SaveOffscreen(Path.Combine(path,"waiting.png"),new(Phase.WaitingVisibility,"画面证据不足；等待界面恢复，继续观察，不会点击",CountdownStatus:"未确认，等待新画面"));
            window.SaveOffscreen(Path.Combine(path,"minimum.png"),new(Phase.Stopped,"等待开始：已明确提示前台接管和鼠标移动"),900,650);
            foreach(var skin in UiTheme.Palettes.Keys){window.PreviewSkin(skin);window.SaveOffscreen(Path.Combine(path,"skin-"+skin+".png"),new(Phase.Performing,"正在观察表演",2,CountdownEnd:Int1.FrameTiming.NowMs()+1122000));}
            window.SaveOffscreen(Path.Combine(path,"failed-start.png"),new(Phase.Paused,"点击后未确认进入表演，请检查当前位置的地面是否允许开启表演；也可能是画面反馈未识别。调整位置后重新开始辅助"),900,650);
            window.Close();app.Shutdown();return 0;
        }
        bool uiChecks=args.Contains("--ui-interaction-test");
        bool smoke=args.Contains("--ui-smoke"),demo=uiChecks || smoke || args.Contains("--demo");
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"JX3ArtistAutomation");
        if(demo){int i=Array.IndexOf(args,"--out");folder=Path.Combine(Path.GetFullPath(i>=0 && i+1<args.Length?args[i+1]:"ui_demo_"+Guid.NewGuid().ToString("N")),"data");}
        try
        {
            bool first=true;using var mutex=demo?null:new Mutex(true,"Local\\JX3ArtistAutomation-v01",out first);
            if(!first && !demo){MessageBox.Show("软件已在运行，请从任务栏打开。","艺人助手");return 0;}
            Directory.CreateDirectory(folder);var app=new Application();MainWindow? window=null;WebHostWindow? webWindow=null;
            app.DispatcherUnhandledException+=(_,e)=>{e.Handled=true;window?.StopForError(e.Exception.Message);webWindow?.Fail("界面运行异常，辅助已停止。请重新打开助手。");try{File.AppendAllText(Path.Combine(folder,"ui_errors.log"),DateTime.UtcNow+" "+e.Exception+Environment.NewLine);}catch{}};
            if(!uiChecks&&!smoke)
            {
                if(!demo)
                {
                    app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                    if(!StartupEnvironment.Ensure(folder))return 4;
                    app.ShutdownMode=ShutdownMode.OnMainWindowClose;
                }
                int code;
                using(var recording=demo?null:new ResourceRecording(folder))
                using(var experience=demo?null:new ExperienceSession(folder))
                {
                    webWindow=new WebHostWindow(folder,Path.Combine(AppContext.BaseDirectory,"engine"),demo,recording,experience);
                    app.MainWindow=webWindow;
                    code=app.Run(webWindow);
                }
                if(webWindow.PendingUpdate is UpdateLaunch update)
                {
                    mutex?.ReleaseMutex();
                    update.Start(ReleaseUpdater.InstallDirectory);
                }
                return code;
            }
            using var legacyRecording=demo?null:new ResourceRecording(folder);
            using var legacyExperience=demo?null:new ExperienceSession(folder);
            window=new MainWindow(folder,Path.Combine(AppContext.BaseDirectory,"engine"),demo,legacyRecording,legacyExperience);
            if(uiChecks)
            {
                window.nativeUiTesting=true; // Set before Show/Loaded: static layout checks must not start entrance clocks.
                app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-12000;window.Top=-12000;window.ShowActivated=false;window.ShowInTaskbar=false;
                window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(()=>{int code;try{code=window.CheckNativeUi(Directory.GetParent(folder)!.FullName);}catch(Exception ex){File.WriteAllText(Path.Combine(Directory.GetParent(folder)!.FullName,"ui-test-error.txt"),ex.ToString());code=9;}finally{window.Close();}app.Shutdown(code);}));
            }
            if(smoke)
            {
                window.Loaded+=(_,_)=>window.BeginDemo();
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(2200)};
                timer.Tick+=(_,_)=>{timer.Stop();try{window.SaveUi(Path.Combine(Directory.GetParent(folder)!.FullName,"window.png"));}finally{window.Close();}};timer.Start();
            }
            return app.Run(window);
        }
        catch(Exception ex){MessageBox.Show("软件未能启动："+ex.Message+"\n请保留完整程序目录。","艺人助手",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
    }
}
