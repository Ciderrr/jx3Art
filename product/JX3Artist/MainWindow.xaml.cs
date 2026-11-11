using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Int1;
namespace JX3Artist;

public partial class MainWindow:Window
{
    readonly AutomationService service;readonly DispatcherTimer timer;readonly string dataRoot;readonly bool demo;readonly bool privileged;
    ViewState? rendered;bool closeRequested,allowClose;DateTime closeAt;HwndSource? source;nint handle;string? uiError;
    readonly ResourceRecording? recording;
    readonly ExperienceSession experience;readonly bool ownsExperience;
    WindowsNotice? notice;
    internal Func<AssistanceNotice,bool>? NotificationSink;
    AssistantHotkeys? hotkeys;Window? hotkeyDialog;
    string commandOrigin="button";
    UiPreferences preferences=new(); bool loadingUi=true,hasStarted=false; Window? preparationDialog; int chosenLimit=3;
    string SettingsPath=>Path.Combine(dataRoot,"settings.json");
    internal MainWindow(string dataRoot,string engineRoot,bool demo,ResourceRecording? recording=null,ExperienceSession? experience=null)
    {
        InitializeComponent();this.dataRoot=dataRoot;this.demo=demo;this.recording=recording;Directory.CreateDirectory(dataRoot);
        ownsExperience=experience is null;this.experience=experience??new ExperienceSession(dataRoot,memoryOnly:demo);
        service=new(dataRoot,engineRoot,demo,this.experience);privileged=demo || InputPreflight.Read((uint)Environment.ProcessId).Elevated==true;
        if(recording!=null){recording.Sampled+=RecordResource;if(recording.LatestCounters is object counters)RecordResource(counters);}
        PermissionNote.Text=privileged?"":"请正常双击软件并完成管理员授权后再开始。";
        PermissionNote.Visibility=privileged?Visibility.Collapsed:Visibility.Visible;
        if(demo)Title+=" 演示（零游戏操作）";
        try{preferences=UiPreferences.Load(SettingsPath);}catch(Exception ex){ShowUiNotice("设置读取失败，使用默认值："+ex.Message);}
        UiTheme.Apply(this,preferences.skin);ApplySkinLayout(preferences.skin);SkinMenuButton.Content=DropdownLabel(preferences.skin);ClientMenuButton.Content=DropdownLabel("旗舰端 · 单开");skinReady=true;RemindAgain.IsChecked=!preferences.skipReminder;
        foreach(ComboBoxItem item in CycleLimit.Items)if(item.Tag.ToString()==preferences.maxStarts.ToString())CycleLimit.SelectedItem=item;
        if(preferences.maxStarts>3){CycleLimit.SelectedIndex=3;CustomLimit.Text=preferences.maxStarts.ToString();}
        Unattended.IsChecked=preferences.unattended;
        loadingUi=false;PlanChanged(this,new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,new object[0],new object[0]));
        timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(_,_)=>Refresh();
        RefreshHotkeys();
        if(!demo)service.RestorePreparationForeground=RestoreForPreparation;
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};StateChanged+=(_,_)=>Refresh();
        Loaded+=(_,_)=>{timer.Start();handle=new WindowInteropHelper(this).Handle;hotkeys=new((id,key)=>demo||Desktop.RegisterHotKey(handle,id,key.Modifiers,key.Key),id=>{if(!demo)Desktop.UnregisterHotKey(handle,id);});try{var error=hotkeys.Apply(preferences.startHotkey,preferences.stopHotkey);if(error!=null)ShowUiNotice(error);}catch(ArgumentException ex){ShowUiNotice(ex.Message+"，请重新设置快捷键");}RefreshHotkeys();if(!demo){service.AssistantWindow=handle;notice=new(handle);source=HwndSource.FromHwnd(handle);source?.AddHook(Hook);}};
        Closing+=OnClosing;Closed+=(_,_)=>{timer.Stop();service.Notices.Close();notice?.Dispose();hotkeys?.Dispose();source?.RemoveHook(Hook);if(recording!=null)recording.Sampled-=RecordResource;service.History.Record("app_close",new{});service.History.Dispose();if(ownsExperience)this.experience.Dispose();};
    }
    nint Hook(nint hwnd,int message,nint w,nint l,ref bool handled){if(WindowsNotice.OpensAssistant(message,l)){Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();handled=true;}if(message==0x312&&hotkeys?.Registered((int)w)==true){DispatchHotkey((int)w);handled=true;}return 0;}
    void RecordResource(object counters)=>service.History.Record("resource_sample",counters);
    void DispatchHotkey(int id){if(id==AssistantHotkeys.StopId){CancelCurtain();service.Stop("shortcut");if(preparationDialog!=null)preparationDialog.DialogResult=false;Refresh();}else if(id==AssistantHotkeys.StartId&&hotkeyDialog==null&&preparationDialog==null){commandOrigin="shortcut";try{StartClicked(this,new RoutedEventArgs());}finally{commandOrigin="button";}}}
    void ShowUiNotice(string message){PermissionNote.Text=message;PermissionNote.Visibility=Visibility.Visible;}
    void FlushNotices()
    {
        if(closeRequested)return;
        while(service.Notices.TryTake(out var item))
        {
            if(item==null||item.Kind=="approval"&&(service.Mode.Enabled||!service.Latest.AwaitingApproval))continue;
            if(demo&&NotificationSink==null)continue;
            bool submitted=false;string? error=null;
            try{if(!demo&&handle!=0)Desktop.Notify(handle);submitted=NotificationSink?.Invoke(item)??notice?.Show(item.Title,item.Text,item.Warning)==true;}
            catch(Exception ex){error=ex.Message;}
            if(!submitted)ShowUiNotice("系统通知未能发送，请查看当前辅助状态。");
            try{File.AppendAllText(Path.Combine(dataRoot,"notification-events.jsonl"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,item.Kind,item.Title,item.Text,submitted,error})+Environment.NewLine);}catch(Exception ex){ShowUiNotice("通知记录未能保存："+ex.Message);}
        }
    }
    void RefreshHotkeys(){StartButton.Content="开始辅助 · "+preferences.startHotkey;StopButton.Content="停止辅助 · "+preferences.stopHotkey;StartButton.ToolTip=hotkeys?.Registered(AssistantHotkeys.StartId)==true?"全局快捷键："+preferences.startHotkey:"开始快捷键未启用，请重新设置";StopButton.ToolTip=hotkeys?.Registered(AssistantHotkeys.StopId)==true?"全局快捷键："+preferences.stopHotkey:"停止快捷键未启用，请重新设置";}
    bool RestoreForPreparation(CancellationToken token)
    {
        if(Dispatcher.HasShutdownStarted||token.IsCancellationRequested)return false;
        return Dispatcher.Invoke(()=>{
            if(token.IsCancellationRequested||closeRequested||handle==0)return false;
            Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();
            bool active=new Desktop().Foreground==handle;
            if(!active)Desktop.Notify(handle);
            return active;
        },DispatcherPriority.Normal,token,TimeSpan.FromMilliseconds(800));
    }
    internal void BeginPreparationForegroundSmoke(string output)
    {
        if(!demo)throw new InvalidOperationException("foreground smoke is isolated demo only");
        Directory.CreateDirectory(output);Title="艺人助手前台恢复测试（零游戏操作）";handle=new WindowInteropHelper(this).Handle;
        PermissionNote.Text="仅测试助手从后台自行恢复前台，不读取或点击游戏。";
        File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{window=handle.ToInt64(),pid=Environment.ProcessId,scope="isolated own-window foreground activation; no game capture/input"}));
        var wait=new DispatcherTimer{Interval=TimeSpan.FromSeconds(20)};
        wait.Tick+=async(_,_)=>{wait.Stop();var desktop=new Desktop();nint before=desktop.Foreground;string? processName=null;try{using var p=Process.GetProcessById((int)desktop.Pid(before));processName=p.ProcessName;}catch{}
            var recovery=new PreparationForeground();var result=await System.Threading.Tasks.Task.Run(()=>recovery.Request(FrameTiming.NowMs(),true,true,before==handle,()=>RestoreForPreparation(CancellationToken.None),CancellationToken.None));
            bool passed=before!=handle&&result.Requested&&result.Activated&&desktop.Foreground==handle;
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed,before=before.ToInt64(),beforeProcess=processName,after=desktop.Foreground.ToInt64(),result,scope="actual own-window activation from other process; no game capture/input"},new JsonSerializerOptions{WriteIndented=true}));
            var close=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};close.Tick+=(_,_)=>{close.Stop();Close();};close.Start();};wait.Start();
    }
    void PreparedChanged(object sender,RoutedEventArgs e){if(service==null)return;if(Prepared.IsChecked!=true && service.IsRunning)service.Stop("preparation_revoked");Refresh();}
    void StartClicked(object sender,RoutedEventArgs e)
    {
        if(!privileged || service.IsRunning){service.History.Record("start_requested",new{accepted=false,origin=commandOrigin});return;}
        try{var tag=((ComboBoxItem)CycleLimit.SelectedItem).Tag.ToString();chosenLimit=tag=="custom"?UiPreferences.ParseLimit(CustomLimit.Text):int.Parse(tag!);}
        catch(Exception ex){StateMessage.Visibility=Visibility.Visible;StateMessage.Text=ex.Message;return;}
        if(!preferences.skipReminder && !ConfirmPreparation())return;
        Prepared.IsChecked=true;preferences=preferences with{maxStarts=chosenLimit,unattended=Unattended.IsChecked==true};SavePreferences();
        Preview.Source=null;Preview.Visibility=Visibility.Collapsed;PreviewHint.Visibility=Visibility.Visible;FrameNote.Text="";if(service.Start(chosenLimit,preferences.unattended,commandOrigin))hasStarted=true;Refresh();
    }
    bool ConfirmPreparation()
    {
        var dialog=new Window{Owner=this,Title="开始前准备",Width=560,Height=510,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Background,Foreground=Foreground,FontSize=14};
        dialog.Resources.MergedDictionaries.Add(Resources);
        if(nativeUiTesting){dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=-12000;dialog.Top=-12000;dialog.ShowActivated=false;dialog.ShowInTaskbar=false;}
        var panel=new StackPanel{Margin=new Thickness(28)};dialog.Content=panel;
        panel.Children.Add(new TextBlock{Text="开始前准备",FontFamily=new FontFamily("SimSun"),FontSize=28,Margin=new Thickness(0,0,0,20)});
        var checks=new[]{"我已站在安全、地面平坦的位置，游戏窗口可见且未最小化","我已在江湖百态中打开艺人，保持艺人表演开始界面可见","续演时会切回游戏并移动鼠标；可随时按 "+preferences.stopHotkey+" 停止辅助"}.Select(t=>new CheckBox{Content=new TextBlock{Text=t,TextWrapping=TextWrapping.Wrap,MaxWidth=450},Margin=new Thickness(0,8,0,8)}).ToArray();
        foreach(var check in checks)panel.Children.Add(check);
        var remember=new CheckBox{Content="我已阅读，之后不再提示",Margin=new Thickness(0,18,0,18)};panel.Children.Add(remember);
        var mode=new CheckBox{Style=(Style)Resources["OptionSwitch"],Content="后续自动接管",IsChecked=Unattended.IsChecked,Margin=new Thickness(0,4,0,4)};panel.Children.Add(mode);
        var modeNote=new TextBlock{Text=mode.IsChecked==true?"续演时自动切回游戏并移动鼠标。":"表演结束后提醒，允许后才接管。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};panel.Children.Add(modeNote);
        mode.Checked+=(_,_)=>{Unattended.IsChecked=true;modeNote.Text="续演时自动切回游戏并移动鼠标。";};mode.Unchecked+=(_,_)=>{Unattended.IsChecked=false;modeNote.Text="表演结束后提醒，允许后才接管。";};
        var confirm=new Button{Content="确认并开始",IsEnabled=false,Padding=new Thickness(12)};panel.Children.Add(confirm);
        foreach(var check in checks){check.Checked+=(_,_)=>confirm.IsEnabled=checks.All(c=>c.IsChecked==true);check.Unchecked+=(_,_)=>confirm.IsEnabled=false;}
        confirm.Click+=(_,_)=>dialog.DialogResult=true;preparationDialog=dialog;
        bool accepted;try{accepted=dialog.ShowDialog()==true;}finally{preparationDialog=null;}
        if(accepted && remember.IsChecked==true){preferences=preferences with{skipReminder=true};if(!SavePreferences())preferences=preferences with{skipReminder=false};RemindAgain.IsChecked=!preferences.skipReminder;}
        return accepted;
    }
    bool SavePreferences(){try{preferences.Save(SettingsPath);service.History.Record("settings_changed",new{success=true});return true;}catch(Exception ex){service.History.Record("settings_changed",new{success=false});ShowUiNotice("设置未保存，下次仍使用原设置："+ex.Message);return false;}}
    void UnattendedChanged(object sender,RoutedEventArgs e){if(loadingUi||service==null)return;bool enabled=Unattended.IsChecked==true;service.SetUnattended(enabled);preferences=preferences with{unattended=enabled};SavePreferences();Refresh();}
    bool ApplyHotkeys(string start,string stop,out string? error)
    {
        var old=preferences;
        try{
            start=AssistantHotkey.Parse(start).Label;stop=AssistantHotkey.Parse(stop).Label;
            error=hotkeys?.Apply(start,stop)??(hotkeys==null?"快捷键尚未就绪":null);if(error!=null){service.History.Record("settings_changed",new{success=false});RefreshHotkeys();return false;}
            preferences=preferences with{startHotkey=start,stopHotkey=stop};
            if(!SavePreferences()){preferences=old;error=hotkeys!.Apply(old.startHotkey,old.stopHotkey)??"设置未保存，已恢复原快捷键";RefreshHotkeys();return false;}
            error=null;RefreshHotkeys();return true;
        }catch(ArgumentException ex){service.History.Record("settings_changed",new{success=false});error=ex.Message;return false;}
    }
    void HotkeysClicked(object sender,RoutedEventArgs e)=>ShowHotkeyRecorder();
    void ReminderChanged(object sender,RoutedEventArgs e){if(loadingUi)return;var old=preferences;preferences=preferences with{skipReminder=RemindAgain.IsChecked!=true};if(!SavePreferences()){preferences=old;loadingUi=true;RemindAgain.IsChecked=!old.skipReminder;loadingUi=false;}}
    void PlanChanged(object sender,SelectionChangedEventArgs e){if(CustomLimit==null)return;CustomLimit.Visibility=((CycleLimit.SelectedItem as ComboBoxItem)?.Tag.ToString()=="custom")?Visibility.Visible:Visibility.Collapsed;}
    void ApproveClicked(object sender,RoutedEventArgs e){service.ApproveTakeover();}
    void StopClicked(object sender,RoutedEventArgs e){CancelCurtain();service.Stop();Refresh();}
    void RecordsLayoutChanged(object sender,RoutedEventArgs e)
    {
        if(MainPanel==null || RecordsExpander==null || Preview==null)return;
        MainPanel.Margin=RecordsExpander.IsExpanded?new Thickness(0):new Thickness(0,30,0,30);
        Preview.Height=Math.Clamp(ActualHeight-618,32,128);
    }
    void LogsClicked(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(dataRoot){UseShellExecute=true});
    internal string? LastSupportPreview;
    void SupportClicked(object sender,RoutedEventArgs e)
    {
        try{
            string preview=SupportReport.Preview(dataRoot,service.Latest,service.History.Snapshot());LastSupportPreview=preview;
            var dialog=new Window{Owner=this,Title="预览问题报告",Width=680,Height=540,MinWidth=520,MinHeight=400,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground};dialog.Resources.MergedDictionaries.Add(Resources);
            if(nativeUiTesting){dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=-12000;dialog.Top=-12000;dialog.ShowActivated=false;dialog.ShowInTaskbar=false;}
            var panel=new DockPanel{Margin=new Thickness(20)};dialog.Content=panel;
            var note=new TextBlock{Text="报告自动整理助手操作、运行条件和故障上下文，补充说明选填。\n不包含截图、原始日志、账号和个人路径，不会自动上传。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(note,Dock.Top);panel.Children.Add(note);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
            var save=new Button{Content="保存诊断包"};var cancel=new Button{Content="取消"};buttons.Children.Add(save);buttons.Children.Add(cancel);
            var text=new TextBox{Text=preview,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(8)};panel.Children.Add(text);
            cancel.Click+=(_,_)=>dialog.Close();save.Click+=(_,_)=>{var picker=new Microsoft.Win32.SaveFileDialog{Filter="诊断包 (*.zip)|*.zip",FileName="JX3Artist-report-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",DefaultExt=".zip",AddExtension=true};if(picker.ShowDialog(dialog)!=true)return;try{SupportReport.Export(picker.FileName,preview);dialog.DialogResult=true;}catch(Exception ex){note.Text="未保存："+ex.Message+"。请选择一个新文件名。";}};
            dialog.ShowDialog();
        }catch(Exception ex){ShowUiNotice("问题报告未能生成："+ex.Message);}
    }
    void RefreshPlan(ViewState view)
    {
        Counts.Text=view.Starts==0?"助手自动开始：0 次":chosenLimit==0?$"{view.Starts} 次":$"{view.Starts} / {chosenLimit} 次";
        CountsCaption.Visibility=view.Starts==0?Visibility.Collapsed:Visibility.Visible;Counts.FontSize=view.Starts==0?14:28;Counts.FontFamily=new FontFamily(view.Starts==0?"Microsoft YaHei UI":"Georgia, SimSun");Counts.Foreground=(Brush)Resources[view.Starts==0?"Muted":"Ink"];
        PlanProgress.Visibility=view.Starts==0 || chosenLimit==0?Visibility.Collapsed:Visibility.Visible;
        PlanProgress.Maximum=Math.Max(1,chosenLimit);PlanProgress.Value=view.Starts;
        // Reconcile the rendered width even when the immutable view is unchanged
        // after hiding/restoring. This count bar has no theme animation clock.
        if(PlanProgress.Visibility==Visibility.Visible){PlanProgress.ApplyTemplate();if(PlanProgress.Template.FindName("PART_Track",PlanProgress) is FrameworkElement track && PlanProgress.Template.FindName("PART_Indicator",PlanProgress) is FrameworkElement fill){double width=track.ActualWidth*Math.Clamp((double)view.Starts/Math.Max(1,chosenLimit),0,1);if(fill.Width!=width)fill.Width=width;}}
    }
    void Refresh()
    {
        var view=service.Latest;bool running=service.IsRunning;
        RefreshExperience();
        FlushNotices();
        RecordingNote.Text=(demo?"离线预览，未记录运行诊断":recording?.Health.Message??"运行诊断记录未启动").Replace(" · ","，");
        if(service.History.SaveErrors>0)RecordingNote.Text+="；问题上下文记录曾失败，本次可导出已收集内容";
        RecordingNote.Foreground=(Brush)Resources["Muted"];
        RecordingNote.ToolTip="记录助手自身的内存、CPU等运行信息，用于排查问题；不表示画面识别结果。"+(recording?.Health.Error is string error?Environment.NewLine+error:"");
        double now=FrameTiming.NowMs();
        double? displayDeadline=view.DisplayDeadline??view.CountdownEnd;
        int seconds=displayDeadline.HasValue?CountdownTracker.DisplaySeconds(displayDeadline.Value,now):0;
        string remaining=view.Phase==Phase.Paused?"未确认（辅助已暂停，不会继续检查）":view.Phase is Phase.Stopped or Phase.Completed?"辅助已停止检查":view.CountdownEnd.HasValue?$"约 {seconds/60:00}:{seconds%60:00}（来源帧整秒读数估计）":view.CountdownStatus??"未确认";
        bool hasTime=view.Phase is not (Phase.Paused or Phase.Stopped or Phase.Completed) && view.CountdownEnd.HasValue;
        CountdownNote.Text=hasTime?$"{seconds/60:00}:{seconds%60:00}":StatusSummary.CountdownFallback(view,service.Mode.Enabled,(running||demo&&!IsVisible)&&!service.IsStopping&&!closeRequested&&uiError==null)??(!hasStarted && view.Phase==Phase.Stopped?"等待开始":view.Phase is Phase.Stopped or Phase.Completed?"已停止":view.Phase==Phase.Paused?"已暂停":"等待识别");
        CountdownNote.FontSize=hasTime?80:14;CountdownNote.FontFamily=new FontFamily(hasTime?"Georgia":"Microsoft YaHei UI");CountdownNote.Foreground=(Brush)Resources[hasTime?"AccentText":"Muted"];
        CountdownNote.ToolTip=remaining+(view.DisplayUncertaintyMs.HasValue?$"；秒切换边界估计误差 ±{view.DisplayUncertaintyMs.Value:0}ms":"");
        NextCheckNote.Text=StatusSummary.Format(view,now,DateTime.Now);
        NextCheckNote.Visibility=running?Visibility.Visible:Visibility.Collapsed;
        AutoTakeoverHint.Text=service.Mode.Enabled?"自动：续演时切回游戏并移动鼠标。":"手动：表演结束后提醒，允许后才接管。";
        bool needsApproval=running && view.AwaitingApproval && !service.Mode.Enabled;
        ApproveButton.Visibility=needsApproval?Visibility.Visible:Visibility.Collapsed;
        Unattended.IsEnabled=!closeRequested;
        StartButton.IsEnabled=!running && privileged && !closeRequested;StopButton.IsEnabled=running || view.Phase==Phase.Paused;CycleLimit.IsEnabled=!running;CustomLimit.IsEnabled=!running;
        RefreshPlan(view);
        AnimateDisplay($"{view.Phase}:{hasTime}");
        string? health=experience.SaveError;
        if(service.History.SaveErrors>0)health=(health==null?"":health+"；")+"问题上下文保存失败，请查看记录。";
        if(recording?.Health.Error is string resourceError)health=(health==null?"":health+"；")+"运行诊断保存异常，请查看记录。";
        HealthNotice.Text=health??"";HealthNotice.Visibility=health==null?Visibility.Collapsed:Visibility.Visible;
        if(!ReferenceEquals(rendered,view))
        {
            rendered=view;StateTitle.Text=view.Phase switch{Phase.Watching=>"确认画面",Phase.ConfirmingEntry=>"确认入口",Phase.WaitingVisibility=>"等待界面",Phase.WaitingIdle=>"等待接管",Phase.Starting=>"接管操作中",Phase.Performing=>"表演观察中",Phase.Paused=>"已暂停",Phase.Completed=>"已完成",_=>"已停止"};
            StateBadge.Visibility=Visibility.Collapsed;StateMessage.Visibility=view.Phase==Phase.Paused?Visibility.Visible:Visibility.Collapsed;
            StateMessage.Text=view.Message.Length>220?view.Message[..220]+"…":view.Message;
            if(view.Phase==Phase.Paused)StateMessage.Text=ProblemCatalog.PauseMessage(StateMessage.Text,hotkeys?.Registered(AssistantHotkeys.StartId)==true?preferences.startHotkey:null);
            DiagnosticNote.Text=view.Message+(view.Detail==null?"":Environment.NewLine+view.Detail);
            StateBadge.Background=Brushes.Transparent;
            if(view.Preview!=null)
            {try{using var stream=new FileStream(view.Preview,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=750;image.StreamSource=stream;image.EndInit();image.Freeze();Preview.Source=image;Preview.Visibility=Visibility.Visible;PreviewHint.Visibility=Visibility.Collapsed;FrameNote.Text=$"最近画面 {view.FrameTime:HH:mm:ss}";}catch{}}
        }
        if(closeRequested && (!running || (!service.ButtonPending && DateTime.UtcNow-closeAt>TimeSpan.FromSeconds(2)))){allowClose=true;Close();}
        if(uiError!=null){StateBadge.Visibility=Visibility.Visible;StateMessage.Visibility=Visibility.Visible;StateTitle.Text="界面异常已停止";StateMessage.Text=uiError+"；请重新打开软件";StartButton.IsEnabled=false;}
    }
    void OnClosing(object? sender,CancelEventArgs e)
    {
        if(!closeRequested){
            CancelCurtain();closeRequested=true;closeAt=DateTime.UtcNow;service.Notices.Close();notice?.Dispose();
            if(!demo)try{File.AppendAllText(Path.Combine(dataRoot,"notification-events.jsonl"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,Kind="closing",cleanupConfirmed=notice?.CleanupSucceeded??true})+Environment.NewLine);}catch{}
        }
        if(allowClose || !service.IsRunning)return;e.Cancel=true;service.Stop("window_close");StateMessage.Text="正在停止后续操作并释放资源…";
    }
    internal void StopForError(string message){uiError=message;service.History.Record("problem_detected",new{state="Paused"},"JX3-UNKNOWN-001");service.Stop("ui_error");Refresh();}
    internal void PreviewSkin(string name){ChangeSkin(name,save:false);}
    internal void BeginDemo(){preferences=preferences with{skipReminder=true};Prepared.IsChecked=true;StartClicked(this,new RoutedEventArgs());}
    internal void SaveUi(string path)
    {
        UpdateLayout();var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(this);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=new FileStream(path,FileMode.CreateNew);encoder.Save(output);
    }
    internal void SaveOffscreen(string path,ViewState state,int width=960,int height=700)
    {
        if(!demo || IsVisible)throw new InvalidOperationException("offscreen rendering requires hidden demo");
        service.Publish(state);Refresh();var root=(FrameworkElement)Content;
        if(width<900 || height<650)throw new ArgumentOutOfRangeException("offscreen size below supported minimum");
        root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        // Check rendered geometry, not just XAML structure: these must remain
        // reachable even when the settings panel needs to scroll.
        foreach(FrameworkElement element in new FrameworkElement[]{StartButton,StopButton,SettingsMenuButton})
        {
            var bounds=element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            if(bounds.Width<=0 || bounds.Height<=0 || bounds.Left<0 || bounds.Top<0 || bounds.Right>root.RenderSize.Width+1 || bounds.Bottom>root.RenderSize.Height+1)
                throw new InvalidOperationException("critical UI control outside rendered area: "+element.Name);
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        var backdrop=new DrawingVisual();using(var drawing=backdrop.RenderOpen())drawing.DrawRectangle(Background,null,new Rect(0,0,width,height));
        bitmap.Render(backdrop);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
