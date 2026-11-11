using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Ocr;

namespace JX3Artist;

internal sealed class DistributionWindow : Window
{
    static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(250,249,247));
    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(39,37,34));
    static readonly Brush Bronze = new SolidColorBrush(Color.FromRgb(128,91,73));
    readonly WrapPanel actions = new() { Margin = new Thickness(0,20,0,0) };
    readonly Style? updateButtons;
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0,18,0,6) };
    internal TextBlock Detail { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.DimGray, LineHeight = 22 };
    internal ProgressBar Progress { get; } = new() { Height = 5, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0,14,0,0) };

    internal DistributionWindow(string title, Window? owner = null, string? skin = null)
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = Paper;
        Foreground = Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        if(skin!=null)
        {
            UiTheme.Apply(this,skin);
            WindowStyle=WindowStyle.None;
            Background=Theme("Surface");Foreground=Theme("Ink");
            FontFamily=new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            Width=520;
            actions.HorizontalAlignment=HorizontalAlignment.Right;
            updateButtons=UpdateButtonStyle();
        }
        ShowInTaskbar = owner == null;
        if (owner != null) Owner = owner;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(28) };
        var header = new DockPanel();
        try
        {
            Icon = AppIcon.WindowSource();
            if(skin==null)
            {
                const int size = 44;
                var art = new Image { Width = size, Height = size, Margin = new Thickness(0,0,14,0) };
                void Fit(DpiScale dpi) => art.Source = AppIcon.ImageSource((int)Math.Ceiling(size * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
                Fit(VisualTreeHelper.GetDpi(this));
                SourceInitialized += (_,_) => Fit(VisualTreeHelper.GetDpi(this));
                DpiChanged += (_,e) => Fit(e.NewDpi);
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(art,Dock.Left);header.Children.Add(art);
            }
        }
        catch (IOException) { }
        var titles=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        var heading=new TextBlock { Text = title, FontSize = skin==null?23:24, FontWeight = FontWeights.SemiBold };
        if(skin!=null){heading.FontFamily=new FontFamily("SimSun, Microsoft YaHei UI");heading.FontWeight=FontWeights.Normal;}
        titles.Children.Add(heading);
        if(skin!=null)
        {
            titles.Children.Add(new TextBlock{Text="当前版本 v"+typeof(ReleaseUpdater).Assembly.GetName().Version!.ToString(3),
                FontSize=13,Foreground=Theme("Muted"),Margin=new Thickness(0,4,0,0)});
            titles.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed)DragMove();};
            var close=new Button{Content="×",Style=updateButtons,Width=36,Height=36,MinWidth=0,MinHeight=0,
                FontSize=21,Foreground=Theme("Ink"),Background=Brushes.Transparent,BorderBrush=Brushes.Transparent,
                BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Top};
            AutomationProperties.SetName(close,"关闭软件更新");
            close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        }
        header.Children.Add(titles);
        body.Children.Add(header);
        if(skin==null)
        {
            body.Children.Add(Status);body.Children.Add(Detail);body.Children.Add(Progress);
        }
        else
        {
            Status.Margin=new Thickness(0,22,0,5);Status.FontWeight=FontWeights.SemiBold;
            Detail.FontSize=14;Detail.Foreground=Theme("Muted");
            Progress.Foreground=Theme("AccentText");Progress.Background=Theme("Line");
            body.Children.Add(Status);body.Children.Add(Detail);body.Children.Add(Progress);
        }
        body.Children.Add(actions);
        if(skin==null)Content=body;
        else
        {
            body.Margin=new Thickness(0);
            Content=new Border{Background=Background,Padding=new Thickness(28),BorderBrush=Theme("Line"),
                BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=body};
        }
    }

    Brush Theme(string name)=>(Brush)Resources[name];

    Style UpdateButtonStyle()
    {
        var border=new FrameworkElementFactory(typeof(Border));
        border.Name="Frame";
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));
        foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})
            border.SetBinding(property,new Binding(property.Name){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty,true);
        border.AppendChild(content);
        var template=new ControlTemplate(typeof(Button)){VisualTree=border};
        var style=new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty,template));
        style.Setters.Add(new Setter(MinHeightProperty,36d));
        style.Setters.Add(new Setter(MinWidthProperty,92d));
        style.Setters.Add(new Setter(FocusVisualStyleProperty,null));
        var hovered=new Trigger{Property=IsMouseOverProperty,Value=true};
        hovered.Setters.Add(new Setter(Border.BorderBrushProperty,Theme("Accent"),"Frame"));template.Triggers.Add(hovered);
        var focus=new Trigger{Property=IsKeyboardFocusedProperty,Value=true};
        focus.Setters.Add(new Setter(Border.BorderBrushProperty,Theme("Accent"),"Frame"));template.Triggers.Add(focus);
        var disabled=new Trigger{Property=IsEnabledProperty,Value=false};
        disabled.Setters.Add(new Setter(OpacityProperty,.58));style.Triggers.Add(disabled);
        return style;
    }

    internal Button Action(string label, Action onClick, bool primary = false)
    {
        var button = new Button { Content = label, FontSize = 13, Padding = new Thickness(15,9,15,9), Margin = new Thickness(0,0,8,6),
            Background = primary ? Bronze : Brushes.White, Foreground = primary ? Brushes.White : Ink };
        if(updateButtons!=null)
        {
            button.Style=updateButtons;button.FontSize=14;button.Padding=new Thickness(13,6,13,6);
            button.Margin=new Thickness(8,0,0,0);
            button.Background=primary?Theme("AccentText"):Theme("Surface");
            button.Foreground=primary?Theme("Paper"):Theme("Ink");
            button.BorderBrush=primary?Theme("AccentText"):Theme("Line");
            button.BorderThickness=new Thickness(1);
        }
        button.Click += (_,_) => onClick();
        actions.Children.Add(button);
        return button;
    }

    internal void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { Detail.Text = "请在浏览器中打开 " + target; }
    }
}

internal static class StartupEnvironment
{
    const string MicrosoftDownload = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";
    const string BootstrapperDigest = "CF2F2445FEF0C5C14E372045F84998771EEB8BB01D59BE4AFD00692FA160082E";

    static bool WebViewAvailable() => !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());

    internal static string[] MissingFiles(string root, bool bundled)
    {
        var required = new List<string> { "web/index.html", "web/ui-manifest.json", "Assets/AppIcon.ico" };
        required.AddRange(EngineBundle.Files.Select(f => "engine/" + f));
        if (bundled) required.AddRange(new[] { "python/python.exe", "python/Lib/site-packages/cv2/__init__.py", "python/Lib/site-packages/numpy/__init__.py" });
        return required.Where(f => !File.Exists(Path.Combine(root, f)) || new FileInfo(Path.Combine(root, f)).Length == 0).ToArray();
    }

    internal static bool Ensure(string dataRoot)
    {
        string root = AppContext.BaseDirectory;
        bool supported = OperatingSystem.IsWindowsVersionAtLeast(10,0,22000) && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
        var missing = MissingFiles(root, File.Exists(Path.Combine(root, "distribution.txt")));
        if (!supported || missing.Length > 0)
        {
            var window = new DistributionWindow("检查运行环境");
            window.Status.Text = supported ? "程序文件不完整" : "当前系统版本不在支持范围内";
            window.Detail.Text = supported ? "界面或识别文件需要修复。请重新安装，或完整解压 ZIP 后再打开。\n" + string.Join("\n", missing.Take(5)) : "艺人助手需要 Windows 11 x64。";
            window.Action("打开下载页", () => window.Open(ReleaseUpdater.Releases), true);
            window.Action("退出", window.Close);
            window.ShowDialog();
            return false;
        }

        bool available;
        try { available = WebViewAvailable(); }
        catch (WebView2RuntimeNotFoundException) { available = false; }
        catch (Exception)
        {
            var window = new DistributionWindow("检查运行环境");
            window.Status.Text = "无法检查 WebView2";
            window.Detail.Text = "请重新安装助手，或在微软官网下载并修复 WebView2。";
            window.Action("微软官网下载", () => window.Open(MicrosoftDownload), true);
            window.Action("退出", window.Close);
            window.ShowDialog();
            return false;
        }
        if (!available && !InstallWebView()) return false;

        string reminder = Path.Combine(dataRoot, "ocr-notice-read");
        if (!File.Exists(reminder) && !HasOcr())
        {
            var window = new DistributionWindow("检查运行环境");
            window.Status.Text = "可选组件：Windows 文字识别";
            window.Detail.Text = "文字识别用来读取倒计时。可以继续使用助手，倒计时读数暂时显示未知。";
            window.Action("继续使用", () => { window.DialogResult = true; }, true);
            window.Action("Windows 语言设置", () => window.Open("ms-settings:regionlanguage"));
            if (window.ShowDialog() != true) return false;
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(reminder, "read");
        }
        return true;
    }

    static bool HasOcr()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
        catch (Exception) { return false; }
    }

    static bool InstallWebView()
    {
        var window = new DistributionWindow("检查运行环境");
        window.Status.Text = "缺少 Microsoft Edge WebView2 Runtime";
        window.Detail.Text = "这是用于显示助手界面的 Windows 组件。安装后会重新检测。";
        string installer = Path.Combine(AppContext.BaseDirectory, "prerequisites", "MicrosoftEdgeWebview2Setup.exe");
        Button? install = null;
        var close = new CancellationTokenSource();
        window.Closed += (_,_) => close.Cancel();
        async void Install()
        {
            install!.IsEnabled = false;
            window.Progress.IsIndeterminate = true;
            window.Progress.Visibility = Visibility.Visible;
            window.Status.Text = "正在安装 WebView2";
            try
            {
                using var file = File.OpenRead(installer);
                if (Convert.ToHexString(SHA256.HashData(file)) != BootstrapperDigest) throw new IOException("安装文件校验失败。");
                var start = new ProcessStartInfo(installer) { UseShellExecute = true };
                start.ArgumentList.Add("/silent");
                start.ArgumentList.Add("/install");
                using var process = Process.Start(start) ?? throw new IOException("安装程序未能启动。");
                await process.WaitForExitAsync(close.Token);
                if (WebViewAvailable()) { window.DialogResult = true; return; }
                window.Status.Text = "安装结束后仍未检测到 WebView2";
                window.Detail.Text = $"安装程序退出码 {process.ExitCode}。你可以重试，或通过微软官网下载。";
            }
            catch (OperationCanceledException) when (close.IsCancellationRequested) { }
            catch (Exception ex) { window.Status.Text = "WebView2 安装未完成";window.Detail.Text = ex.Message; }
            finally { install.IsEnabled = true;window.Progress.Visibility = Visibility.Collapsed; }
        }
        install = window.Action("一键安装", Install, true);
        install.IsEnabled = File.Exists(installer);
        window.Action("微软官网下载", () => window.Open(MicrosoftDownload));
        window.Action("退出", window.Close);
        return window.ShowDialog() == true;
    }
}

internal static class UpdateDialog
{
    internal static DistributionWindow CreateWindow(Window? owner,string skin)
    {
        var window=new DistributionWindow("软件更新",owner,skin);
        window.Status.Text = "正在检查更新";
        window.Detail.Text = "查询项目的 GitHub 正式发行版。";
        window.Progress.Visibility = Visibility.Visible;
        window.Progress.IsIndeterminate = true;
        return window;
    }
    internal static UpdateLaunch? Show(Window owner, string dataRoot,string skin)
    {
        var window=CreateWindow(owner,skin);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        window.Closed += (_,_) => cancellation.Cancel();
        UpdateLaunch? result = null;
        var dismiss = window.Action("关闭", window.Close);
        window.Action("发布页", () => window.Open(ReleaseUpdater.Releases));
        window.Loaded += async (_,_) =>
        {
            try
            {
                var asset = await ReleaseUpdater.Check(token);
                if (asset == null) { window.Status.Text = "当前已是最新版本";window.Detail.Text = "已核对 GitHub 正式发行版。";return; }
                window.Status.Text = "可更新到 v" + asset.Version;
                window.Detail.Text = $"需要下载约 {asset.Size/1048576d:0} MB。" + (asset.Installer ? "安装版会在原目录升级。" : "便携版会在新目录打开，原目录可用于回退。");
                Button? download = null;
                async void Download()
                {
                    download!.IsEnabled = false;
                    dismiss.Content = "取消下载";
                    window.Status.Text = "正在下载更新";
                    window.Progress.IsIndeterminate = false;
                    window.Progress.Value = 0;
                    window.Progress.Visibility = Visibility.Visible;
                    try
                    {
                        var progress = new Progress<int>(p => { window.Progress.Value = p;window.Detail.Text = p == 100 ? "正在校验并准备新版" : $"下载进度 {p}%"; });
                        result = await ReleaseUpdater.Download(asset, dataRoot, progress, token);
                        token.ThrowIfCancellationRequested();
                        window.DialogResult = true;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result = null; }
                    catch (OperationCanceledException) { window.Status.Text = "下载超时";window.Detail.Text = "请重试，或在浏览器打开发布页下载。"; }
                    catch (Exception ex) { window.Status.Text = "更新未完成";window.Detail.Text = ProblemCatalog.UserMessage(ex.Message,"请重试，或在浏览器打开发布页下载。"); }
                    finally { download.IsEnabled = true;dismiss.Content = "关闭";window.Progress.Visibility = Visibility.Collapsed; }
                }
                download = window.Action("下载并更新", Download, true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (OperationCanceledException) { window.Status.Text = "检查更新超时";window.Detail.Text = "请重新打开更新窗口，或在浏览器打开发布页。"; }
            catch (Exception ex) { window.Status.Text = "暂时无法检查更新";window.Detail.Text = ProblemCatalog.UserMessage(ex.Message,"请稍后重试，或在浏览器打开发布页查看。"); }
            finally { window.Progress.Visibility = Visibility.Collapsed; }
        };
        return window.ShowDialog() == true ? result : null;
    }
}
