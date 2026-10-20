using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.ViewModels;

public partial class FoldersViewModel : ObservableObject
{
    readonly FocusCoordinator _coord;
    readonly IAppShell _shell;

    public FoldersViewModel(FocusCoordinator coord, IAppShell shell)
    {
        _coord = coord;
        _shell = shell;
        foreach (var f in coord.Config.Folders) Items.Add(new FolderItemViewModel(this, f));
    }

    public ObservableCollection<FolderItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _error = "";

    [RelayCommand]
    void Add()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("FoldersDialogTitle"),
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        var rejected = new List<string>();
        foreach (var path in dlg.FolderNames)
        {
            if (!FolderGuard.CanGuard(path, out var reason))
            {
                rejected.Add(Loc.F("FolderRejected", path, reason));
                continue;
            }
            AddOrEnable(path);
        }
        Error = string.Join("\n", rejected);
        _coord.RulesChanged();
    }

    /// <summary>智能拦截批量加入；调用方负责最后统一 RulesChanged。</summary>
    public void AddMany(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => FolderGuard.CanGuard(p, out _))) AddOrEnable(path);
    }

    void AddOrEnable(string path)
    {
        var key = FolderGuard.NormalizeFolder(path);
        var existing = Items.FirstOrDefault(i => FolderGuard.NormalizeFolder(i.Path) == key);
        if (existing is not null)
        {
            existing.Rule.Enabled = true;
            existing.SyncFromRule();
            return;
        }
        var rule = new FolderRule { Path = path };
        _coord.Config.Folders.Add(rule);
        Items.Add(new FolderItemViewModel(this, rule));
    }

    [RelayCommand]
    void Remove(FolderItemViewModel item)
    {
        if (!AllowLoosen()) return;
        _coord.Config.Folders.Remove(item.Rule);
        Items.Remove(item);
        _coord.RulesChanged();
    }

    /// <summary>每秒刷新“本轮已允许”标记。</summary>
    public void Refresh()
    {
        var focusing = _coord.Engine.IsFocus;
        foreach (var item in Items)
            item.AllowedThisRound = focusing && item.IsEnabled && _coord.Folders.IsAllowed(FolderGuard.NormalizeFolder(item.Path));
    }

    internal bool AllowLoosen()
    {
        if (!_coord.StrictLocked) return true;
        _shell.ShowToast(ToastKind.Warning, Loc.T("StrictTitle"), Loc.T("StrictNoUnblockFolders"));
        return false;
    }

    internal void Changed() => _coord.RulesChanged();
}

public partial class FolderItemViewModel : ObservableObject
{
    readonly FoldersViewModel _owner;

    public FolderItemViewModel(FoldersViewModel owner, FolderRule rule)
    {
        _owner = owner;
        Rule = rule;
        _isEnabled = rule.Enabled;
    }

    public FolderRule Rule { get; }
    public string Path => Rule.Path;
    public string Name => FolderGuard.DisplayName(Rule.Path);
    public bool Exists => System.IO.Directory.Exists(Rule.Path);

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _allowedThisRound;

    /// <summary>规则在外部被改了（智能拦截），同步开关但不再触发保存。</summary>
    public void SyncFromRule() => IsEnabled = Rule.Enabled;

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (newValue == Rule.Enabled) return;
        if (!newValue && !_owner.AllowLoosen())
        {
            Application.Current.Dispatcher.BeginInvoke(() => IsEnabled = true);
            return;
        }
        Rule.Enabled = newValue;
        _owner.Changed();
    }
}
