using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.ViewModels;

public enum SetupPage { Choose, Install, Done }

/// <summary>
/// 扩展安装向导：先勾选浏览器，再逐个打开扩展页并等待扩展连上桥接，最后汇总。
/// 连接状态由窗口每秒调用 <see cref="Tick"/> 刷新。
/// </summary>
public partial class ExtensionSetupViewModel : ObservableObject
{
    readonly Func<IReadOnlyList<BridgeConnectionInfo>> _connections;
    readonly Func<InstalledBrowser, Task<bool>> _openPage;
    readonly Action<string> _revealFolder;
    CancellationTokenSource? _advance;

    /// <param name="openPage">打开扩展页；测试和截图里换成不真的启动浏览器的版本。</param>
    /// <param name="revealFolder">在资源管理器里选中扩展文件夹；同上。</param>
    public ExtensionSetupViewModel(IEnumerable<InstalledBrowser> browsers, string extensionFolder,
        Func<IReadOnlyList<BridgeConnectionInfo>> connections,
        Func<InstalledBrowser, Task<bool>>? openPage = null, Action<string>? revealFolder = null)
    {
        ExtensionFolder = extensionFolder;
        _connections = connections;
        _openPage = openPage ?? (b => BrowserInstaller.OpenExtensionsPageAsync(b));
        _revealFolder = revealFolder ?? BrowserInstaller.RevealFolder;
        foreach (var b in browsers) Browsers.Add(new BrowserChoice(this, b));
        Tick();
        // 默认勾上还没连上的 Chromium 浏览器
        foreach (var b in Browsers) b.IsChecked = b.Supported && !b.IsConnected;
        SelectionChanged();
    }

    public ObservableCollection<BrowserChoice> Browsers { get; } = [];
    /// <summary>这次要装的浏览器，按顺序。</summary>
    public ObservableCollection<BrowserChoice> Queue { get; } = [];
    public string ExtensionFolder { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosePage), nameof(IsInstallPage), nameof(IsDonePage))]
    private SetupPage _page = SetupPage.Choose;

    public bool IsChoosePage => Page == SetupPage.Choose;
    public bool IsInstallPage => Page == SetupPage.Install;
    public bool IsDonePage => Page == SetupPage.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallTitle), nameof(DevModeText), nameof(WaitingText), nameof(IsLast), nameof(NextText))]
    private BrowserChoice? _current;

    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private string _stepOfText = "";
    [ObservableProperty] private string _openText = "";
    [ObservableProperty] private bool _isOpening;
    [ObservableProperty] private bool _openFailed;
    [ObservableProperty] private bool _currentConnected;
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private bool _allConnected;

    public bool HasBrowsers => Browsers.Count > 0;
    public string InstallTitle => Current is null ? "" : Loc.F("SetupInstallTitle", Current.Name);
    public string DevModeText => Loc.T(Current?.Browser.Family == BrowserFamily.Edge ? "SetupStep2Edge" : "SetupStep2");
    public string WaitingText => Current is null ? "" : Loc.F("SetupWaiting", Current.Name);
    public bool IsLast => Current is not null && Queue.IndexOf(Current) == Queue.Count - 1;
    public string NextText => Loc.T(IsLast ? "SetupFinish" : "SetupNext");

    /// <summary>向导关掉时设为 true，窗口据此关闭。</summary>
    [ObservableProperty] private bool _closeRequested;

    internal void SelectionChanged()
    {
        var n = Browsers.Count(b => b.IsChecked);
        CanStart = n > 0;
        StartText = Loc.F("SetupStart", n);
    }

    /// <summary>每秒刷新各浏览器的连接状态；当前浏览器连上后自动进入下一个。</summary>
    public void Tick()
    {
        var conns = _connections();
        foreach (var b in Browsers) b.IsConnected = conns.Any(b.Browser.Owns);
        if (Page != SetupPage.Install || Current is null) return;
        CurrentConnected = Current.IsConnected;
        if (Current.IsConnected && _advance is null)
        {
            Current.State = StepState.Connected;
            _advance = new CancellationTokenSource();
            _ = AdvanceSoonAsync(_advance.Token);
        }
    }

    async Task AdvanceSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);
            Next();
        }
        catch (TaskCanceledException)
        {
        }
    }

    public void AddBrowser(string exe)
    {
        var existing = Browsers.FirstOrDefault(b => PathUtil.SamePath(b.Browser.ExePath, exe));
        if (existing is null)
        {
            existing = new BrowserChoice(this, BrowserInstaller.Describe(exe));
            Browsers.Add(existing);
            OnPropertyChanged(nameof(HasBrowsers));
            Tick();
        }
        existing.IsChecked = existing.Supported;
    }

    [RelayCommand]
    void Start()
    {
        Queue.Clear();
        foreach (var b in Browsers.Where(b => b.IsChecked && b.Supported))
        {
            b.State = StepState.Pending;
            Queue.Add(b);
        }
        if (Queue.Count == 0) return;
        Page = SetupPage.Install;
        _ = EnterAsync(Queue[0]);
    }

    async Task EnterAsync(BrowserChoice browser)
    {
        _advance?.Cancel();
        _advance = null;
        Current = browser;
        browser.State = StepState.Active;
        StepOfText = Loc.F("SetupStepOf", Queue.IndexOf(browser) + 1, Queue.Count);
        CurrentConnected = false;
        Tick();
        if (!browser.IsConnected) await OpenPageAsync();
    }

    [RelayCommand]
    async Task OpenPage()
    {
        if (Current is null || IsOpening) return;
        await OpenPageAsync();
    }

    async Task OpenPageAsync()
    {
        var browser = Current!;
        IsOpening = true;
        OpenFailed = false;
        OpenText = Loc.F("SetupStep1Opening", browser.Name);
        bool ok;
        try
        {
            ok = await _openPage(browser.Browser);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            ok = false;
        }
        if (Current != browser) return;
        IsOpening = false;
        OpenFailed = !ok;
        OpenText = Loc.F(ok ? "SetupStep1" : "SetupStep1Failed", browser.Browser.ExtensionsUrl, browser.Name);
        // 资源管理器打开在最上面，扩展文件夹已选中，直接拖进刚打开的扩展页
        _revealFolder(ExtensionFolder);
    }

    [RelayCommand]
    void RevealFolder() => _revealFolder(ExtensionFolder);

    [RelayCommand]
    void CopyFolderPath() => BrowserInstaller.SetClipboard(ExtensionFolder);

    [RelayCommand]
    void CopyUrl()
    {
        if (Current is not null) BrowserInstaller.SetClipboard(Current.Browser.ExtensionsUrl);
    }

    [RelayCommand]
    void Skip()
    {
        if (Current is null) return;
        if (!Current.IsConnected) Current.State = StepState.Skipped;
        Next();
    }

    [RelayCommand]
    void Next()
    {
        if (Current is null) return;
        if (Current.State == StepState.Active) Current.State = Current.IsConnected ? StepState.Connected : StepState.Skipped;
        var i = Queue.IndexOf(Current);
        if (i + 1 < Queue.Count)
        {
            _ = EnterAsync(Queue[i + 1]);
            return;
        }
        _advance?.Cancel();
        _advance = null;
        Current = null;
        var connected = Queue.Count(b => b.IsConnected);
        AllConnected = connected == Queue.Count;
        DoneTitle = Loc.T(AllConnected ? "SetupDoneTitle" : "SetupDonePartialTitle");
        DoneText = AllConnected ? Loc.T("SetupDoneAll") : Loc.F("SetupDoneSome", connected, Queue.Count);
        Page = SetupPage.Done;
    }

    [RelayCommand]
    void Back()
    {
        _advance?.Cancel();
        _advance = null;
        Current = null;
        Page = SetupPage.Choose;
    }

    [RelayCommand]
    void Close()
    {
        _advance?.Cancel();
        CloseRequested = true;
    }
}

public enum StepState { Pending, Active, Connected, Skipped }

public partial class BrowserChoice : ObservableObject
{
    readonly ExtensionSetupViewModel _owner;

    public BrowserChoice(ExtensionSetupViewModel owner, InstalledBrowser browser)
    {
        _owner = owner;
        Browser = browser;
        Icon = AppScanner.LoadIcon(browser.ExePath);
    }

    public InstalledBrowser Browser { get; }
    public ImageSource? Icon { get; }
    public string Name => Browser.Name;
    public string Path => Browser.ExePath;
    public bool Supported => Browser.Supported;
    public bool Unsupported => !Browser.Supported;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private StepState _state;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !Supported)
        {
            IsChecked = false;
            return;
        }
        _owner.SelectionChanged();
    }
}
