using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;
using FocusYourself.Views;

namespace FocusYourself.ViewModels;

/// <summary>App 提供给界面的能力（退出、以管理员重启、悬浮窗开关等）。</summary>
public interface IAppShell
{
    void RequestExit();
    void RestartAsAdmin();
    void ShowToast(ToastKind kind, string title, string message);
    void OnSettingsSaved();
    void SetTheme(string theme);
    void SetLanguage(string language);
    /// <summary>弹出智能拦截确认窗，返回用户勾选的条目；取消返回 null。</summary>
    IReadOnlyList<SmartSuggestion>? ConfirmSmartBlock(IReadOnlyList<SmartSuggestion> suggestions);
    /// <summary>打开扩展安装向导（已打开就切到前台）。</summary>
    void ShowExtensionSetup();
}

public partial class MainViewModel : ObservableObject
{
    readonly FocusCoordinator _coord;
    readonly IAppShell _shell;
    Action? _pendingStrictAction;
    DateTime _strictUnlockAt;

    public MainViewModel(FocusCoordinator coord, IAppShell shell)
    {
        _coord = coord;
        _shell = shell;
        Apps = new AppsViewModel(coord, shell);
        Sites = new SitesViewModel(coord, shell);
        Folders = new FoldersViewModel(coord, shell);
        Settings = new SettingsViewModel(coord, shell);
        ShowSmartBanner = !coord.Config.SmartBlockOffered;
        Loc.LanguageChanged += (_, _) => OnLanguageChanged();
        ThemeManager.ThemeChanged += (_, _) => RefreshThemeTexts();
        RefreshThemeTexts();
        Refresh();
    }

    public AppsViewModel Apps { get; }
    public SitesViewModel Sites { get; }
    public FoldersViewModel Folders { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<RoundDot> RoundDots { get; } = [];

    [ObservableProperty] private string _phaseLabel = "";
    [ObservableProperty] private string _timeText = "25:00";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private Brush _phaseBrush = Brushes.Gray;
    [ObservableProperty] private string _roundText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private int _appsCount;
    [ObservableProperty] private int _sitesCount;
    [ObservableProperty] private int _foldersCount;
    [ObservableProperty] private string _hintText = "";
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFocus;
    [ObservableProperty] private bool _isBreak;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _showFocusActions;
    [ObservableProperty] private bool _showBreakActions;
    [ObservableProperty] private bool _strictPending;
    [ObservableProperty] private bool _strictCanConfirm;
    [ObservableProperty] private string _strictText = "";
    [ObservableProperty] private string _extensionStatus = "";
    [ObservableProperty] private bool _extensionConnected;
    [ObservableProperty] private string _adminStatus = "";
    [ObservableProperty] private bool _strictLocked;
    [ObservableProperty] private string _themeIcon = "";
    [ObservableProperty] private string _themeLabel = "";
    [ObservableProperty] private string _themeTip = "";
    [ObservableProperty] private bool _showSmartBanner;
    [ObservableProperty] private bool _isSmartBusy;

    void RefreshThemeTexts()
    {
        // 显示当前模式；点击按 亮 → 暗 → 自动 循环
        (ThemeIcon, ThemeLabel) = ThemeManager.Mode switch
        {
            ThemeManager.Light => ("\uE706", Loc.T("ThemeLight")), // 太阳
            ThemeManager.Dark => ("\uE708", Loc.T("ThemeDark")),   // 月亮
            _ => ("\uE7F4", Loc.T("ThemeAuto")),                   // 显示器
        };
        ThemeTip = Loc.F("ThemeToggleTip", Loc.T(ThemeManager.NextMode(ThemeManager.Mode) switch
        {
            ThemeManager.Light => "ThemeLight",
            ThemeManager.Dark => "ThemeDark",
            _ => "ThemeAuto",
        }));
    }

    void OnLanguageChanged()
    {
        RefreshThemeTexts();
        Apps.RefreshTexts();
        Settings.RefreshTexts();
        Refresh();
    }

    [RelayCommand]
    void ToggleTheme() => _shell.SetTheme(ThemeManager.NextMode(ThemeManager.Mode));

    [RelayCommand]
    void ToggleLanguage() => _shell.SetLanguage(Loc.Instance.IsChinese ? Loc.English : Loc.Chinese);

    [RelayCommand]
    void OpenExtensionSetup() => _shell.ShowExtensionSetup();

    [RelayCommand]
    void DismissSmartBanner()
    {
        ShowSmartBanner = false;
        _coord.Config.SmartBlockOffered = true;
        _coord.SaveConfig();
    }

    [RelayCommand]
    async Task OpenSmartBlock()
    {
        if (IsSmartBusy) return;
        IsSmartBusy = true;
        try
        {
            await Apps.EnsureScannedAsync();
            if (Apps.LastScan is not { } scan) return;
            var suggestions = SmartBlock.Suggest(scan, _coord.Config);
            if (suggestions.Count == 0)
            {
                _shell.ShowToast(ToastKind.Info, Loc.T("SmartNothingTitle"), Loc.T("SmartNothingBody"));
                return;
            }
            var chosen = _shell.ConfirmSmartBlock(suggestions);
            if (chosen is null) return;
            ApplySmartBlock(chosen);
            DismissSmartBanner();
        }
        finally
        {
            IsSmartBusy = false;
        }
    }

    /// <summary>把智能拦截选中的条目一次性写入规则（只会增加拦截，严格模式下也允许）。</summary>
    public void ApplySmartBlock(IReadOnlyList<SmartSuggestion> chosen)
    {
        Apps.BlockMany(chosen.Where(c => c.Category is SmartCategory.Platforms or SmartCategory.Games or SmartCategory.Entertainment));
        Sites.AddMany(chosen.Where(c => c.Category == SmartCategory.Sites).Select(c => c.Target));
        Folders.AddMany(chosen.Where(c => c.Category == SmartCategory.Folders).Select(c => c.Target));
        _coord.RulesChanged();
        Refresh();

        var apps = chosen.Count(c => c.Category is SmartCategory.Platforms or SmartCategory.Games or SmartCategory.Entertainment);
        var sites = chosen.Count(c => c.Category == SmartCategory.Sites);
        var folders = chosen.Count(c => c.Category == SmartCategory.Folders);
        _shell.ShowToast(ToastKind.Info, Loc.T("SmartAppliedTitle"), Loc.F("SmartAppliedBody", apps, sites, folders));
    }

    /// <summary>每秒刷新一次。</summary>
    public void Refresh()
    {
        var e = _coord.Engine;
        var phase = e.Phase;
        PhaseLabel = PhaseVisuals.Label(phase, e.IsPaused);
        PhaseBrush = PhaseVisuals.Brush(phase, e.IsPaused);
        IsIdle = phase == Phase.Idle;
        IsRunning = !IsIdle;
        IsFocus = e.IsFocus;
        IsBreak = e.IsBreak;
        IsPaused = e.IsPaused;
        ShowFocusActions = e.IsFocus && !e.IsPaused;
        ShowBreakActions = e.IsBreak && !e.IsPaused;
        StrictLocked = _coord.StrictLocked;
        TimeText = IsIdle ? PhaseVisuals.Format(TimeSpan.FromMinutes(_coord.Config.Pomodoro.FocusMinutes)) : PhaseVisuals.Format(e.Remaining);
        Progress = e.Progress;

        var rounds = Math.Max(1, _coord.Config.Pomodoro.RoundsBeforeLongBreak);
        var doneInCycle = e.CompletedFocusRounds % rounds;
        if (phase == Phase.LongBreak) doneInCycle = rounds; // 刚完成一整组
        if (RoundDots.Count != rounds)
        {
            RoundDots.Clear();
            for (int i = 0; i < rounds; i++) RoundDots.Add(new RoundDot());
        }
        for (int i = 0; i < rounds; i++)
        {
            var dot = RoundDots[i];
            dot.Done = i < doneInCycle;
            dot.Current = e.IsFocus && i == doneInCycle;
            dot.Fill = dot.Done ? 1 : dot.Current ? e.Progress : 0;
            dot.Tip = Loc.F(dot.Done ? "TomatoDone" : dot.Current ? "TomatoCurrent" : "TomatoPending", i + 1, rounds);
        }
        RoundText = Loc.F("RoundsDone", e.CompletedFocusRounds);
        AdminStatus = Loc.T(AdminHelper.IsAdmin ? "AdminYes" : "AdminNo");

        var apps = _coord.Config.Apps.Count(a => a.Enabled);
        var sites = _coord.Config.Sites.Count(s => s.Enabled);
        AppsCount = apps;
        SitesCount = sites;
        FoldersCount = _coord.Config.Folders.Count(f => f.Enabled);
        Folders.Refresh();
        SummaryText = Loc.F("SummaryText", apps, sites);
        HintText = Loc.T(phase switch
        {
            Phase.Focus when e.IsPaused => "HintPausedFocus",
            Phase.ShortBreak or Phase.LongBreak when e.IsPaused => "HintPausedBreak",
            Phase.Focus => _coord.Config.StrictMode ? "HintFocusStrict" : "HintFocus",
            Phase.ShortBreak or Phase.LongBreak => "HintBreak",
            _ => "HintIdle",
        });

        if (_pendingStrictAction is not null)
        {
            if (!_coord.StrictLocked)
            {
                CancelStrict();
            }
            else
            {
                var left = _strictUnlockAt - DateTime.UtcNow;
                StrictCanConfirm = left <= TimeSpan.Zero;
                StrictText = StrictCanConfirm ? Loc.T("StrictConfirmReady") : Loc.F("StrictWait", (int)Math.Ceiling(left.TotalSeconds));
            }
        }
        RefreshBridge();
    }

    public void RefreshBridge()
    {
        var conns = _coord.Bridge.Connections;
        ExtensionConnected = conns.Count > 0;
        ExtensionStatus = _coord.Bridge.StartError ?? (conns.Count switch
        {
            0 => Loc.T("ExtNotConnected"),
            1 => Loc.F("ExtConnectedOne", BrowserName(conns[0])),
            _ => Loc.F("ExtConnectedMany", conns.Count),
        });
        Sites.RefreshConnections(conns);
    }

    /// <summary>扩展上报的名字不可靠（Helium 自称 Chrome），优先用实际连接进程的产品名。</summary>
    static string BrowserName(BridgeConnectionInfo c) => c.OwnerPath is { } path ? PathUtil.FriendlyName(path) : c.Browser;

    public int FocusMinutes => _coord.Config.Pomodoro.FocusMinutes;

    /// <summary>与设置页的取值范围一致。</summary>
    public static int ClampFocusMinutes(int minutes) => Math.Clamp(minutes, 1, 180);

    /// <summary>在番茄钟页直接改专注时长（只在空闲时允许），立即保存并同步到设置页。</summary>
    public void SetFocusMinutes(int minutes)
    {
        if (!IsIdle) return;
        minutes = ClampFocusMinutes(minutes);
        if (minutes != FocusMinutes)
        {
            _coord.Config.Pomodoro.FocusMinutes = minutes;
            Settings.FocusMinutes = minutes;
            _coord.SaveConfig();
        }
        Refresh();
    }

    [RelayCommand]
    void StartFocus() => _coord.Engine.StartFocus();

    [RelayCommand]
    void StartBreak() => _coord.Engine.StartBreak();

    /// <summary>暂停只冻结计时、不解除拦截，严格模式下也不需要冷静期。</summary>
    [RelayCommand]
    void TogglePause()
    {
        _coord.Engine.TogglePause();
        Refresh();
    }

    [RelayCommand]
    void Skip() => Guarded(_coord.Engine.Skip);

    [RelayCommand]
    void Stop() => Guarded(_coord.Engine.Stop);

    [RelayCommand]
    void ConfirmStrict()
    {
        if (!StrictCanConfirm) return;
        var action = _pendingStrictAction;
        CancelStrict();
        action?.Invoke();
    }

    [RelayCommand]
    void CancelStrict()
    {
        _pendingStrictAction = null;
        StrictPending = false;
        StrictCanConfirm = false;
    }

    /// <summary>严格模式下，结束专注要先经过冷静期。</summary>
    public void Guarded(Action action)
    {
        if (!_coord.StrictLocked)
        {
            action();
            Refresh();
            return;
        }
        if (_pendingStrictAction is null)
        {
            _strictUnlockAt = DateTime.UtcNow.AddSeconds(Math.Max(1, _coord.Config.StrictUnlockSeconds));
        }
        _pendingStrictAction = action;
        StrictPending = true;
        Refresh();
    }
}

/// <summary>一组番茄里的一颗。</summary>
public partial class RoundDot : ObservableObject
{
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _current;
    [ObservableProperty] private string _tip = "";

    /// <summary>这颗番茄变红的比例 0~1：完成为 1，正在专注时跟着进度走。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillHeight))]
    private double _fill;

    /// <summary>
    /// 界面上番茄按 32 单位高绘制，果实占 y = 7.6 ~ 30.4；红色部分从果实底部往上长，
    /// 让进度和看得见的红色高度成正比。
    /// </summary>
    public double FillHeight => Fill switch
    {
        <= 0 => 0,
        >= 1 => 32,
        _ => Math.Round(1.6 + Fill * 22.8, 2),
    };
}
