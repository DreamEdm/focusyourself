using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusYourself.Core;
using FocusYourself.Services;
using FocusYourself.ViewModels;
using FocusYourself.Views;
using H.NotifyIcon;

namespace FocusYourself;

public partial class App : Application, IFocusUi, IAppShell
{
    const string MutexName = @"Local\FocusYourself.SingleInstance";
    const string ShowEventName = @"Local\FocusYourself.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    ConfigStore _store = null!;
    FocusCoordinator _coord = null!;
    MainViewModel _vm = null!;
    MainWindow? _main;
    OverlayWindow? _overlay;
    TaskbarIcon? _tray;
    MenuItem? _trayHeader;
    MenuItem? _trayOverlayItem;
    readonly List<(MenuItem Item, string Key)> _trayItems = [];
    DispatcherTimer? _timer;
    Phase? _iconPhase;
    bool _iconPaused;
    MenuItem? _trayPauseItem;
    bool _hiddenHintShown;
    bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var args = e.Args;
        if (args.Length > 0 && args[0] == IfeoBlocker.BlockedArg)
        {
            RunBlockedMode(args.Skip(1).ToArray());
            return;
        }
        if (args.Length > 0 && args[0] == UninstallCleanupArg)
        {
            RunUninstallCleanup();
            return;
        }
        if (!AcquireSingleInstance(waitForPrevious: args.Contains("--restarted")))
        {
            Shutdown();
            return;
        }

        _store = new ConfigStore();
        var config = _store.LoadConfig();
        ApplyAppearance(config);
        if (!config.GuardedBrowsersInitialized)
        {
            // 首次运行：自动把 Helium 加入浏览器守护
            if (BrowserDetector.FindHelium() is { } helium) config.GuardedBrowsers.Add(helium);
            config.GuardedBrowsersInitialized = true;
            _store.SaveConfig(config);
        }

        _coord = new FocusCoordinator(_store, config, this);
        _vm = new MainViewModel(_coord, this);
        _coord.StateChanged += (_, _) => OnTick(tickEngine: false);
        _coord.Bridge.ConnectionsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _coord.NoteBridgeActivity();
            _vm.RefreshBridge();
        });
        ToastManager.ToastClicked += (_, _) => ShowMainWindow();
        // 手动切换或“自动”模式下系统亮暗变化：同步扩展页面和托盘/悬浮窗
        ThemeManager.ThemeChanged += (_, _) =>
        {
            _coord.BroadcastState();
            OnTick(tickEngine: false);
        };

        CreateTray();
        _coord.Initialize(_store.LoadState());
        if (_coord.Bridge.StartError is { } err) ShowToast(ToastKind.Warning, Loc.T("BridgePortBusy"), err);

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick(tickEngine: true);
        _timer.Start();
        OnTick(tickEngine: false);

        if (!args.Contains(AutoStart.AutoStartArg)) ShowMainWindow();
        else if (_coord.Engine.IsFocus && !_coord.Engine.IsPaused) ShowToast(ToastKind.Focus, Loc.T("FocusResumed"), Loc.F("FocusResumedBody", PhaseVisuals.Format(_coord.Engine.Remaining)));

        _ = _vm.Apps.ScanAsync();

        if (!AdminHelper.IsAdmin && !_coord.Engine.IsFocus && _coord.Ifeo.FindResidue().Count > 0)
            ShowToast(ToastKind.Warning, Loc.T("ResidueFound"), Loc.T("ResidueFoundBody"));
    }

    /// <summary>主题：默认“自动”跟随 Windows，可手动固定亮色/暗色。语言：首次跟随系统（中文→简体中文，其余英文）。</summary>
    static void ApplyAppearance(AppConfig config)
    {
        Loc.Instance.SetLanguage(config.Language ?? Loc.SystemDefault());
        ThemeManager.Apply(config.Theme);
    }

    // ================= IFEO 拦截实例 =================

    /// <summary>
    /// 被拦截的程序启动时，Windows 实际运行的是 "FocusYourself.exe --blocked 原程序路径 原参数"。
    /// 这里只显示提示窗，关闭后退出，不启动原程序。
    /// </summary>
    void RunBlockedMode(string[] rest)
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        var path = rest.FirstOrDefault() ?? "";
        var name = File.Exists(path) ? PathUtil.FriendlyName(path) : Path.GetFileNameWithoutExtension(path);

        var store = new ConfigStore();
        var config = store.LoadConfig();
        ApplyAppearance(config);
        var engine = new PomodoroEngine(config.Pomodoro);
        engine.Restore(store.LoadState());

        if (config.SoundEnabled) ChimePlayer.Play(Chime.Blocked);
        BlockedWindow.CreateStandalone(name, path, () => new FocusClock(engine.IsFocus, engine.Remaining, engine.Progress)).Show();
    }

    // ================= 卸载清理 =================

    /// <summary>安装包卸载时调用（以管理员身份）：移除开机自启和本程序写入的 IFEO 拦截，然后退出。</summary>
    public const string UninstallCleanupArg = "--uninstall-cleanup";

    void RunUninstallCleanup()
    {
        try { AutoStart.Set(false); } catch { }
        try { if (AdminHelper.IsAdmin) new IfeoBlocker().Clear(); } catch { }
        Shutdown();
    }

    // ================= 单实例 =================

    bool AcquireSingleInstance(bool waitForPrevious)
    {
        _mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // 上一个实例被强制结束
        }

        if (!owned)
        {
            // 已有实例在运行：通知它显示主窗口
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); }
            }
            catch (UnauthorizedAccessException) { } // 对方以管理员身份运行
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "ShowSignal" };
        thread.Start();
        return true;
    }

    // ================= 主循环 =================

    void OnTick(bool tickEngine)
    {
        if (_exiting) return;
        if (tickEngine) _coord.Tick();
        _vm.Refresh();
        UpdateOverlay();
        UpdateTray();
    }

    FocusClock Clock() => new(_coord.Engine.IsFocus, _coord.Engine.Remaining, _coord.Engine.Progress);

    void UpdateOverlay()
    {
        var e = _coord.Engine;
        var show = _coord.Config.OverlayEnabled && e.Phase != Phase.Idle;
        if (!show)
        {
            _overlay?.Hide();
            return;
        }
        _overlay ??= new OverlayWindow();
        _overlay.Update(e.Phase, e.IsPaused, e.Remaining, e.Progress, _coord.Config.OverlayCorner, _coord.Config.OverlayOpacity);
        if (!_overlay.IsVisible) _overlay.Show();
    }

    // ================= 托盘 =================

    void CreateTray()
    {
        _trayHeader = new MenuItem { IsEnabled = false };
        _trayOverlayItem = new MenuItem { IsCheckable = true };
        _trayItems.Add((_trayOverlayItem, "TrayOverlay"));
        _trayOverlayItem.Click += (_, _) =>
        {
            _coord.Config.OverlayEnabled = _trayOverlayItem.IsChecked;
            _coord.SaveConfig();
            _vm.Settings.Load();
            UpdateOverlay();
        };

        var menu = new ContextMenu();
        menu.Items.Add(_trayHeader);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("TrayOpen", ShowMainWindow));
        menu.Items.Add(Item("TrayStartFocus", () => _coord.Engine.StartFocus()));
        // 文字随状态在“暂停”/“继续”之间切换，见 UpdateTray
        _trayPauseItem = new MenuItem();
        _trayPauseItem.Click += (_, _) => _coord.Engine.TogglePause();
        menu.Items.Add(_trayPauseItem);
        menu.Items.Add(Item("TraySkip", () => { ShowMainWindow(); _vm.Guarded(_coord.Engine.Skip); }));
        menu.Items.Add(Item("TrayStop", () => { ShowMainWindow(); _vm.Guarded(_coord.Engine.Stop); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(_trayOverlayItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("TrayExit", RequestExit));
        LocalizeTray();
        Loc.LanguageChanged += (_, _) =>
        {
            LocalizeTray();
            UpdateTray();
        };

        _tray = new TaskbarIcon
        {
            ToolTipText = "FocusYourself",
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _tray.ForceCreate(enablesEfficiencyMode: false);
        UpdateTray();

        MenuItem Item(string key, Action action)
        {
            var mi = new MenuItem();
            mi.Click += (_, _) => action();
            _trayItems.Add((mi, key));
            return mi;
        }
    }

    void LocalizeTray()
    {
        foreach (var (item, key) in _trayItems) item.Header = Loc.T(key);
    }

    void UpdateTray()
    {
        if (_tray is null) return;
        var e = _coord.Engine;
        if (_iconPhase != e.Phase || _iconPaused != e.IsPaused)
        {
            _iconPhase = e.Phase;
            _iconPaused = e.IsPaused;
            var old = _tray.Icon;
            _tray.Icon = TrayIconFactory.CreateIcon(e.Phase, e.IsPaused);
            old?.Dispose();
            if (_main is not null) _main.Icon = TrayIconFactory.CreateImageSource(e.Phase, e.IsPaused);
        }
        var text = e.Phase == Phase.Idle ? $"FocusYourself · {Loc.T("TrayIdle")}" : $"FocusYourself · {PhaseVisuals.Label(e.Phase, e.IsPaused)} {PhaseVisuals.Format(e.Remaining)}";
        _tray.ToolTipText = text;
        if (_trayHeader is not null) _trayHeader.Header = text;
        if (_trayPauseItem is not null)
        {
            _trayPauseItem.Header = Loc.T(e.IsPaused ? "TrayResume" : "TrayPause");
            _trayPauseItem.IsEnabled = e.Phase != Phase.Idle;
        }
        if (_trayOverlayItem is not null) _trayOverlayItem.IsChecked = _coord.Config.OverlayEnabled;
    }

    void ShowMainWindow()
    {
        if (_exiting) return;
        if (_main is null)
        {
            _main = new MainWindow { DataContext = _vm, Icon = TrayIconFactory.CreateImageSource(_coord.Engine.Phase, _coord.Engine.IsPaused) };
            _main.HiddenToTray += (_, _) =>
            {
                if (_hiddenHintShown) return;
                _hiddenHintShown = true;
                ShowToast(ToastKind.Info, Loc.T("StillRunning"), Loc.T("StillRunningBody"));
            };
        }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    // ================= IFocusUi / IAppShell =================

    public void ShowToast(ToastKind kind, string title, string message) =>
        Dispatcher.BeginInvoke(() => ToastManager.Show(kind, title, message));

    public void ShowBlocked(string appName, string appPath) =>
        Dispatcher.BeginInvoke(() => BlockedWindow.ShowFor(appName, appPath, Clock));

    public void ShowFolderBlocked(string folderName, string openedPath, int continueDelaySeconds, Action allow) =>
        Dispatcher.BeginInvoke(() => BlockedWindow.ShowFolder(folderName, openedPath, Clock, continueDelaySeconds, allow));

    public void PlayChime(Chime chime)
    {
        if (_coord.Config.SoundEnabled) ChimePlayer.Play(chime);
    }

    public void OnSettingsSaved() => OnTick(tickEngine: false);

    public void SetTheme(string theme)
    {
        ThemeManager.Apply(theme);
        _coord.Config.Theme = ThemeManager.Mode;
        _coord.SaveConfig();
    }

    public void SetLanguage(string language)
    {
        Loc.Instance.SetLanguage(language);
        _coord.Config.Language = Loc.Instance.Language;
        _coord.SaveConfig();
        _coord.BroadcastState();
    }

    public IReadOnlyList<SmartSuggestion>? ConfirmSmartBlock(IReadOnlyList<SmartSuggestion> suggestions)
    {
        var dlg = new SmartBlockWindow(suggestions) { Owner = _main is { IsVisible: true } ? _main : null };
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }

    public void RequestExit()
    {
        if (_coord.StrictLocked)
        {
            ShowToast(ToastKind.Warning, Loc.T("StrictTitle"), Loc.T("StrictNoExit"));
            return;
        }
        if (_coord.Engine.IsFocus)
        {
            var r = MessageBox.Show(Loc.T("ExitConfirm"), "FocusYourself",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        ExitApp(endFocus: true);
    }

    public void RestartAsAdmin()
    {
        if (!AdminHelper.RestartAsAdmin()) return; // 用户在 UAC 中取消
        // 不结束专注：新实例会从 state.json 恢复
        ExitApp(endFocus: false);
    }

    void ExitApp(bool endFocus)
    {
        if (_exiting) return;
        _exiting = true;
        _timer?.Stop();
        if (endFocus && _coord.Engine.Phase != Phase.Idle)
        {
            _coord.Engine.Stop(); // 会写入 state.json 并广播 idle
        }
        _coord.Shutdown(endFocus);
        _tray?.Dispose();
        _overlay?.Close();
        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }
        ReleaseInstance();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 注销/关机：保留专注状态（下次登录继续），但清理 IFEO
        if (!_exiting && _coord is not null)
        {
            _exiting = true;
            _coord.Shutdown(endFocus: false);
            _tray?.Dispose();
        }
        base.OnSessionEnding(e);
    }

    void ReleaseInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
        _showEvent?.Set(); // 让等待线程退出
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusYourself");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        e.Handled = true;
    }
}
