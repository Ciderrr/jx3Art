using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Int1;
namespace JX3Artist;

public partial class MainWindow
{
    internal bool nativeUiTesting;
    internal int CheckNativeUi(string output)
    {
        if(!demo)throw new InvalidOperationException("Only demo may run UI checks");
        nativeUiTesting=true;var results=new List<object>();int failed=0;
        void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
        void Test(string name,Action action){try{action();results.Add(new{name,passed=true});}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.ToString()});}}
        void PumpUntil(Func<bool> done,int timeout=3000)
        {
            var frame=new DispatcherFrame();var started=DateTime.UtcNow;var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            t.Tick+=(_,_)=>{if(done() || (DateTime.UtcNow-started).TotalMilliseconds>timeout){t.Stop();frame.Continue=false;}};t.Start();Dispatcher.PushFrame(frame);Assert(done(),"UI wait timed out");
        }
        void Click(Button button){Assert(button.IsEnabled,"button disabled");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        void Prepare(Action<Window> action)
        {
            Exception? driverError=null;
            var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            t.Tick+=(_,_)=>{if(preparationDialog==null)return;t.Stop();try{action(preparationDialog);}catch(Exception ex){driverError=ex;preparationDialog!.DialogResult=false;}};
            t.Start();try{Click(StartButton);}finally{t.Stop();}
            if(driverError!=null)throw new Exception("Modal driver check failed",driverError);
        }
        Test("read-only native geometry agrees with own WPF physical origin",()=>{
            var own=new System.Windows.Interop.WindowInteropHelper(this).Handle;var snapshot=WindowGeometryReader.Read(own);Assert(snapshot.Physical && snapshot.Dpi>0,"own physical snapshot missing");
            var origin=PointToScreen(new Point(0,0));Assert(Math.Abs(origin.X-snapshot.Client.X)<=1 && Math.Abs(origin.Y-snapshot.Client.Y)<=1,"WPF/client physical origins differ");
            var m=new WindowFrameMap(snapshot,WindowGeometryReader.Read(own),snapshot.Visible.Width,snapshot.Visible.Height,snapshot.Visible.Width,snapshot.Visible.Height,(long)snapshot.Visible.Width*snapshot.Visible.Height*4);
            var b=new Jx3.OneClickTest.Box(m.ClientInFrame.X+5,m.ClientInFrame.Y+5,m.ClientInFrame.X+15,m.ClientInFrame.Y+15);var point=m.ScreenCenter(b,snapshot);Assert(point.X==snapshot.Client.X+10 && point.Y==snapshot.Client.Y+10,"own physical mapping differs");
        });
        Test("default Intermission and honest idle fields",()=>{Refresh();Assert(preferences.skin=="幕间"&&FindName("SkinChoice")==null,"default skin");Assert(Counts.Text=="助手自动开始：0 次"&&PlanProgress.Visibility==Visibility.Collapsed,"idle count claims a started plan");Assert(CountdownNote.Text=="等待开始"&&CountdownNote.FontSize==14,"idle uses giant placeholder");Assert(StateBadge.Visibility==Visibility.Collapsed&&StateMessage.Visibility==Visibility.Collapsed&&CountsCaption.Visibility==Visibility.Collapsed,"duplicate idle labels");UpdateLayout();Assert(Math.Abs(Counts.TranslatePoint(new Point(),this).Y-CountdownNote.TranslatePoint(new Point(),this).Y)<1,$"idle value baselines not aligned: count={Counts.TranslatePoint(new Point(),this).Y}, timer={CountdownNote.TranslatePoint(new Point(),this).Y}, countH={Counts.ActualHeight}, timerH={CountdownNote.ActualHeight}");});
        Test("active action contrasts with disabled action",()=>{UpdateLayout();Assert(StartButton.IsEnabled&&!StopButton.IsEnabled,"idle actions");Assert(StartButton.Background.ToString()!=StopButton.Background.ToString(),"buttons indistinguishable");var border=(Border)StopButton.Template.FindName("Surface",StopButton);Assert(border.Opacity<=0.4,"disabled action not muted");});
        Test("supported theme reload preserves other preferences",()=>{var old=preferences with{skin="夜演"};old.Save(SettingsPath);var clone=new MainWindow(dataRoot,"not-used",true);try{Assert(clone.preferences==old,"retired theme lost settings");Assert(clone.FindName("SkinChoice")==null&&((System.Windows.Media.SolidColorBrush)clone.Resources["Paper"]).Color==(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(UiTheme.Palettes["夜演"][0]),"retired palette still exposed");}finally{clone.Close();preferences.Save(SettingsPath);}});
        Test("invalid custom count blocks startup",()=>{CycleLimit.SelectedIndex=3;CustomLimit.Text="0";Click(StartButton);Assert(!service.IsRunning && StateMessage.Text.Contains("整数"),"invalid value accepted");});
        Test("cancel preparation does not start or suppress reminder",()=>{CustomLimit.Text="37";Prepare(d=>d.DialogResult=false);Assert(!service.IsRunning&&!UiPreferences.Load(SettingsPath).skipReminder,"cancel mutated consent");});
        Test("confirmation is disabled before all checks",()=>{
            Prepare(d=>{var panel=(StackPanel)d.Content;var checks=panel.Children.OfType<CheckBox>().ToArray();var button=panel.Children.OfType<Button>().Single();Assert(!button.IsEnabled,"confirmation initially enabled");checks[0].IsChecked=true;checks[1].IsChecked=true;Assert(!button.IsEnabled,"two checks enough");checks[2].IsChecked=true;Assert(button.IsEnabled,"three checks not enough");checks[3].IsChecked=true;Click(button);});
            Assert(service.IsRunning,"demo not started");Refresh();Assert(StopButton.IsEnabled&&!StartButton.IsEnabled&&StopButton.Background.ToString()!=StartButton.Background.ToString(),"running actions not distinct");SaveUi(Path.Combine(output,"running-actions.png"));var saved=UiPreferences.Load(SettingsPath);Assert(saved.maxStarts==37 && saved.skipReminder,"preparation settings not saved");
            Click(StopButton);PumpUntil(()=>!service.IsRunning);Refresh();
        });
        Test("saved suppression starts without modal and stop cancels worker",()=>{Click(StartButton);Assert(service.IsRunning&&preparationDialog==null,"suppression not effective");Click(StopButton);PumpUntil(()=>!service.IsRunning);Refresh();});
        Test("restore reminder and hotkey handler cancels modal",()=>{RemindAgain.IsChecked=true;Assert(!UiPreferences.Load(SettingsPath).skipReminder,"restore failed");bool handled=false;Prepare(d=>Hook(0,0x312,(nint)Desktop.HotkeyId,0,ref handled));Assert(handled&&!service.IsRunning,"hotkey cancel failed");});
        Test("default preparation inherits palette and confirmation fits",()=>{Prepare(d=>{d.UpdateLayout();var panel=(StackPanel)d.Content;var confirm=panel.Children.OfType<Button>().Single();var check=panel.Children.OfType<CheckBox>().First();Assert(check.Foreground.ToString()==Resources["Ink"].ToString(),"default preparation foreground mismatch");var bounds=confirm.TransformToAncestor(d).TransformBounds(new Rect(confirm.RenderSize));Assert(bounds.Bottom<d.ActualHeight&&bounds.Top>=0,"confirmation outside modal");var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)d.ActualWidth,(int)d.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(d);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,"default-preparation.png")))encoder.Save(file);d.DialogResult=false;});Assert(!service.IsRunning,"cancel started worker");});
        Test("native countdown uses learned display phase instead of schedule upper edge",()=>{
            double now=FrameTiming.NowMs();service.Publish(new(Phase.Performing,"计时显示检查",1,CountdownEnd:now+59000,DisplayDeadline:now+58000,DisplayUncertaintyMs:100));Refresh();
            Assert(CountdownNote.Text=="00:58","UI used late schedule endpoint");Assert(CountdownNote.ToolTip.ToString()!.Contains("100ms"),"phase uncertainty missing");
        });
        Test("returned view immediately displays countdown adopted from actual startup controller path",()=>{
            double now=FrameTiming.NowMs();var core=new AutomationController(3,now-20000);
            core.Observe(new(now-10000,now-9900,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE"));core.Observe(new(now-9000,now-8900,true,true,"UNIQUE_CANDIDATE",1,"NOT_VISIBLE"));core.BeginAction();core.ActionFinished(true,true,now,now-2000,null);
            var samples=new[]{new Observation(now-6000,now-4000,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownSeconds:1799,CountdownQuality:"DUAL_PASS_AGREEMENT"),new Observation(now-2000,now-100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",CountdownSeconds:1795,CountdownQuality:"DUAL_PASS_AGREEMENT")};
            Assert(core.AdoptStartCountdown(samples,now-6500,now),"feedback countdown not adopted");
            service.Publish(new(core.State,core.Message,core.Starts,CountdownEnd:core.Countdown.EndSourceMs,DisplayDeadline:core.Countdown.DisplayDeadline));Refresh();Assert(CountdownNote.Text.StartsWith("29:")&&CountdownNote.FontSize==80,"first returned view waits despite confirmed feedback time");
        });
        Test("count text and actual progress width recover together on unchanged snapshot",()=>{
            int oldLimit=chosenLimit;chosenLimit=4;
            try{
                for(int count=1;count<=4;count++){
                    service.Publish(new(Phase.Performing,"轮次同步检查",count));Refresh();UpdateLayout();PlanProgress.ApplyTemplate();
                    var track=(FrameworkElement)PlanProgress.Template.FindName("PART_Track",PlanProgress);var fill=(FrameworkElement)PlanProgress.Template.FindName("PART_Indicator",PlanProgress);
                    Assert(track.ActualWidth>0,"track not laid out");Assert(Counts.Text==$"{count} / 4 次"&&Math.Abs(fill.Width-track.ActualWidth*count/4)<.5,"count and fill differ");
                    if(count==1)SaveUi(Path.Combine(output,"plan-one-of-four.png"));
                }
                var current=(FrameworkElement)PlanProgress.Template.FindName("PART_Indicator",PlanProgress);var currentTrack=(FrameworkElement)PlanProgress.Template.FindName("PART_Track",PlanProgress);
                current.Width=currentTrack.ActualWidth/4;Refresh();UpdateLayout();
                Assert(Counts.Text=="4 / 4 次"&&Math.Abs(current.Width-currentTrack.ActualWidth)<.5,"unchanged snapshot leaves stale quarter-width fill");
                Visibility=Visibility.Hidden;service.Publish(new(Phase.Performing,"隐藏期间更新",2));Refresh();Visibility=Visibility.Visible;Refresh();UpdateLayout();
                Assert(Counts.Text=="2 / 4 次"&&Math.Abs(current.Width-currentTrack.ActualWidth/2)<.5,"restore leaves stale fill");
                service.Publish(new(Phase.Performing,"轮次同步检查",4));Refresh();UpdateLayout();SaveUi(Path.Combine(output,"plan-four-of-four.png"));
            }finally{chosenLimit=oldLimit;}
        });
        Test("missing countdown explains fallback switches live and clears for known time or pause",()=>{
            preferences=preferences with{skipReminder=true};RemindAgain.IsChecked=false;Unattended.IsChecked=true;Click(StartButton);Assert(service.IsRunning,"demo not started");
            service.Publish(new(Phase.Performing,"正在观察"));Refresh();
            Assert(CountdownNote.Text.Contains("不影响挂机流程")&&CountdownNote.Text.Contains("自动续演")&&CountdownNote.FontSize==14,"automatic fallback missing");
            UpdateLayout();SaveUi(Path.Combine(output,"countdown-unknown-auto.png"));
            Unattended.IsChecked=false;Refresh();Assert(CountdownNote.Text.Contains("提醒您继续")&&!CountdownNote.Text.Contains("自动续演"),"manual mode promises automatic action");
            service.Publish(new(Phase.Performing,"正在观察",CountdownEnd:FrameTiming.NowMs()+59000));Refresh();Assert(!CountdownNote.Text.Contains("暂未识别")&&CountdownNote.FontSize==80,"stale fallback after recovery");
            service.Publish(new(Phase.Paused,"测试暂停"));Refresh();Assert(CountdownNote.Text=="已暂停"&&!CountdownNote.ToolTip.ToString()!.Contains("不影响"),"paused state promises continued checking");
            service.Publish(new(Phase.WaitingVisibility,"等待新画面"));Refresh();Assert(!CountdownNote.Text.Contains("不影响"),"unconfirmed panel promises fallback");
            service.Publish(new(Phase.Performing,"正在观察"));Click(StopButton);Refresh();Assert(!CountdownNote.Text.Contains("不影响"),"stopping still promises continuation");PumpUntil(()=>!service.IsRunning);
            service.Publish(new(Phase.Performing,"陈旧状态"));Refresh();Assert(!CountdownNote.Text.Contains("不影响"),"dead worker still promises continuation");
        });
        Test("real timer tick does not change count progress",()=>{var view=new ViewState(Phase.Performing,"计时检查",2,CountdownEnd:FrameTiming.NowMs()+9000);service.Publish(view);Refresh();var before=CountdownNote.Text;int changes=0;RoutedPropertyChangedEventHandler<double> handler=(_,_)=>changes++;PlanProgress.ValueChanged+=handler;try{PumpUntil(()=>CountdownNote.Text!=before);Assert(changes==0 && PlanProgress.Value==2,"timer changed count progress");}finally{PlanProgress.ValueChanged-=handler;}});
        Test("continuous mode persists as zero without fake progress",()=>{preferences=preferences with{skipReminder=true};RemindAgain.IsChecked=false;CycleLimit.SelectedIndex=4;Click(StartButton);Assert(UiPreferences.Load(SettingsPath).maxStarts==0,"continuous not saved");Assert(PlanProgress.Visibility!=Visibility.Visible,"continuous has denominator progress");Click(StopButton);PumpUntil(()=>!service.IsRunning);Refresh();});
        Test("next check countdown visible and stops with assistance",()=>{
            Click(StartButton);PumpUntil(()=>service.Latest.Phase==Phase.Performing);service.Publish(new(Phase.WaitingIdle,"需要续演，请点击允许本次接管",NextCheck:FrameTiming.NowMs()+9000,AwaitingApproval:true));Refresh();
            Assert(NextCheckNote.Visibility==Visibility.Visible && NextCheckNote.Text.Contains("秒后"),"next monitoring time missing");Assert(ApproveButton.Visibility==Visibility.Visible,"approval action missing");UpdateLayout();SaveUi(Path.Combine(output,"approval.png"));
            Unattended.IsChecked=true;Refresh();Assert(ApproveButton.Visibility==Visibility.Collapsed,"live automatic mode kept stale approval prompt");Unattended.IsChecked=false;Refresh();Assert(ApproveButton.Visibility==Visibility.Visible,"live manual mode lost pending approval prompt");
            service.Publish(new(Phase.Performing,"正在观察",1,CountdownEnd:FrameTiming.NowMs()+50000,NextCheck:FrameTiming.NowMs()+3000));Refresh();Assert(NextCheckNote.Text.Contains("状态：表演中") && NextCheckNote.Text.Contains("下次画面检查于"),"combined state and schedule missing");
            service.Publish(new(Phase.Performing,"正在观察",1,CountdownEnd:FrameTiming.NowMs()+9000,NextCheck:FrameTiming.NowMs()+1000));Refresh();Assert(NextCheckNote.Text.Contains("状态：表演中")&&!NextCheckNote.Text.Contains("最后10秒"),"near end duplicates status text");UpdateLayout();SaveUi(Path.Combine(output,"near-end.png"));
            service.Publish(new(Phase.Performing,"正在观察",1,CountdownEnd:FrameTiming.NowMs()-1000));Refresh();Assert(NextCheckNote.Text.Contains("状态：表演中") && NextCheckNote.Text.Contains("正在检查画面"),"predicted zero falsely claims game ended");
            service.Publish(new(Phase.WaitingVisibility,"等待新画面",NextCheck:FrameTiming.NowMs()+5000));Refresh();Assert(!NextCheckNote.Text.Contains("加密检查"),"unknown countdown retains near-end claim");
            Click(StopButton);PumpUntil(()=>!service.IsRunning);Refresh();Assert(NextCheckNote.Visibility==Visibility.Collapsed && ApproveButton.Visibility==Visibility.Collapsed,"stale countdown/approval after stop");
        });
        Test("unattended setting roundtrip through native Start",()=>{
            Unattended.IsChecked=true;Click(StartButton);Assert(UiPreferences.Load(SettingsPath).unattended && Unattended.IsEnabled,"mode not saved or locked during run");Unattended.IsChecked=false;Assert(service.IsRunning&&!service.Mode.Enabled&&!UiPreferences.Load(SettingsPath).unattended,"live disable did not reach service and disk");Unattended.IsChecked=true;Assert(service.IsRunning&&service.Mode.Enabled&&UiPreferences.Load(SettingsPath).unattended,"live enable restarted or ignored");Click(StopButton);PumpUntil(()=>!service.IsRunning);Refresh();Assert(Unattended.IsEnabled,"mode still locked after stop");
        });
        Test("custom global hotkey settings save reload and route Start Stop",()=>{
            var driver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};Exception? driverError=null;
            driver.Tick+=(_,_)=>{if(hotkeyDialog==null)return;driver.Stop();try{var panel=(StackPanel)hotkeyDialog.Content;Assert(!panel.Children.OfType<TextBox>().Any(),"shortcut still uses typing fields");Click(panel.Children.OfType<Button>().Single(b=>b.Name=="RecordStart"));keyCapture!(System.Windows.Input.Key.S,System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Alt);Click(panel.Children.OfType<Button>().Single(b=>b.Name=="RecordStop"));keyCapture!(System.Windows.Input.Key.Q,System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Alt);Click(panel.Children.OfType<Button>().Single(b=>b.Name=="RecordSave"));}catch(Exception ex){driverError=ex;hotkeyDialog?.Close();}};
            driver.Start();try{Click(HotkeySettingsButton);}finally{driver.Stop();}if(driverError!=null)throw driverError;
            var saved=UiPreferences.Load(SettingsPath);Assert(saved.startHotkey=="Ctrl+Alt+S"&&saved.stopHotkey=="Ctrl+Alt+Q","custom shortcuts not persisted");Assert(StartButton.Content.ToString()!.Contains("Ctrl+Alt+S")&&StopButton.Content.ToString()!.Contains("Ctrl+Alt+Q"),"buttons missing bindings");
            Assert(!ApplyHotkeys("F9","F9",out _)&&preferences==saved,"duplicate shortcut changed bindings");
            bool handled=false;Hook(handle,0x312,AssistantHotkeys.StartId,0,ref handled);Assert(handled&&service.IsRunning,"start WM_HOTKEY not routed");handled=false;Hook(handle,0x312,AssistantHotkeys.StopId,0,ref handled);Assert(handled,"stop WM_HOTKEY not routed");PumpUntil(()=>!service.IsRunning);Refresh();
        });
        Test("state summary distinguishes stale last update and awaiting approval",()=>{
            var wall=DateTime.Today.AddHours(12);double now=10000;var view=new ViewState(Phase.WaitingIdle,"internal diagnostic",FrameTime:wall.AddSeconds(-23),NextCheck:now+8000,AwaitingApproval:true);
            string summary=StatusSummary.Format(view,now,wall);Assert(summary.Contains("23秒前")&&summary.Contains("状态：待续演，等待确认")&&summary.Contains("8秒后"),"state summary time or approval mismatch");
            service.Publish(view);Refresh();Assert(StateBadge.Visibility==Visibility.Collapsed&&StateMessage.Visibility==Visibility.Collapsed&&DiagnosticNote.Text=="internal diagnostic","technical status still duplicated");
        });
        Test("native system registers and releases an unused diagnostic shortcut without input",()=>{
            bool registered=false;const int ownDiagnosticId=0x4A60;
            for(uint key=0x7C;key<=0x87&&!registered;key++)registered=Desktop.RegisterHotKey(handle,ownDiagnosticId,0x4007,key);
            Assert(registered,"no diagnostic shortcut available");Assert(Desktop.UnregisterHotKey(handle,ownDiagnosticId),"owned shortcut release failed");
        });
        Test("lifecycle notices use real UI submission route once and expose delivery failures",()=>{
            while(service.Notices.TryTake(out _)){}var sent=new List<AssistanceNotice>();NotificationSink=item=>{sent.Add(item);return true;};
            try{service.Notices.Begin();service.Publish(new(Phase.Paused,"点击后未确认进入表演"));Refresh();Refresh();Assert(sent.Count==2&&sent[0].Kind=="started"&&sent[1].Kind=="paused"&&sent[1].Text.Contains("暂未确认"),"fast pause lost or duplicate notification");
                service.Publish(new(Phase.Completed,"完成"));Refresh();Assert(sent.Last().Kind=="completed","completion notification missing");
                service.SetUnattended(false);service.Publish(new(Phase.WaitingIdle,"等待",AwaitingApproval:true));Refresh();Assert(sent.Last().Kind=="approval","manual approval notification missing");
                service.SetUnattended(true);service.Publish(new(Phase.WaitingIdle,"自动",AwaitingApproval:true));int count=sent.Count;Refresh();Assert(sent.Count==count,"automatic mode sent approval notice");
                NotificationSink=_=>false;service.Publish(new(Phase.Stopped,"停止"));Refresh();Assert(PermissionNote.Visibility==Visibility.Visible&&PermissionNote.Text.Contains("通知未能发送"),"silent notification delivery failure");
            }finally{NotificationSink=null;PermissionNote.Visibility=Visibility.Collapsed;}
        });
        Test("closing a running demo suppresses queued and late notifications and withdraws its icon",()=>{
            var clone=new MainWindow(Path.Combine(output,"closing-data"),"not-used",true){WindowStartupLocation=WindowStartupLocation.Manual,Left=-13000,Top=-13000,ShowActivated=false,ShowInTaskbar=false};
            var calls=new List<(uint op,WindowsNotice.Data data)>();int sent=0;bool closed=false;
            try{
                clone.Show();clone.NotificationSink=_=>{sent++;return true;};clone.notice=new WindowsNotice(123,(op,d)=>{calls.Add((op,d));return true;});clone.notice.Show("此前","此前");
                clone.service.Start(1,false);clone.service.Publish(new(Phase.Paused,"目标不是游戏客户端"));clone.Closed+=(_,_)=>closed=true;clone.Close();
                clone.service.Notices.Begin();clone.service.Publish(new(Phase.Paused,"迟到的暂停"));clone.Refresh();
                Assert(sent==0&&!clone.service.Notices.TryTake(out _),"closing submitted pending or late notices");
                Assert(calls.Count==4&&calls[2].op==1&&calls[2].data.Info==""&&calls[3].op==2,"close did not withdraw and remove icon");
                PumpUntil(()=>closed&&!clone.service.IsRunning);Assert(sent==0,"late notification after close");
            }finally{clone.allowClose=true;clone.service.Stop("window_close");clone.Close();}
        });
        Test("new native window restores theme plan and reminder",()=>{var clone=new MainWindow(dataRoot,"not-used",true);try{Assert(clone.preferences.skin=="幕间" && clone.CycleLimit.SelectedIndex==4 && clone.RemindAgain.IsChecked==false && clone.Unattended.IsChecked==true,"reload lost preferences");clone.SaveOffscreen(Path.Combine(output,"restored.png"),new(Phase.Stopped,"等待开始"),900,650);}finally{clone.Close();}});
        Test("expanded records aligns preview metadata and compact action",()=>{
            var visual=new System.Windows.Media.DrawingVisual();using(var draw=visual.RenderOpen()){draw.DrawRectangle(System.Windows.Media.Brushes.LightGray,null,new Rect(0,0,480,261));draw.DrawRectangle(System.Windows.Media.Brushes.SlateGray,null,new Rect(20,20,440,221));}
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(480,261,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));var path=Path.Combine(output,"synthetic-preview.png");using(var file=File.Create(path))encoder.Save(file);
            service.Publish(new(Phase.Stopped,"已停止",Preview:path,FrameTime:DateTime.Today.AddHours(12),FrameAgeMs:1446));Refresh();RecordsExpander.IsExpanded=true;UpdateLayout();
            Assert(!FrameNote.Text.Contains("帧龄") && !FrameNote.Text.Contains("1446"),"technical age exposed");
            var image=Preview.TranslatePoint(new Point(),RecordsContent);var text=FrameNote.TranslatePoint(new Point(),RecordsContent);var note=RecordingNote.TranslatePoint(new Point(),RecordsContent);var action=OpenRecordsButton.TranslatePoint(new Point(),RecordsContent);
            Assert(Math.Abs(image.X-text.X)<1 && Math.Abs(text.X-note.X)<1,"preview and labels not aligned");Assert(Math.Abs(image.Y-action.Y)<1,"action and preview tops differ");Assert(OpenRecordsButton.ActualHeight<=40 && OpenRecordsButton.ActualWidth<180,"records button stretches");
            SaveUi(Path.Combine(output,"expanded-records.png"));
            var title=CountdownCaption.TransformToAncestor(MainPanel).TransformBounds(new Rect(CountdownCaption.RenderSize));Assert(title.Top>=0,"expanded records clips main heading");
            var clone=new MainWindow(Path.Combine(output,"expanded-data"),"not-used",true);try{clone.RecordsExpander.IsExpanded=true;clone.SaveOffscreen(Path.Combine(output,"expanded-minimum.png"),new(Phase.Performing,"正在观察表演",1,CountdownEnd:FrameTiming.NowMs()+9000),900,650);}finally{clone.Close();}
            RecordsExpander.IsExpanded=false;Refresh();
        });
        service.Stop();PumpUntil(()=>!service.IsRunning);Refresh();SaveUi(Path.Combine(output,"native-window.png"));
        Test("support report preview shows stable problem code and cancellation writes no archive",()=>{
            service.Publish(new(Phase.Paused,"点击后未确认进入表演"));Refresh();Assert(StateMessage.Text.Contains("JX3-START-001"),"problem code missing from pause");
            Exception? driverError=null;var driver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            driver.Tick+=(_,_)=>{var dialog=OwnedWindows.Cast<Window>().FirstOrDefault(w=>w.Title=="预览问题报告");if(dialog==null)return;driver.Stop();try{var panel=(DockPanel)dialog.Content;var preview=panel.Children.OfType<TextBox>().Single();Assert(preview.IsReadOnly&&preview.Text==LastSupportPreview&&preview.Text.Contains("JX3-START-001")&&!preview.Text.Contains(dataRoot),"preview mismatch or path leak");var actions=panel.Children.OfType<StackPanel>().Single();Click(actions.Children.OfType<Button>().Single(b=>b.Content.ToString()=="取消"));}catch(Exception ex){driverError=ex;dialog.Close();}};
            driver.Start();try{Click(SupportButton);}finally{driver.Stop();}if(driverError!=null)throw driverError;Assert(!Directory.GetFiles(dataRoot,"*.zip",SearchOption.AllDirectories).Any(),"cancel created archive");
        });
        Test("own assistant commands are automatically correlated without user narration",()=>{
            service.ApproveTakeover();service.SetUnattended(false);service.Stop("shortcut");Refresh();
            var context=service.History.Snapshot();Assert(context.Recent.Any(x=>x.Kind=="start_requested"&&x.Data.TryGetProperty("origin",out var origin)&&origin.GetString()=="shortcut"),"start shortcut not recorded");
            Assert(context.Recent.Any(x=>x.Kind=="approval_requested")&&context.Recent.Any(x=>x.Kind=="mode_changed")&&context.Recent.Any(x=>x.Kind=="stop_requested"&&x.Data.GetProperty("origin").GetString()=="shortcut"),"own command timeline incomplete");
            Assert(context.Incidents.Length>0&&SupportReport.Preview(dataRoot,service.Latest,context).Contains("automaticContext"),"report missing retained incident");
        });
        AddNativeShellTests(Test,output);
        AddExperienceUiTests(Test,output);
        File.WriteAllText(Path.Combine(output,"native-ui-results.json"),JsonSerializer.Serialize(new{scope="WPF routed events and modal Dispatcher, isolated demo; zero game access; WM_HOTKEY dispatch and own registration, no physical key input",passed=results.Count-failed,total=results.Count,results},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:9;
    }
}
