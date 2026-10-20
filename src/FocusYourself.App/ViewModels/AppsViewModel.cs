using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.ViewModels;

public enum AppFilter { All, Apps, Games, Blocked, System }

public partial class AppsViewModel : ObservableObject
{
    readonly FocusCoordinator _coord;
    readonly IAppShell _shell;
    Task? _scanTask;

    public AppsViewModel(FocusCoordinator coord, IAppShell shell)
    {
        _coord = coord;
        _shell = shell;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is AppItemViewModel a && Matches(a);
        View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.SortKey), ListSortDirection.Ascending));
        // 先把已有规则显示出来，扫描完成后再补全
        foreach (var rule in coord.Config.Apps)
            Items.Add(new AppItemViewModel(this, rule.Name, rule.Path, AppSource.Rule, rule.Kind,
                rule.Kind == AppRuleKind.Folder ? AppCategory.Game : AppCategory.App, 0, null));
        RefreshStrongAvailability();
    }

    public ObservableCollection<AppItemViewModel> Items { get; } = [];
    public ICollectionView View { get; }

    /// <summary>最近一次扫描结果（智能拦截使用）。</summary>
    public ScanResult? LastScan { get; private set; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAll), nameof(FilterApps), nameof(FilterGames), nameof(FilterBlocked), nameof(FilterSystem))]
    private AppFilter _filter = AppFilter.All;

    public bool FilterAll { get => Filter == AppFilter.All; set { if (value) Filter = AppFilter.All; } }
    public bool FilterApps { get => Filter == AppFilter.Apps; set { if (value) Filter = AppFilter.Apps; } }
    public bool FilterGames { get => Filter == AppFilter.Games; set { if (value) Filter = AppFilter.Games; } }
    public bool FilterBlocked { get => Filter == AppFilter.Blocked; set { if (value) Filter = AppFilter.Blocked; } }
    public bool FilterSystem { get => Filter == AppFilter.System; set { if (value) Filter = AppFilter.System; } }

    public int BlockedCount => Items.Count(i => i.IsBlocked);
    public int CountAll => Items.Count(i => i.Category != AppCategory.System);
    public int CountApps => Items.Count(i => i.Category == AppCategory.App);
    public int CountGames => Items.Count(i => i.Category == AppCategory.Game);
    public int CountSystem => Items.Count(i => i.Category == AppCategory.System);

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnFilterChanged(AppFilter value) => View.Refresh();

    bool Matches(AppItemViewModel a)
    {
        var inFilter = Filter switch
        {
            AppFilter.Apps => a.Category == AppCategory.App,
            AppFilter.Games => a.Category == AppCategory.Game,
            AppFilter.Blocked => a.IsBlocked,
            AppFilter.System => a.Category == AppCategory.System,
            // “全部”不含系统工具，除非已经被拦截
            _ => a.Category != AppCategory.System || a.IsBlocked,
        };
        if (!inFilter) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        return a.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
               a.Path.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    void RaiseCounts()
    {
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountApps));
        OnPropertyChanged(nameof(CountGames));
        OnPropertyChanged(nameof(CountSystem));
    }

    /// <summary>确保至少扫描过一次（智能拦截前调用）。</summary>
    public Task EnsureScannedAsync() => LastScan is not null ? Task.CompletedTask : (_scanTask ?? ScanAsync());

    [RelayCommand]
    public Task ScanAsync()
    {
        if (IsScanning && _scanTask is not null) return _scanTask;
        _scanTask = ScanCoreAsync();
        return _scanTask;
    }

    async Task ScanCoreAsync()
    {
        IsScanning = true;
        ScanStatus = Loc.T("Scanning");
        try
        {
            var scan = await Task.Run(() => AppScanner.ScanAll());
            LastScan = scan;
            MigrateExeRulesIntoGames(scan.Apps.Where(a => a.Kind == AppRuleKind.Folder).ToList());

            var byPath = Items.GroupBy(i => FolderGuard.NormalizeFolder(i.Path)).ToDictionary(g => g.Key, g => g.First());
            var folderRules = _coord.Config.Apps.Where(r => r.Kind == AppRuleKind.Folder).Select(r => r.Path).ToList();
            foreach (var app in scan.Apps)
            {
                // 已经整体拦截的目录（智能拦截认出的游戏等）里的单个 exe 不再单独列出
                if (app.Kind == AppRuleKind.Exe && RuleFor(app.Path) is null && folderRules.Any(f => FolderGuard.IsInside(app.Path, f)))
                    continue;
                if (byPath.TryGetValue(FolderGuard.NormalizeFolder(app.Path), out var existing))
                    existing.UpdateFromScan(app);
                else
                    Items.Add(new AppItemViewModel(this, app.Name, app.Path, app.Source, app.Kind, app.Category, app.ExeCount, app.Icon));
            }
            // 已有规则但扫描没找到的，补上图标
            foreach (var item in Items.Where(i => i.Icon is null && i.Kind == AppRuleKind.Exe).ToList())
                item.Icon = await Task.Run(() => AppScanner.LoadIcon(item.Path));

            _coord.KnownApps = scan.Apps.Where(a => a.Kind == AppRuleKind.Exe).Select(a => (a.Name, a.Path)).ToList();
            RefreshStrongAvailability();
            RaiseCounts();
            View.Refresh();
            ScanStatus = Loc.F("ScanFound", Items.Count);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 旧版本按 exe 一个个拦游戏。现在一个游戏一行：把落在游戏目录里的 exe 规则合并成目录规则。
    /// </summary>
    void MigrateExeRulesIntoGames(IReadOnlyList<ScannedApp> games)
    {
        var changed = false;
        foreach (var game in games)
        {
            var inside = _coord.Config.Apps
                .Where(r => r.Kind == AppRuleKind.Exe && FolderGuard.IsInside(r.Path, game.Path))
                .ToList();
            if (inside.Count == 0) continue;

            var folderRule = RuleFor(game.Path);
            if (folderRule is null)
            {
                folderRule = new AppRule { Name = game.Name, Path = game.Path, Kind = AppRuleKind.Folder, Enabled = false };
                _coord.Config.Apps.Add(folderRule);
            }
            folderRule.Enabled |= inside.Any(r => r.Enabled);
            if (inside.Any(r => r.Enabled && r.Mode == BlockMode.Strong)) folderRule.Mode = BlockMode.Strong;
            foreach (var r in inside)
            {
                _coord.Config.Apps.Remove(r);
                var item = Items.FirstOrDefault(i => PathUtil.SamePath(i.Path, r.Path));
                if (item is not null) Items.Remove(item);
            }
            changed = true;
        }
        if (changed) _coord.RulesChanged();
    }

    [RelayCommand]
    void AddManual()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("PickProgramTitle"),
            Filter = Loc.T("ExeFilter"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog() != true) return;

        var key = PathUtil.Normalize(dlg.FileName);
        var item = Items.FirstOrDefault(i => PathUtil.Normalize(i.Path) == key);
        if (item is null)
        {
            item = new AppItemViewModel(this, PathUtil.FriendlyName(dlg.FileName), dlg.FileName, AppSource.Manual, AppRuleKind.Exe,
                AppCategory.App, 0, AppScanner.LoadIcon(dlg.FileName));
            Items.Add(item);
            RefreshStrongAvailability();
            RaiseCounts();
        }
        item.IsBlocked = true;
        Search = "";
    }

    /// <summary>智能拦截：批量开启拦截（标准模式）。调用方负责最后统一 RulesChanged。</summary>
    public void BlockMany(IEnumerable<SmartSuggestion> apps)
    {
        foreach (var s in apps)
        {
            var rule = RuleFor(s.Target);
            if (rule is null)
            {
                rule = new AppRule { Name = s.Name, Path = s.Target, Kind = s.Kind };
                _coord.Config.Apps.Add(rule);
            }
            rule.Enabled = true;
            if (s.Kind == AppRuleKind.Folder)
            {
                foreach (var inner in Items.Where(i => i.Kind == AppRuleKind.Exe && RuleFor(i.Path) is null && FolderGuard.IsInside(i.Path, s.Target)).ToList())
                    Items.Remove(inner);
            }

            var item = Items.FirstOrDefault(i => FolderGuard.NormalizeFolder(i.Path) == FolderGuard.NormalizeFolder(s.Target));
            if (item is null)
            {
                var scanned = LastScan?.Apps.FirstOrDefault(a => PathUtil.SamePath(a.Path, s.Target));
                item = new AppItemViewModel(this, s.Name, s.Target, scanned?.Source ?? AppSource.Rule, s.Kind,
                    scanned?.Category ?? (s.Kind == AppRuleKind.Folder ? AppCategory.Game : AppCategory.App),
                    scanned?.ExeCount ?? 0, scanned?.Icon);
                Items.Add(item);
            }
            else
            {
                item.SyncFromRule(rule);
            }
        }
        RefreshStrongAvailability();
        RaiseCounts();
        View.Refresh();
    }

    void RefreshStrongAvailability()
    {
        var known = _coord.KnownAppsWithBrowsers().ToList();
        foreach (var item in Items) item.UpdateAvailability(known);
    }

    /// <summary>切换语言后刷新代码里生成的文字。</summary>
    public void RefreshTexts()
    {
        foreach (var item in Items) item.RefreshTexts();
        RefreshStrongAvailability();
        if (!IsScanning && LastScan is not null) ScanStatus = Loc.F("ScanFound", Items.Count);
    }

    /// <summary>勾选变化：写回配置。返回 false 表示被严格模式拒绝。</summary>
    internal bool OnItemChanged(AppItemViewModel item, bool unblocking)
    {
        if (unblocking && _coord.StrictLocked)
        {
            _shell.ShowToast(ToastKind.Warning, Loc.T("StrictTitle"), Loc.T("StrictNoUnblockApps"));
            return false;
        }

        var rule = RuleFor(item.Path);
        if (rule is null)
        {
            rule = new AppRule { Name = item.Name, Path = item.Path, Kind = item.Kind };
            _coord.Config.Apps.Add(rule);
        }
        rule.Enabled = item.IsBlocked;
        rule.Mode = item.IsStrong ? BlockMode.Strong : BlockMode.Standard;
        _coord.RulesChanged();
        RaiseCounts();
        item.RefreshSortKey();
        return true;
    }

    internal AppRule? RuleFor(string path)
    {
        var key = FolderGuard.NormalizeFolder(path);
        return _coord.Config.Apps.FirstOrDefault(r => FolderGuard.NormalizeFolder(r.Path) == key);
    }
}

public partial class AppItemViewModel : ObservableObject
{
    readonly AppsViewModel _owner;
    bool _loading = true;

    public AppItemViewModel(AppsViewModel owner, string name, string path, AppSource source, AppRuleKind kind,
        AppCategory category, int exeCount, ImageSource? icon)
    {
        _owner = owner;
        Name = name;
        Path = path;
        Kind = kind;
        _source = source;
        _category = category;
        _exeCount = exeCount;
        _icon = icon;
        var rule = owner.RuleFor(path);
        IsBlocked = rule?.Enabled == true;
        IsStrong = rule?.Mode == BlockMode.Strong;
        _loading = false;
        RefreshTexts();
        RefreshSortKey();
    }

    public string Name { get; }
    public string Path { get; }
    public AppRuleKind Kind { get; }
    public bool IsFolder => Kind == AppRuleKind.Folder;

    [ObservableProperty] private AppSource _source;
    [ObservableProperty] private AppCategory _category;
    [ObservableProperty] private int _exeCount;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isBlocked;
    [ObservableProperty] private bool _isStrong;
    [ObservableProperty] private bool _canStrong;
    [ObservableProperty] private bool _canBlock;
    [ObservableProperty] private string _blockReason = "";
    [ObservableProperty] private string _strongTip = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _sourceTip = "";
    [ObservableProperty] private string _sortKey = "";

    /// <summary>已拦截的排在前面。</summary>
    public void RefreshSortKey() => SortKey = (IsBlocked ? "0" : "1") + Name.ToLowerInvariant();

    public void UpdateFromScan(ScannedApp app)
    {
        Icon ??= app.Icon;
        Source = app.Source;
        Category = app.Category;
        ExeCount = app.ExeCount;
        RefreshTexts();
    }

    /// <summary>规则在外部被修改（智能拦截）：同步开关，不再触发保存。</summary>
    public void SyncFromRule(AppRule rule)
    {
        _loading = true;
        IsBlocked = rule.Enabled;
        IsStrong = rule.Mode == BlockMode.Strong;
        _loading = false;
        RefreshSortKey();
    }

    public void RefreshTexts()
    {
        var rule = new AppRule { Name = Name, Path = Path, Kind = Kind };
        CanBlock = RuleSafety.CanBlockRule(rule, out var reason);
        BlockReason = reason;
        var source = Loc.T(Source switch
        {
            AppSource.StartMenu => "SourceStartMenu",
            AppSource.Installed => "SourceInstalled",
            AppSource.AppPaths => "SourceAppPaths",
            AppSource.Steam => "SourceSteam",
            AppSource.Epic => "SourceEpic",
            AppSource.Running => "SourceRunning",
            AppSource.Manual => "SourceManual",
            _ => "SourceRule",
        });
        SourceText = IsFolder && ExeCount > 0 ? Loc.F("SourceWithExes", source, ExeCount) : source;
        SourceTip = IsFolder ? Loc.T("FolderRuleTip") : "";
    }

    public void UpdateAvailability(IReadOnlyList<(string Name, string Path)> known)
    {
        bool ok;
        string reason;
        if (IsFolder)
        {
            ok = RuleSafety.CanBlockFolder(Path, out reason);
        }
        else
        {
            ok = RuleSafety.CanUseStrong(Path, known, out reason);
        }
        if (!AdminHelper.IsAdmin && ok)
        {
            ok = false;
            reason = Loc.T("StrongNeedsAdmin");
        }
        CanStrong = ok;
        StrongTip = ok ? Loc.T(IsFolder ? "StrongTipFolder" : "StrongTip") : reason;
    }

    partial void OnIsBlockedChanged(bool oldValue, bool newValue)
    {
        if (_loading) return;
        if (!_owner.OnItemChanged(this, unblocking: oldValue && !newValue))
            Revert(() => IsBlocked = oldValue);
    }

    partial void OnIsStrongChanged(bool oldValue, bool newValue)
    {
        if (_loading) return;
        // 从强力降级为标准也算“放松拦截”，严格模式下同样不允许
        if (!_owner.OnItemChanged(this, unblocking: IsBlocked && oldValue && !newValue))
            Revert(() => IsStrong = oldValue);
    }

    /// <summary>延后到绑定更新结束后再改回，确保界面上的开关同步复原。</summary>
    void Revert(Action restore) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _loading = true;
            restore();
            _loading = false;
        });
}
