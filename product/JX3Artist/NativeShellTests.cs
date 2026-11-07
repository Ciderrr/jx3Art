using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Int1;

namespace JX3Artist;
public partial class MainWindow
{
    void AddNativeShellTests(Action<string,Action> test,string output)
    {
        void Check(bool condition,string text){if(!condition)throw new Exception(text);}
        void Click(Button value)=>value.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        test("native header menus share surface and client groups have real separation",()=>{
            ClientMenuClicked(this,new RoutedEventArgs());var client=ClientMenuButton.ContextMenu!;
            Check(client.Items.OfType<MenuItem>().Count(x=>!x.IsEnabled)==5,"future clients became enabled");
            Check(((MenuItem)client.Items[0]).Margin.Bottom>=8,"client heading cramped");client.IsOpen=false;
            SkinMenuClicked(this,new RoutedEventArgs());var skin=SkinMenuButton.ContextMenu!;Check(skin.Items.Count==12&&skin.Background.ToString()==client.Background.ToString(),"skin menu differs from common menu");skin.IsOpen=false;
            ApplicationMenuClicked(this,new RoutedEventArgs());Check(SettingsMenuButton.ContextMenu!.Background.ToString()==client.Background.ToString(),"settings menu surface differs");SettingsMenuButton.ContextMenu.IsOpen=false;
        });
        test("native theme switching keeps real view counts estimate and input mode",()=>{
            var state=new ViewState(Phase.Performing,"native theme check",2,CountdownEnd:FrameTiming.NowMs()+60000);service.Publish(state);Refresh();var estimate=experience.Read();bool mode=service.Mode.Enabled;
            foreach(string skin in UiTheme.Palettes.Keys){ChangeSkin(skin);Check(ReferenceEquals(service.Latest,state)&&experience.Read()==estimate&&service.Mode.Enabled==mode,"theme changed business state");Check(CountdownNote.Foreground.ToString()==Resources["AccentText"].ToString(),"timer kept old theme color");}
            ChangeSkin("幕间");
        });
        test("native recorder Escape cancels without changing saved keys",()=>{
            var old=preferences;Exception? error=null;var driver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            driver.Tick+=(_,_)=>{if(hotkeyDialog==null)return;driver.Stop();try{var panel=(StackPanel)hotkeyDialog.Content;var button=panel.Children.OfType<Button>().First(b=>b.Name=="RecordStart");Click(button);keyCapture!(Key.Escape,ModifierKeys.None);Check(keyCapture==null&&button.Content.ToString()==old.startHotkey,"escape changed value");hotkeyDialog.DialogResult=false;}catch(Exception ex){error=ex;hotkeyDialog!.Close();}};
            driver.Start();try{ShowHotkeyRecorder();}finally{driver.Stop();}if(error!=null)throw error;Check(preferences==old,"cancel persisted keys");
        });
        test("native settings has supported keys mail copy and deliberately unconfigured Issues",()=>{
            Exception? error=null;var links=new List<string>();var copied=new List<string>();ShellOpenForTest=url=>{links.Add(url);return true;};ClipboardForTest=text=>copied.Add(text);
            var driver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            driver.Tick+=(_,_)=>{if(settingsDialog==null)return;driver.Stop();try{
                var tabs=(TabControl)settingsDialog.Content;var first=(StackPanel)((ScrollViewer)((TabItem)tabs.Items[0]).Content).Content;
                var keys=(StackPanel)first.Children[1];Check(keys.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("F12除外")),"supported keys missing");
                tabs.SelectedIndex=1;settingsDialog.UpdateLayout();var about=(StackPanel)((ScrollViewer)((TabItem)tabs.Items[1]).Content).Content;
                var row=about.Children.OfType<StackPanel>().Single();Click(row.Children.OfType<Button>().First());Click(row.Children.OfType<Button>().Last());
                Check(links.Single().StartsWith("mailto:"+FeedbackEmail+"?",StringComparison.Ordinal)&&copied.Single()==FeedbackEmail,"wrong feedback destination");
                Check(about.Children.OfType<Button>().Single(b=>b.Content.ToString()!.StartsWith("GitHub Issues")).IsEnabled==false,"unconfigured issue target enabled");
                var status=about.Children.OfType<TextBlock>().Last();ShellOpenForTest=_=>false;OpenLink("mailto:"+FeedbackEmail+"?subject=test",status);Check(status.Text.Contains("复制邮箱"),"mail failure hidden");
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)settingsDialog.ActualWidth,(int)settingsDialog.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(settingsDialog);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(output,"settings-about.png")))encoder.Save(f);
                settingsDialog.DialogResult=false;
            }catch(Exception ex){error=ex;settingsDialog!.Close();}};
            driver.Start();try{ShowSettings();}finally{driver.Stop();ShellOpenForTest=null;ClipboardForTest=null;}if(error!=null)throw error;
        });
    }
}
