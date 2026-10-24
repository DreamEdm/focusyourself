using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.ViewModels;

/// <summary>设置页：编辑的是副本，点“保存设置”才写回配置。</summary>
public partial class SettingsViewModel : ObservableObject
{
    readonly FocusCoordinator _coord;
    readonly IAppShell _shell;

    public SettingsViewModel(FocusCoordinator coord, IAppShell shell)
    {
        _coord = coord;
        _shell = shell;
        Load();
    }

    AppConfig Config => _coord.Config;

    public bool IsAdmin => AdminHelper.IsAdmin;
    public bool IsNotAdmin => !AdminHelper.IsAdmin;
    public string ExtensionId => Config.ExtensionId;
    public ObservableCollection<string> GuardedBrowsers { get; } = [];

    [ObservableProperty] private int _focusMinutes;
    [ObservableProperty] private int _shortBreakMinutes;
    [ObservableProperty] private int _longBreakMinutes;
    [ObservableProperty] private int _roundsBeforeLongBreak;
    [ObservableProperty] private bool _autoStartNextFocus;
    [ObservableProperty] private int _closeGraceSeconds;
    [ObservableProperty] private bool _strictMode;
    [ObservableProperty] private int _strictUnlockSeconds;
    [ObservableProperty] private bool _guardBrowsers;
    [ObservableProperty] private bool _soundEnabled;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private double _overlayOpacity;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private int _bridgePort;
    [ObservableProperty] private string _savedText = "";
    [ObservableProperty] private string _residueText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerTopLeft), nameof(CornerTopRight), nameof(CornerBottomLeft), nameof(CornerBottomRight))]
    private OverlayCorner _corner;

    public bool CornerTopLeft { get => Corner == OverlayCorner.TopLeft; set { if (value) Corner = OverlayCorner.TopLeft; } }
    public bool CornerTopRight { get => Corner == OverlayCorner.TopRight; set { if (value) Corner = OverlayCorner.TopRight; } }
    public bool CornerBottomLeft { get => Corner == OverlayCorner.BottomLeft; set { if (value) Corner = OverlayCorner.BottomLeft; } }
    public bool CornerBottomRight { get => Corner == OverlayCorner.BottomRight; set { if (value) Corner = OverlayCorner.BottomRight; } }

    public void Load()
    {
        var p = Config.Pomodoro;
        FocusMinutes = p.FocusMinutes;
        ShortBreakMinutes = p.ShortBreakMinutes;
        LongBreakMinutes = p.LongBreakMinutes;
        RoundsBeforeLongBreak = p.RoundsBeforeLongBreak;
        AutoStartNextFocus = p.AutoStartNextFocus;
        CloseGraceSeconds = Config.CloseGraceSeconds;
        StrictMode = Config.StrictMode;
        StrictUnlockSeconds = Config.StrictUnlockSeconds;
        GuardBrowsers = Config.GuardBrowsers;
        SoundEnabled = Config.SoundEnabled;
        OverlayEnabled = Config.OverlayEnabled;
        OverlayOpacity = Config.OverlayOpacity;
        Corner = Config.OverlayCorner;
        AutoStart = Config.AutoStart;
        BridgePort = Config.BridgePort;
        GuardedBrowsers.Clear();
        foreach (var b in Config.GuardedBrowsers) GuardedBrowsers.Add(b);
        RefreshResidue();
    }

    [RelayCommand]
    void Save()
    {
        if (_coord.StrictLocked)
        {
            _shell.ShowToast(ToastKind.Warning, Loc.T("StrictTitle"), Loc.T("StrictNoSettings"));
            return;
        }

        var p = Config.Pomodoro;
        p.FocusMinutes = MainViewModel.ClampFocusMinutes(FocusMinutes);
        p.ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60);
        p.LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120);
        p.RoundsBeforeLongBreak = Math.Clamp(RoundsBeforeLongBreak, 1, 12);
        p.AutoStartNextFocus = AutoStartNextFocus;
        Config.CloseGraceSeconds = Math.Clamp(CloseGraceSeconds, 0, 120);
        Config.StrictMode = StrictMode;
        Config.StrictUnlockSeconds = Math.Clamp(StrictUnlockSeconds, 10, 600);
        Config.GuardBrowsers = GuardBrowsers;
        Config.GuardedBrowsers = GuardedBrowsers.ToList();
        Config.SoundEnabled = SoundEnabled;
        Config.OverlayEnabled = OverlayEnabled;
        Config.OverlayOpacity = Math.Clamp(OverlayOpacity, 0.2, 1);
        Config.OverlayCorner = Corner;

        var port = Math.Clamp(BridgePort, 1024, 65535);
        var portChanged = port != Config.BridgePort;
        Config.BridgePort = port;

        if (AutoStart != Config.AutoStart)
        {
            Services.AutoStart.Set(AutoStart);
            Config.AutoStart = AutoStart;
        }

        _coord.RulesChanged(); // 保存配置并让新设置立即生效
        if (portChanged) _coord.RestartBridge();
        Load();
        _shell.OnSettingsSaved();
        SavedText = Loc.T(portChanged ? "SavedPortChanged" : "Saved");
    }

    [RelayCommand]
    void AddBrowser()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("PickBrowserTitle"), Filter = Loc.T("ExeFilter") };
        if (dlg.ShowDialog() != true) return;
        if (!GuardedBrowsers.Any(b => PathUtil.SamePath(b, dlg.FileName))) GuardedBrowsers.Add(dlg.FileName);
    }

    [RelayCommand]
    void AddHelium()
    {
        var helium = BrowserDetector.FindHelium();
        if (helium is null)
        {
            _shell.ShowToast(ToastKind.Warning, Loc.T("HeliumNotFound"), Loc.T("HeliumNotFoundBody"));
            return;
        }
        if (!GuardedBrowsers.Any(b => PathUtil.SamePath(b, helium))) GuardedBrowsers.Add(helium);
    }

    [RelayCommand]
    void RemoveBrowser(string path) => GuardedBrowsers.Remove(path);

    [RelayCommand]
    void RestartAsAdmin() => _shell.RestartAsAdmin();

    [RelayCommand]
    void CleanResidue()
    {
        if (!AdminHelper.IsAdmin)
        {
            _shell.RestartAsAdmin();
            return;
        }
        if (_coord.Engine.IsFocus)
        {
            _shell.ShowToast(ToastKind.Info, Loc.T("FocusInProgress"), Loc.T("StrongCleanLater"));
            return;
        }
        var ok = _coord.Ifeo.Clear();
        RefreshResidue();
        _shell.ShowToast(ok ? ToastKind.Info : ToastKind.Warning, Loc.T(ok ? "Cleaned" : "CleanFailed"), Loc.T(ok ? "CleanedBody" : "CleanFailedBody"));
    }

    void RefreshResidue()
    {
        var residue = _coord.Ifeo.FindResidue();
        ResidueText = residue.Count == 0
            ? Loc.T("ResidueNone")
            : Loc.F("ResidueSome", residue.Count, string.Join(Loc.T("ListSep"), residue.Take(5)));
    }

    /// <summary>切换语言后重新生成代码里拼出来的文字。</summary>
    public void RefreshTexts()
    {
        SavedText = "";
        RefreshResidue();
    }
}
