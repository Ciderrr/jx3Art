using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Int1;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JX3Artist;
internal sealed class WebHostWindow:Window
{
    readonly string dataRoot;readonly bool demo;readonly bool privileged;readonly ExperienceSession experience;readonly bool ownsExperience;readonly ResourceRecording? recording;
    internal readonly AutomationService Service;internal readonly WebView2 Browser=new();readonly DispatcherTimer timer;
    readonly Grid root=new();readonly StackPanel emergency=new(){Margin=new Thickness(32)};readonly TextBlock emergencyText=new(){TextWrapping=TextWrapping.Wrap,FontSize=16};
    UiPreferences preferences=new();AssistantHotkeys? hotkeys;WindowsNotice? notice;HwndSource? source;nint handle;bool ready,closing,allowClose,ever;DateTime closeAt;string? uiError;long epoch,revision,lastId;string? pendingPreparation;string pendingOrigin="button";double lastHeartbeat;int activePlan;double demoDeadline;bool editing;string? captureTarget;
    internal bool Diagnostic;internal bool Ready=>ready;internal bool Editing=>editing;internal string? CaptureTarget=>captureTarget;internal long Epoch=>epoch;
    internal ViewState? Fixture;internal bool? FixtureRunning;
    internal Func<Task<string>>? ShortcutForTest;internal Func<string,bool>? ShellForTest;internal Action<string>? ClipboardForTest;internal Func<string?>? SaveReportPathForTest;
    string iconPreview="";
    bool disposed,resettingExperience,uploadingIcon,checkingUpdate;
    internal UpdateLaunch? PendingUpdate { get; private set; }string? pendingReportPreview,pendingExperienceReset;
    internal ExperienceSession Experience=>experience;
    string SettingsPath=>Path.Combine(dataRoot,"settings.json");
    static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal WebHostWindow(string folder,string engine,bool demo,ResourceRecording? recording=null,ExperienceSession? experience=null)
    {
        dataRoot=folder;this.demo=demo;privileged=demo||InputPreflight.Read((uint)Environment.ProcessId).Elevated==true;this.recording=recording;Directory.CreateDirectory(folder);ownsExperience=experience==null;this.experience=experience??new ExperienceSession(folder,memoryOnly:demo);
        try{preferences=UiPreferences.Load(SettingsPath);}catch(Exception){uiError="设置读取失败，当前使用默认设置。";}
        if(!privileged)uiError="请正常双击助手并完成Windows管理员授权后再开始。";activePlan=preferences.maxStarts;Service=new(folder,engine,demo,this.experience);Service.Mode.Set(preferences.unattended);Service.RestorePreparationForeground=demo?null:RestoreAssistant;
        Title=demo?"艺人助手 · 隔离演示（零游戏操作）":"剑网3 艺人助手";Width=960;Height=700;MinWidth=900;MinHeight=650;Background=new SolidColorBrush(Color.FromRgb(253,253,252));WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
        try{AppIcon.SelectedPath=IconChoices.Resolve(dataRoot,preferences.appIcon);iconPreview=IconChoices.Preview(AppIcon.FilePath);}catch{preferences=preferences with{appIcon="01-yunxiu"};AppIcon.SelectedPath=null;iconPreview=IconChoices.Preview(AppIcon.FilePath);uiError="已恢复默认图标，原图标无法读取。";}Icon=AppIcon.WindowSource();
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});
        root.Children.Add(Browser);emergency.Children.Add(emergencyText);var stop=new Button{Content="停止辅助",Margin=new Thickness(0,20,0,0),Padding=new Thickness(20,10,20,10)};stop.Click+=(_,_)=>Stop("emergency_button");emergency.Children.Add(stop);var close=new Button{Content="关闭助手",Margin=new Thickness(0,12,0,0),Padding=new Thickness(20,10,20,10)};close.Click+=(_,_)=>Close();emergency.Children.Add(close);emergency.Visibility=Visibility.Collapsed;root.Children.Add(emergency);Content=root;
        if(recording!=null)recording.Sampled+=ResourceSample;
        timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(_,_)=>Tick();Activated+=(_,_)=>lastHeartbeat=FrameTiming.NowMs();Loaded+=async(_,_)=>await Initialize();Closing+=OnClosing;Closed+=OnClosed;
    }
    void ResourceSample(object value)=>Service.History.Record("resource_sample",value);
    bool RestoreAssistant(CancellationToken token)=>Dispatcher.Invoke(()=>{if(token.IsCancellationRequested||closing)return false;Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();bool ok=new Desktop().Foreground==handle;if(!ok)Desktop.Notify(handle);return ok;});
    async System.Threading.Tasks.Task Initialize()
    {
        try{
            if(Browser.ActualWidth>0){Width+=960-Browser.ActualWidth;Height+=700-Browser.ActualHeight;MinWidth=Width-60;MinHeight=Height-50;}
            handle=new WindowInteropHelper(this).Handle;Service.AssistantWindow=handle;
            hotkeys=new((id,key)=>demo||Desktop.RegisterHotKey(handle,id,key.Modifiers,key.Key),id=>{if(!demo)Desktop.UnregisterHotKey(handle,id);});try{uiError=hotkeys.Apply(preferences.startHotkey,preferences.stopHotkey)??uiError;}catch(ArgumentException){uiError="快捷键设置无效，请在设置中重新录入；当前临时使用F7/F8。";preferences=preferences with{startHotkey="F7",stopHotkey="F8"};hotkeys.Apply("F7","F8");}
            source=HwndSource.FromHwnd(handle);source?.AddHook(Hook);if(!demo)notice=new(handle);
            var env=await CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(dataRoot,"webview-profile"));await Browser.EnsureCoreWebView2Async(env);
            var core=Browser.CoreWebView2;await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=Diagnostic;core.Settings.IsStatusBarEnabled=false;core.Settings.AreBrowserAcceleratorKeysEnabled=false;
            core.Settings.IsNonClientRegionSupportEnabled=true;
            core.SetVirtualHostNameToFolderMapping("jx3artist.local",Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.NavigationStarting+=(_,e)=>{if(!WebUiProtocol.Trusted(e.Uri))e.Cancel=true;else if(ready){Stop("ui_navigation");ready=false;lastId=0;}};core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.WebMessageReceived+=OnMessage;core.ProcessFailed+=(_,_)=>Fail("界面进程异常，辅助已停止。请重新打开助手。");
            core.NavigationCompleted+=(_,e)=>{if(!e.IsSuccess)Fail("界面加载失败，辅助已停止。");};
            lastHeartbeat=FrameTiming.NowMs();timer.Start();core.Navigate(WebUiProtocol.Origin+"/index.html");
        }catch(Exception ex){if(Diagnostic)File.WriteAllText(Path.Combine(Directory.GetParent(dataRoot)!.FullName,"web-initialize-error.txt"),ex.ToString());Fail("界面未能初始化。请确认WebView2 Runtime和完整程序目录可用。");}
    }
    nint Hook(nint hwnd,int msg,nint w,nint l,ref bool handled)
    {
        if(msg==0x312&&hotkeys?.Registered((int)w)==true){DispatchHotkey((int)w);handled=true;}
        if(WindowsNotice.OpensAssistant(msg,l)){Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();handled=true;}return 0;
    }
    internal void DispatchHotkey(int id)
    {
        if(captureTarget!=null&&editing&&(id!=AssistantHotkeys.StopId||!Service.IsRunning)){
            var target=captureTarget;captureTarget=null;Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new{kind="capture",target,value=id==AssistantHotkeys.StartId?preferences.startHotkey:preferences.stopHotkey},Json));return;
        }
        if(id==AssistantHotkeys.StopId)Stop("shortcut");else if(id==AssistantHotkeys.StartId&&!editing&&!closing&&ready&&pendingPreparation==null&&CanStart)RequestStart("shortcut");
    }
    internal void Fail(string message){uiError=message;Stop("web_ui_error");Browser.Visibility=Visibility.Collapsed;emergencyText.Text=message;emergency.Visibility=Visibility.Visible;}
    internal void Stop(string origin="button"){epoch++;pendingPreparation=null;pendingExperienceReset=null;captureTarget=null;editing=false;Service.Stop(origin);Publish();}
    bool CanStart=>!closing&&PendingUpdate==null&&!uploadingIcon&&!checkingUpdate&&!resettingExperience&&pendingExperienceReset==null&&!Service.IsRunning&&privileged&&Browser.Visibility==Visibility.Visible;
    void RequestStart(string origin)
    {
        if(!CanStart)throw new InvalidOperationException(Service.IsRunning?"辅助正在运行。":"当前不能开始，请检查权限或界面状态。");
        if(preferences.NeedsPreparation){pendingOrigin=origin;pendingPreparation=Guid.NewGuid().ToString("N");}
        else Start(origin);
    }
    void Start(string origin){demoDeadline=0;pendingPreparation=null;activePlan=preferences.maxStarts;if(Service.Start(activePlan,preferences.unattended,origin))ever=true;}
    void Save(UiPreferences next){next.Save(SettingsPath);preferences=next;Service.History.Record("settings_changed",new{success=true});}
    void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(disposed)return;
        // Leave the WebView2 callback before dispatching any native modal operation.
        string source=e.Source,json=e.WebMessageAsJson;
        Dispatcher.BeginInvoke(new Action(async()=>await HandleMessage(source,json)),DispatcherPriority.Normal);
    }
    async Task HandleMessage(string source,string json)
    {
        if(disposed)return;
        long id=0;try{var c=WebUiProtocol.Parse(source,json);id=c.Id;if(c.Id<=lastId)throw new ArgumentException("重复或乱序请求");lastId=c.Id;if(closing&&c.Type!="stop")throw new InvalidOperationException("助手正在关闭");
            if(WebUiProtocol.RequiresCurrentEpoch(c.Type)&&c.Epoch!=epoch)throw new InvalidOperationException("操作许可已过期，请重新操作。");
            object? result;
            if(c.Type=="confirmExperienceReset"){
                if(resettingExperience||pendingExperienceReset==null||c.Payload.GetProperty("token").GetString()!=pendingExperienceReset||Service.IsRunning||pendingPreparation!=null)throw new InvalidOperationException("清零确认已失效，请停止辅助后重新操作。");
                resettingExperience=true;try{await Task.Run(experience.ResetLifetime);pendingExperienceReset=null;Service.History.Record("settings_changed",new{success=true});result="累计经验已清零。";}finally{resettingExperience=false;}
            }
            else if(c.Type=="uploadIcon"){if(Service.IsRunning)throw new InvalidOperationException("请先停止辅助，再上传图标。");uploadingIcon=true;try{var picker=new Microsoft.Win32.OpenFileDialog{Filter="图片 (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",Title="选择助手图标"};if(picker.ShowDialog(this)==true){string importedIcon=IconChoices.Import(dataRoot,picker.FileName);SelectIcon(importedIcon);result="自定义图标已应用。";}else result="已取消选择。";}finally{uploadingIcon=false;}}
            else if(c.Type=="checkUpdate")
            {
                if(Service.IsRunning || Service.IsStopping) throw new InvalidOperationException("请先停止辅助，再更新助手。");
                if(demo) result="隔离演示中可查看更新入口。";
                else
                {
                    if(checkingUpdate) throw new InvalidOperationException("更新窗口已经打开。");
                    checkingUpdate=true;
                    try
                    {
                        PendingUpdate=UpdateDialog.Show(this,dataRoot);
                        if(PendingUpdate!=null) _ = Dispatcher.BeginInvoke(new Action(Close));
                        result=null;
                    }
                    finally{checkingUpdate=false;}
                }
            }
            else if(c.Type=="createShortcut"){
                if(ShortcutForTest!=null)result=await ShortcutForTest();else{var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"Create-DesktopShortcut.ps1")})start.ArgumentList.Add(arg);using var process=Process.Start(start)??throw new InvalidOperationException("快捷方式未能创建。");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();await output;await errors;if(process.ExitCode!=0)throw new InvalidOperationException("快捷方式未能创建，请检查桌面写入权限。");result="桌面快捷方式已创建。";}
            }
            else if(c.Type=="copyEmail")result=await CopyEmail();
            else if(c.Type is "openProfile" or "openAuthor" or "openIssues"){
                string target=c.Type switch {
                    "openProfile"=>"https://github.com/Ciderrr/jx3Art",
                    "openAuthor"=>"https://github.com/Ciderrr",
                    _=>"https://github.com/Ciderrr/jx3Art/issues"
                };
                await Task.Run(()=>Open(target));
                result=null;
            }else result=Execute(c.Type,c.Payload);
            Publish();Reply(id,true,null,result);
        }catch(Exception ex){if(disposed)return;Service.History.Record("settings_changed",new{success=false});Reply(id,false,ex is ArgumentException or InvalidOperationException?ex.Message:"操作未完成，请重试或查看记录。",null);Publish();}
    }
    internal object? Execute(string type,JsonElement p)
    {
        switch(type){
            case "requestExperienceReset":if(Service.IsRunning||closing||resettingExperience||pendingPreparation!=null)throw new InvalidOperationException("请先停止辅助，再清零累计经验。");pendingExperienceReset=Guid.NewGuid().ToString("N");return pendingExperienceReset;
            case "cancelExperienceReset":if(!resettingExperience)pendingExperienceReset=null;break;
            case "ready":ready=true;lastHeartbeat=FrameTiming.NowMs();if(!Diagnostic&&preferences.NeedsPreparation&&!ever&&CanStart&&pendingPreparation==null)RequestStart("button");break;
            case "minimize":WindowState=WindowState.Minimized;break;
            case "maximize":WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;break;
            case "closeWindow":Close();break;
            case "heartbeat":lastHeartbeat=FrameTiming.NowMs();break;
            case "uiContext":editing=WebUiProtocol.Boolean(p,"editing");if(!editing)captureTarget=null;break;
            case "capture":string? target=p.GetProperty("target").GetString();if(target is not (null or "start" or "stop"))throw new ArgumentException("录入目标无效");captureTarget=target;break;
            case "uiFailure":Fail("界面运行异常，辅助已停止。请重新打开助手。");break;
            case "requestStart":if(editing)throw new InvalidOperationException("请先关闭设置或计划弹层。");RequestStart("button");break;
            case "confirmStart":
                if(pendingPreparation==null||p.GetProperty("token").GetString()!=pendingPreparation)throw new InvalidOperationException("准备确认已失效，请重新开始。");
                bool skip=WebUiProtocol.Boolean(p,"skip");try{Save(preferences with{skipReminder=skip,preparationRevision=UiPreferences.CurrentPreparationRevision});}catch{uiError="提醒设置未保存，下次仍会提示。";}Start(pendingOrigin);break;
            case "stop":Stop();break;
            case "cancelPreparation":pendingPreparation=null;break;
            case "approve":if(!Service.IsRunning||Service.IsStopping||!Service.Latest.AwaitingApproval||preferences.unattended)throw new InvalidOperationException("当前无需允许接管。");Service.ApproveTakeover();break;
            case "setAutomatic":
                bool automatic=WebUiProtocol.Boolean(p,"enabled");Service.SetUnattended(automatic);preferences=preferences with{unattended=automatic};try{Save(preferences);}catch{uiError="接管选项本次已生效，但未保存，下次仍使用旧设置。";}break;
            case "setPlan":if(Service.IsRunning)throw new InvalidOperationException("请先停止辅助再修改计划。");Save(preferences with{maxStarts=WebUiProtocol.Plan(p)});break;
            case "setReminder":Save(preferences with{skipReminder=!WebUiProtocol.Boolean(p,"enabled"),preparationRevision=UiPreferences.CurrentPreparationRevision});break;
            case "setIcon":SelectIcon(p.GetProperty("icon").GetString()??"");break;
            case "setSkin":string skin=p.GetProperty("skin").GetString()??"";if(!UiTheme.Palettes.ContainsKey(skin))throw new ArgumentException("未知皮肤");Save(preferences with{skin=skin});break;
            case "setHotkeys":
                string a=AssistantHotkey.Parse(p.GetProperty("start").GetString()??"").Label,b=AssistantHotkey.Parse(p.GetProperty("stop").GetString()??"").Label;
                string? error=hotkeys!.Apply(a,b);if(error!=null)throw new InvalidOperationException(error);var old=preferences;try{Save(old with{startHotkey=a,stopHotkey=b});}catch{hotkeys.Apply(old.startHotkey,old.stopHotkey);throw new InvalidOperationException("快捷键未保存，已恢复原设置。");}break;
            case "records":return new{message=Service.Latest.Message,detail=Service.Latest.Detail,recording=recording?.Health.Message??"隔离演示，不记录真实运行资源。",history=experience.Previous is ExperienceEntry previous?$"上次估算 +{previous.Estimate.EstimatedGain:N0}":"暂无上次经验记录",saveError=experience.SaveError,preview=ReadCachedPreview(),frameAt=Service.Latest.FrameTime?.ToString("HH:mm:ss",System.Globalization.CultureInfo.InvariantCulture)};
            case "previewReport":pendingReportPreview=SupportReport.Preview(dataRoot,Service.Latest,Service.History.Snapshot());return pendingReportPreview;
            case "saveReport":
                string preview=pendingReportPreview??throw new InvalidOperationException("请先预览问题报告。");
                string? destination;
                if(SaveReportPathForTest!=null)destination=SaveReportPathForTest();
                else{var picker=new Microsoft.Win32.SaveFileDialog{Filter="诊断包 (*.zip)|*.zip",FileName="JX3Artist-report-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip"};destination=picker.ShowDialog(this)==true?picker.FileName:null;}
                if(destination!=null){SupportReport.Export(destination,preview);return "诊断包已保存。";}return "已取消保存。";
            default:throw new ArgumentException("未知界面操作");
        }return null;
    }
    void SelectIcon(string id){string path=IconChoices.Resolve(dataRoot,id);string preview=IconChoices.Preview(path);Save(preferences with{appIcon=id});AppIcon.SelectedPath=path;Icon=AppIcon.WindowSource();iconPreview=preview;try{notice?.RefreshIcon();if(!demo)IconChoices.SyncShortcut(path);}catch{uiError="图标已保存，系统图标未能同步；请重新打开助手。";}}
    string? ReadCachedPreview()
    {
        try{string? file=Service.Latest.Preview;if(file==null)return null;string path=Path.GetFullPath(file),cache=Path.GetFullPath(Path.Combine(dataRoot,"cache"))+Path.DirectorySeparatorChar;
            if(!path.StartsWith(cache,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)||new FileInfo(path).Length>64*1024*1024)return null;
            // Reuse the previous UI's bounded display decode; never capture again or rewrite source evidence.
            using var source=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var bitmap=new System.Windows.Media.Imaging.BitmapImage();bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=750;bitmap.StreamSource=source;bitmap.EndInit();bitmap.Freeze();
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var bytes=new MemoryStream();encoder.Save(bytes);
            return "data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray());
        }catch{return null;}
    }
    const string NativeShellEmail="147984829@qq.com";
    async Task<string> CopyEmail()
    {
        for(int n=0;n<3;n++){
            try{if(ClipboardForTest!=null)ClipboardForTest(NativeShellEmail);else Clipboard.SetText(NativeShellEmail);return "邮箱已复制。";}
            catch(System.Runtime.InteropServices.ExternalException){await Task.Delay(80);}
            catch{break;}
        }
        throw new InvalidOperationException("复制失败，可选中邮箱按 Ctrl+C 复制。");
    }
    void Open(string value)
    {
        try{if(ShellForTest!=null){if(!ShellForTest(value))throw new InvalidOperationException();}else Process.Start(new ProcessStartInfo(value){UseShellExecute=true});}
        catch{throw new InvalidOperationException("未能打开浏览器，请手动访问项目的 GitHub 仓库。");}
    }
    internal object Snapshot()
    {
        var view=Fixture??Service.Latest;if(demo&&!Diagnostic&&view.Phase==Phase.Performing&&!view.CountdownEnd.HasValue){if(demoDeadline==0)demoDeadline=FrameTiming.NowMs()+1122000;view=view with{CountdownEnd=demoDeadline};}
        bool running=FixtureRunning??Service.IsRunning;bool stopping=Fixture==null&&Service.IsStopping&&running;
        double now=FrameTiming.NowMs();bool time=running&&!stopping&&view.Phase is not (Phase.Stopped or Phase.Completed or Phase.Paused)&&view.CountdownEnd.HasValue;
        int? seconds=time?CountdownTracker.DisplaySeconds(view.DisplayDeadline??view.CountdownEnd!.Value,now):null;
        string phase=view.Phase.ToString();string title=!running&&view.Phase is not (Phase.Paused or Phase.Completed or Phase.Stopped)?"辅助已停止":stopping?"正在停止":view.AwaitingApproval&&!preferences.unattended?"等待允许接管":view.Phase switch{Phase.Performing=>"表演中",Phase.Paused=>"辅助已暂停",Phase.WaitingVisibility=>"画面暂未确认",Phase.Completed=>"计划已完成",Phase.Stopped=>ever?"辅助已停止":"准备开始",Phase.Starting=>"正在开始表演",_=>"正在确认画面"};
        var estimate=experience.Read();string? health=experience.SaveError;if(Service.History.SaveErrors>0)health=(health??"")+" 问题上下文保存失败，请查看记录。";if(recording?.Health.Error!=null)health=(health??"")+" 运行诊断保存异常，请查看记录。";
        string? reason=!running&&!ever&&view.Phase==Phase.Stopped?"请开启艺人身份，站在平整路面上，开始辅助吧！":view.Phase==Phase.Paused?view.Message+"\n问题代码："+ProblemCatalog.Identify(view.Message).Code:view.Phase==Phase.WaitingVisibility?StatusSummary.VisibilityHint(view):StatusSummary.CountdownFallback(view,preferences.unattended,running&&!stopping&&!closing);
        return new{schema=1,version=typeof(WebHostWindow).Assembly.GetName().Version!.ToString(3),revision=++revision,epoch,phase,title,running,stopping,closing,ever=ever||view.Starts>0,starts=view.Starts,plan=running?activePlan:preferences.maxStarts,automatic=preferences.unattended,skin=preferences.skin,appIcon=preferences.appIcon,iconPreview,skipReminder=!preferences.NeedsPreparation,maximized=WindowState==WindowState.Maximized,startHotkey=preferences.startHotkey,stopHotkey=preferences.stopHotkey,seconds,reason,awaitingApproval=running&&!stopping&&view.AwaitingApproval&&!preferences.unattended,canStart=Fixture!=null?!running:CanStart,preparationToken=pendingPreparation,gain=estimate.EstimatedGain.ToString(System.Globalization.CultureInfo.InvariantCulture),total=experience.LifetimeGain?.ToString(System.Globalization.CultureInfo.InvariantCulture),partial=experience.LifetimePartial,health,error=uiError};
    }
    void Reply(long id,bool ok,string? error,object? result){if(disposed||Browser.CoreWebView2==null||!WebUiProtocol.Trusted(Browser.Source?.ToString()??""))return;Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{kind="reply",id,ok,error,result},Json));}
    internal void Publish(){if(disposed||!ready||Browser.CoreWebView2==null||!WebUiProtocol.Trusted(Browser.Source?.ToString()??""))return;try{Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{kind="snapshot",value=Snapshot()},Json));}catch{}}
    void Tick()
    {
        if(!closing&&IsActive&&WindowState!=WindowState.Minimized&&Service.IsRunning&&ready&&FrameTiming.NowMs()-lastHeartbeat>5000)Fail("界面响应超时，辅助已停止。");
        if(!demo&&!closing)while(Service.Notices.TryTake(out var item)){if(item==null||item.Kind=="approval"&&(Service.Mode.Enabled||!Service.Latest.AwaitingApproval))continue;bool sent=false;try{if(handle!=0)Desktop.Notify(handle);sent=notice?.Show(item.Title,item.Text,item.Warning)==true;}catch{sent=false;}if(!sent)uiError="系统通知未能发送，请查看当前辅助状态。";try{File.AppendAllText(Path.Combine(dataRoot,"notification-events.jsonl"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,item.Kind,submitted=sent})+Environment.NewLine);}catch{uiError="通知记录未能保存。";}}
        Publish();if(closing&&(!Service.IsRunning||(!Service.ButtonPending&&DateTime.UtcNow-closeAt>TimeSpan.FromSeconds(2)))){allowClose=true;Close();}
    }
    void OnClosing(object? sender,CancelEventArgs e){if(!closing){closing=true;closeAt=DateTime.UtcNow;Service.Notices.Close();notice?.Dispose();Stop("window_close");}if(!allowClose&&Service.IsRunning)e.Cancel=true;}
    void OnClosed(object? sender,EventArgs e){disposed=true;timer.Stop();Service.Stop("window_close");notice?.Dispose();hotkeys?.Dispose();source?.RemoveHook(Hook);if(recording!=null)recording.Sampled-=ResourceSample;Service.History.Record("app_close",new{});Service.History.Dispose();Browser.Dispose();if(ownsExperience)experience.Dispose();}
}
