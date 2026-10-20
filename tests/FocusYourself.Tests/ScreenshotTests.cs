using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusYourself.Core;
using FocusYourself.Services;
using FocusYourself.ViewModels;
using FocusYourself.Views;

namespace FocusYourself.Tests;

/// <summary>
/// 把各个窗口离屏渲染成 PNG，用于检查界面效果。只在设置了环境变量 FY_SCREENSHOTS（输出目录）时运行：
///   FY_SCREENSHOTS=docs/screenshots dotnet test --filter ScreenshotTests（相对路径以仓库根目录为准）
/// </summary>
public class ScreenshotTests
{
    sealed class Shell : IAppShell, IFocusUi
    {
        public void RequestExit() { }
        public void RestartAsAdmin() { }
        public void ShowToast(ToastKind kind, string title, string message) { }
        public void OnSettingsSaved() { }
        public void ShowBlocked(string appName, string appPath) { }
        public void ShowFolderBlocked(string folderName, string openedPath, int continueDelaySeconds, Action allow) { }
        public void PlayChime(Chime chime) { }
        public void SetTheme(string theme) => ThemeManager.Apply(theme);
        public void SetLanguage(string language) => Loc.Instance.SetLanguage(language);
        public IReadOnlyList<SmartSuggestion>? ConfirmSmartBlock(IReadOnlyList<SmartSuggestion> suggestions) => null;
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FocusYourself.sln"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }

    [Fact]
    public void Render_all_windows()
    {
        var outDir = Environment.GetEnvironmentVariable("FY_SCREENSHOTS");
        if (string.IsNullOrEmpty(outDir)) return;
        // 相对路径按仓库根目录（FocusYourself.sln 所在目录）解析，而不是测试的 bin 目录
        if (!Path.IsPathRooted(outDir)) outDir = Path.Combine(RepoRoot(), outDir);
        Directory.CreateDirectory(outDir);

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RenderAll(outDir); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    static void RenderAll(string outDir)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        // 不能 new App()：它会在消息循环里执行真正的启动逻辑（单实例、托盘、端口、读写用户配置）。
        // 这里只建一个空的 Application 并加载主题。
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FocusYourself;component/Themes/Colors.Dark.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FocusYourself;component/Themes/Theme.xaml"),
        });

        foreach (var (theme, lang) in new[] { (ThemeManager.Dark, Loc.English), (ThemeManager.Light, Loc.Chinese) })
        {
            ThemeManager.Apply(theme, app);
            Loc.Instance.SetLanguage(lang);
            RenderVariant(app, Path.Combine(outDir, $"{theme}-{lang}"));
        }
        Loc.Instance.SetLanguage(Loc.English);
        ThemeManager.Apply(ThemeManager.Dark, app);
    }

    static void RenderVariant(Application app, string outDir)
    {
        Directory.CreateDirectory(outDir);

        var dir = Path.Combine(Path.GetTempPath(), "fy-shots-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        var config = store.LoadConfig();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        config.BridgePort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        config.Apps.Add(new AppRule { Name = "Steam", Path = @"C:\Program Files (x86)\Steam\steam.exe" });
        config.Apps.Add(new AppRule { Name = "Hollow Knight", Path = @"D:\SteamLibrary\steamapps\common\Hollow Knight", Kind = AppRuleKind.Folder });
        config.Sites.Add(new SiteRule { Domain = "douyin.com" });
        config.Sites.Add(new SiteRule { Domain = "weibo.com", Enabled = false });
        config.Folders.Add(new FolderRule { Path = @"D:\Games" });
        config.Folders.Add(new FolderRule { Path = @"D:\Downloads\Videos" });
        config.GuardedBrowsers.Add(@"C:\Users\me\AppData\Local\imput\Helium\Application\chrome.exe");

        var shell = new Shell();
        var coord = new FocusCoordinator(store, config, shell);
        coord.Initialize(new FocusState());
        var vm = new MainViewModel(coord, shell);

        var scan = vm.Apps.ScanAsync();
        while (!scan.IsCompleted) Pump(TimeSpan.FromMilliseconds(100));

        // 模拟专注进行中：第 3 个番茄，已过 7 分钟
        coord.Engine.Restore(new FocusState
        {
            Phase = Phase.Focus,
            PhaseStartedUtc = DateTime.UtcNow.AddMinutes(-7),
            EndsAtUtc = DateTime.UtcNow.AddMinutes(18),
            CompletedFocusRounds = 2,
        });
        vm.Refresh();

        var main = new MainWindow { DataContext = vm, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        string[] pages = ["pomodoro", "apps", "sites", "folders", "settings"];
        for (int i = 0; i < pages.Length; i++)
        {
            main.Tabs.SelectedIndex = i;
            Pump(TimeSpan.FromMilliseconds(400));
            Save((FrameworkElement)main.Content, (Brush)app.FindResource("BgBrush"), Path.Combine(outDir, $"main-{pages[i]}.png"));
        }
        vm.Guarded(() => { });
        config.StrictMode = true;
        vm.Guarded(() => { });
        main.Tabs.SelectedIndex = 0;
        Pump(TimeSpan.FromMilliseconds(400));
        Save((FrameworkElement)main.Content, (Brush)app.FindResource("BgBrush"), Path.Combine(outDir, "main-pomodoro-strict.png"));
        // 运行时切换主题和语言：已打开的窗口应立即跟着变
        var (theme0, lang0) = (ThemeManager.Current, Loc.Instance.Language);
        vm.ToggleThemeCommand.Execute(null);
        vm.ToggleLanguageCommand.Execute(null);
        main.Tabs.SelectedIndex = 1;
        Pump(TimeSpan.FromMilliseconds(400));
        Save((FrameworkElement)main.Content, (Brush)app.FindResource("BgBrush"), Path.Combine(outDir, "main-apps-toggled.png"));
        ThemeManager.Apply(theme0, app);
        Loc.Instance.SetLanguage(lang0);
        Pump(TimeSpan.FromMilliseconds(200));

        main.AllowClose = true;
        main.Close();

        var smart = new SmartBlockWindow(
        [
            new SmartSuggestion(SmartCategory.Platforms, "Steam", @"C:\Program Files (x86)\Steam\steam.exe", Detail: @"C:\Program Files (x86)\Steam\steam.exe"),
            new SmartSuggestion(SmartCategory.Platforms, "Epic Games Launcher", @"C:\Program Files\Epic Games\Launcher\EpicGamesLauncher.exe", Detail: @"C:\Program Files\Epic Games\Launcher\EpicGamesLauncher.exe"),
            new SmartSuggestion(SmartCategory.Games, "Hollow Knight", @"D:\SteamLibrary\steamapps\common\Hollow Knight", AppRuleKind.Folder, @"D:\SteamLibrary\steamapps\common\Hollow Knight"),
            new SmartSuggestion(SmartCategory.Games, "Hades", @"D:\SteamLibrary\steamapps\common\Hades", AppRuleKind.Folder, @"D:\SteamLibrary\steamapps\common\Hades"),
            new SmartSuggestion(SmartCategory.Entertainment, "哔哩哔哩", @"C:\Program Files\bilibili\哔哩哔哩.exe", Detail: @"C:\Program Files\bilibili\哔哩哔哩.exe"),
            new SmartSuggestion(SmartCategory.Sites, "youtube.com", "youtube.com"),
            new SmartSuggestion(SmartCategory.Sites, "reddit.com", "reddit.com"),
            new SmartSuggestion(SmartCategory.Sites, "twitch.tv", "twitch.tv"),
            new SmartSuggestion(SmartCategory.Folders, "SteamLibrary", @"D:\SteamLibrary", AppRuleKind.Folder, @"D:\SteamLibrary"),
        ]) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false };
        smart.Groups[1].Items[1].IsChecked = false;
        smart.Show();
        Pump(TimeSpan.FromMilliseconds(400));
        Save(smart, null, Path.Combine(outDir, "smart-block.png"));
        smart.SkipCountdown();
        Pump(TimeSpan.FromMilliseconds(100));
        Save(smart, null, Path.Combine(outDir, "smart-block-ready.png"));
        smart.Close();

        var clock = () => new FocusClock(true, coord.Engine.Remaining, coord.Engine.Progress);
        var blocked = BlockedWindow.CreateStandalone("Steam", @"C:\Windows\explorer.exe", clock);
        blocked.WindowStartupLocation = WindowStartupLocation.Manual;
        blocked.Left = -30000;
        blocked.Show();
        Pump(TimeSpan.FromMilliseconds(900));
        Save(blocked, null, Path.Combine(outDir, "blocked.png"));
        blocked.Close();

        BlockedWindow.ShowFolder("Videos", @"D:\Downloads\Videos\动漫", clock, 3, () => { });
        Pump(TimeSpan.FromMilliseconds(900));
        var folderWindow = Application.Current.Windows.OfType<BlockedWindow>().First();
        folderWindow.Left = -30000;
        Pump(TimeSpan.FromMilliseconds(200));
        Save(folderWindow, null, Path.Combine(outDir, "blocked-folder.png"));
        folderWindow.Close();

        var toast = new ToastWindow(ToastKind.Focus, Loc.F("ToastFocusTitle", 3), Loc.F("ToastFocusBody", 25, 1, 3));
        toast.Left = -30000;
        toast.Show();
        Pump(TimeSpan.FromMilliseconds(700));
        Save(toast, null, Path.Combine(outDir, "toast-focus.png"));
        toast.Close();

        var toast2 = new ToastWindow(ToastKind.Break, Loc.T("ToastFocusDone"), Loc.F("ToastShortBreakBody", 5));
        toast2.Left = -30000;
        toast2.Show();
        Pump(TimeSpan.FromMilliseconds(700));
        Save(toast2, null, Path.Combine(outDir, "toast-break.png"));
        toast2.Close();

        var overlay = new OverlayWindow();
        overlay.Show();
        overlay.Left = -30000;
        overlay.Update(Phase.Focus, coord.Engine.Remaining, coord.Engine.Progress, OverlayCorner.TopRight, 0.85);
        Pump(TimeSpan.FromMilliseconds(300));
        overlay.Update(Phase.Focus, coord.Engine.Remaining, coord.Engine.Progress, OverlayCorner.TopRight, 0.85);
        Pump(TimeSpan.FromMilliseconds(100));
        Save(overlay, null, Path.Combine(outDir, "overlay.png"));
        overlay.Close();

        using (var bmp = TrayIconFactory.DrawBitmap(Phase.Focus, 64))
            bmp.Save(Path.Combine(outDir, "tray-focus.png"));

        coord.Shutdown(endFocus: true);
        Directory.Delete(dir, recursive: true);
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Save(FrameworkElement element, Brush? background, string path)
    {
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        var scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 透明窗口放在深灰底上，方便看清阴影和圆角
            dc.DrawRectangle(background ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
            }, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
