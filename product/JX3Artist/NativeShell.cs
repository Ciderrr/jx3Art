using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JX3Artist;
public partial class MainWindow
{
    internal const string FeedbackEmail="147984829@qq.com";
    internal const string ProfileUrl="https://github.com/Ciderrr";
    Window? settingsDialog;
    Action<Key,ModifierKeys>? keyCapture;
    string? animatedState;
    bool skinReady;long curtainGeneration;
    internal Func<string,bool>? ShellOpenForTest;
    internal Action<string>? ClipboardForTest;

    static StackPanel DropdownLabel(string text)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(new TextBlock{Text=text,VerticalAlignment=VerticalAlignment.Center});
        // Lucide chevron-down geometry, same 24-unit source used by the prototype icon set.
        var arrow=new System.Windows.Shapes.Path{Data=Geometry.Parse("M 6,9 L 12,15 L 18,9"),StrokeThickness=2,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(arrow);row.Children.Add(new Viewbox{Width=14,Height=14,Child=canvas,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center});return row;
    }
    void CancelCurtain(){curtainGeneration++;CurtainImage.Visibility=Visibility.Collapsed;CurtainImage.Source=null;CurtainImage.OpacityMask=null;}
    ContextMenu CreateMenu(Button owner)
    {
        var menu=new ContextMenu{PlacementTarget=owner,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,HorizontalOffset=0,VerticalOffset=6,Background=(Brush)Resources["Surface"],Foreground=(Brush)Resources["Ink"],BorderBrush=(Brush)Resources["Line"],BorderThickness=new Thickness(.5),Padding=new Thickness(6),MinWidth=owner.ActualWidth};
        menu.Resources.MergedDictionaries.Add(Resources);
        menu.FontFamily=FontFamily;menu.FontSize=14;
        menu.Opened+=(_,_)=>{if(!SystemParameters.ClientAreaAnimation)return;menu.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)));};
        owner.ContextMenu=menu;return menu;
    }
    static MenuItem Item(string title,Action? action=null,bool enabled=true)
    {var item=new MenuItem{Header=title,IsEnabled=enabled};if(action!=null)item.Click+=(_,_)=>action();return item;}
    void ClientMenuClicked(object sender,RoutedEventArgs e)
    {
        var menu=CreateMenu(ClientMenuButton);
        void Heading(string text)=>menu.Items.Add(new MenuItem{Header=text,IsEnabled=false,FontSize=12,Margin=new Thickness(0,10,0,8),Padding=new Thickness(12,4,12,4)});
        Heading("旗舰端");menu.Items.Add(Item("单开　当前"));menu.Items.Add(Item("多开　规划中",enabled:false));menu.Items.Add(new Separator{Margin=new Thickness(8,10,8,4)});
        Heading("无界端");menu.Items.Add(Item("单开　规划中",enabled:false));menu.Items.Add(Item("多开　规划中",enabled:false));menu.IsOpen=true;
    }
    void SkinMenuClicked(object sender,RoutedEventArgs e)
    {var menu=CreateMenu(SkinMenuButton);foreach(string name in UiTheme.Palettes.Keys){var item=Item(name,()=>ChangeSkin(name));item.IsCheckable=true;item.IsChecked=preferences.skin==name;menu.Items.Add(item);}menu.IsOpen=true;}
    void ApplicationMenuClicked(object sender,RoutedEventArgs e)
    {var menu=CreateMenu(SettingsMenuButton);menu.Items.Add(Item("设置",()=>ShowSettings()));menu.Items.Add(Item("画面与记录",()=>{RecordsExpander.IsExpanded=!RecordsExpander.IsExpanded;}));menu.Items.Add(Item("导出问题报告",()=>SupportClicked(this,new RoutedEventArgs())));menu.IsOpen=true;}
    void ChangeSkin(string name,bool save=true)
    {
        if(!UiTheme.Palettes.ContainsKey(name))return;
        BitmapSource? old=null;
        if(skinReady&&IsVisible&&ActualWidth>0&&ActualHeight>0&&SystemParameters.ClientAreaAnimation&&!nativeUiTesting){var bitmap=new RenderTargetBitmap((int)Math.Ceiling(ApplicationRoot.ActualWidth),(int)Math.Ceiling(ApplicationRoot.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(ApplicationRoot);old=bitmap;}
        var previous=preferences;preferences=preferences with{skin=name};UiTheme.Apply(this,name);ApplySkinLayout(name);SkinMenuButton.Content=DropdownLabel(name);
        if(save&&!SavePreferences()){preferences=previous;UiTheme.Apply(this,previous.skin);ApplySkinLayout(previous.skin);SkinMenuButton.Content=DropdownLabel(previous.skin);return;}
        if(old!=null){
            long generation=++curtainGeneration;CurtainImage.Source=old;CurtainImage.Opacity=1;CurtainImage.Visibility=Visibility.Visible;
            var mask=new RadialGradientBrush{Center=new Point(1,0),GradientOrigin=new Point(1,0),RadiusX=.001,RadiusY=.001};mask.GradientStops.Add(new GradientStop(Colors.Transparent,.94));mask.GradientStops.Add(new GradientStop(Colors.Black,1));CurtainImage.OpacityMask=mask;
            var wipe=new DoubleAnimation(.001,1.6,TimeSpan.FromMilliseconds(950)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            wipe.Completed+=(_,_)=>{if(generation!=curtainGeneration)return;CurtainImage.Visibility=Visibility.Collapsed;CurtainImage.Source=null;CurtainImage.OpacityMask=null;};
            mask.BeginAnimation(RadialGradientBrush.RadiusXProperty,wipe);mask.BeginAnimation(RadialGradientBrush.RadiusYProperty,wipe);
        }
        Refresh();
    }
    void ApplySkinLayout(string name)
    {
        // Real native layout families share the same controls and service state.
        bool reverse=name is "幕间" or "墨场" or "续章" or "听雨" or "夜演" or "青岚";
        int plan=reverse?2:0,timerColumn=reverse?0:2;
        foreach(var element in new FrameworkElement[]{PlanCaption,PlanControls,CountsBlock,AutoBlock,ExperienceBlock})Grid.SetColumn(element,plan);
        foreach(var element in new FrameworkElement[]{CountdownCaption,StateBadge,CountdownNote,StateMessage,ActionBlock})Grid.SetColumn(element,timerColumn);
        MainPanel.ColumnDefinitions[0].Width=new GridLength(name=="听雨"?1.35:1,GridUnitType.Star);
        MainPanel.ColumnDefinitions[2].Width=new GridLength(name=="续章"?1.25:1,GridUnitType.Star);
        MainDivider.Visibility=name is "素幕" or "清笺" or "流光"?Visibility.Collapsed:Visibility.Visible;
    }
    void AnimateDisplay(string key)
    {
        if(animatedState==key)return;animatedState=key;
        if(!SystemParameters.ClientAreaAnimation||nativeUiTesting||!IsVisible)return;
        CountdownNote.BeginAnimation(OpacityProperty,new DoubleAnimation(.15,1,TimeSpan.FromMilliseconds(520)));
        var move=new TranslateTransform();CountdownNote.RenderTransform=move;move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(12,0,TimeSpan.FromMilliseconds(520)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    Window Dialog(string title,double width=520,double height=520)
    {
        var dialog=new Window{Owner=this,Title=title,Width=width,Height=height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,FontFamily=FontFamily,FontSize=14};dialog.Resources.MergedDictionaries.Add(Resources);
        if(nativeUiTesting){dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=-12000;dialog.Top=-12000;dialog.ShowActivated=false;dialog.ShowInTaskbar=false;}return dialog;
    }
    static TextBlock Label(string value,double size=14)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
    StackPanel KeyPanel(Window dialog)
    {
        var panel=new StackPanel{Margin=new Thickness(24)};
        var message=Label("支持F1–F24（F12除外）；普通键需搭配Ctrl或Alt，可加Shift。Esc取消录入。");
        var start=new Button{Name="RecordStart",Content=preferences.startHotkey,Margin=new Thickness(0,0,0,14)};
        var stop=new Button{Name="RecordStop",Content=preferences.stopHotkey,Margin=new Thickness(0,0,0,14)};
        string startValue=preferences.startHotkey,stopValue=preferences.stopHotkey;
        void Arm(Button button,bool first)
        {
            keyCapture=null;start.Content=startValue;stop.Content=stopValue;button.Content="请按快捷键…";
            keyCapture=(key,mods)=>{
                if(key==Key.Escape){keyCapture=null;button.Content=first?startValue:stopValue;message.Text="已取消录入。";return;}
                if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)return;
                try{var label=new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key,mods))!;label=AssistantHotkey.Parse(label).Label;
                    if(label==(first?stopValue:startValue))throw new ArgumentException("开始与停止快捷键不能相同，请换一个。");
                    if(first)startValue=label;else stopValue=label;button.Content=label;keyCapture=null;message.Text="已录入，点击保存生效。";
                }catch(Exception ex){message.Text=ex.Message;}
            };
        }
        start.Click+=(_,_)=>Arm(start,true);stop.Click+=(_,_)=>Arm(stop,false);
        panel.Children.Add(Label("开始辅助"));panel.Children.Add(start);panel.Children.Add(Label("停止辅助"));panel.Children.Add(stop);panel.Children.Add(message);
        var save=new Button{Name="RecordSave",Content="保存",HorizontalAlignment=HorizontalAlignment.Right};save.Click+=(_,_)=>{if(keyCapture!=null){message.Text="请先完成按键录入，或按Esc取消。";return;}if(ApplyHotkeys(startValue,stopValue,out var error)){message.Text="快捷键已保存。";if(dialog.Title=="自定义快捷键")dialog.DialogResult=true;}else message.Text=error;};panel.Children.Add(save);
        dialog.PreviewKeyDown+=(_,e)=>{if(keyCapture==null)return;var key=e.Key==Key.System?e.SystemKey:e.Key;keyCapture(key,Keyboard.Modifiers);e.Handled=true;};
        dialog.Closed+=(_,_)=>keyCapture=null;return panel;
    }
    void ShowHotkeyRecorder()
    {
        if(hotkeyDialog!=null){hotkeyDialog.Activate();return;}var dialog=Dialog("自定义快捷键",500,430);dialog.Content=KeyPanel(dialog);hotkeyDialog=dialog;try{dialog.ShowDialog();}finally{hotkeyDialog=null;keyCapture=null;}
    }
    void ShowSettings()
    {
        if(settingsDialog!=null){settingsDialog.Activate();return;}
        var dialog=Dialog("设置",550,620);var tabs=new TabControl{Margin=new Thickness(20),Background=Background,Foreground=Foreground,BorderThickness=new Thickness(0)};
        var preferencesPanel=new StackPanel();var reminder=new CheckBox{Style=(Style)Resources["OptionSwitch"],Content=RemindAgain.IsChecked==true?"开始前提醒 · 已开启":"开始前提醒 · 已关闭",IsChecked=RemindAgain.IsChecked,Margin=new Thickness(24,18,0,6)};
        bool syncing=false;void Changed(){if(syncing)return;RemindAgain.IsChecked=reminder.IsChecked;syncing=true;reminder.IsChecked=RemindAgain.IsChecked;reminder.Content=RemindAgain.IsChecked==true?"开始前提醒 · 已开启":"开始前提醒 · 已关闭";syncing=false;}
        reminder.Checked+=(_,_)=>Changed();reminder.Unchecked+=(_,_)=>Changed();preferencesPanel.Children.Add(reminder);preferencesPanel.Children.Add(KeyPanel(dialog));
        tabs.Items.Add(new TabItem{Header="偏好设置",Content=new ScrollViewer{Content=preferencesPanel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        var about=new StackPanel{Margin=new Thickness(24)};var status=Label("");
        var profile=new Button{Content="Ciderrr · GitHub主页 ↗",HorizontalAlignment=HorizontalAlignment.Left};profile.Click+=(_,_)=>OpenLink(ProfileUrl,status);about.Children.Add(profile);about.Children.Add(Label("唯满侠\n双开门盾萝@长安城"));about.Children.Add(Label("问题反馈",18));
        about.Children.Add(Label("描述遇到的问题，可附截图或问题报告。"));
        var emailRow=new StackPanel{Orientation=Orientation.Horizontal};var email=new Button{Content="邮件反馈 ↗"};email.Click+=(_,_)=>OpenLink("mailto:"+FeedbackEmail+"?subject="+Uri.EscapeDataString("艺人助手问题反馈"),status);emailRow.Children.Add(email);var copy=new Button{Content="复制邮箱"};copy.Click+=async(_,_)=>{for(int attempt=0;attempt<3;attempt++){try{if(ClipboardForTest!=null)ClipboardForTest(FeedbackEmail);else Clipboard.SetText(FeedbackEmail);status.Text="邮箱已复制。";return;}catch(ExternalException){await System.Threading.Tasks.Task.Delay(80);}catch(Exception){break;}}status.Text="复制失败，请选中下方邮箱按Ctrl+C。";};emailRow.Children.Add(copy);about.Children.Add(emailRow);
        about.Children.Add(new TextBox{Text=FeedbackEmail,IsReadOnly=true,BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(0,10,0,12)});
        var issues=new Button{Content="GitHub Issues · 尚未配置",IsEnabled=false,HorizontalAlignment=HorizontalAlignment.Left,ToolTip="上传GitHub后，确认仓库地址再补齐"};about.Children.Add(issues);about.Children.Add(status);
        tabs.Items.Add(new TabItem{Header="关于与反馈",Content=new ScrollViewer{Content=about,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        dialog.Content=tabs;settingsDialog=dialog;hotkeyDialog=dialog;try{dialog.ShowDialog();}finally{settingsDialog=null;hotkeyDialog=null;keyCapture=null;}
    }
    void OpenLink(string target,TextBlock status)
    {
        if(target!=ProfileUrl&&!target.StartsWith("mailto:"+FeedbackEmail+"?",StringComparison.Ordinal))throw new ArgumentException("不允许的反馈链接");
        try{if(ShellOpenForTest!=null){if(!ShellOpenForTest(target))throw new InvalidOperationException();}else Process.Start(new ProcessStartInfo(target){UseShellExecute=true});status.Text=target.StartsWith("mailto:")?"已请求打开邮件应用；未打开时可复制邮箱。":"已请求打开GitHub主页。";}
        catch{status.Text=target.StartsWith("mailto:")?"未能打开邮件应用，请复制邮箱后反馈。":"未能打开浏览器，请访问 "+ProfileUrl;}
    }
}
