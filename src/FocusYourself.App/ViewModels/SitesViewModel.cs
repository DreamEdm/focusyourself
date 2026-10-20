using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.ViewModels;

public partial class SitesViewModel : ObservableObject
{
    readonly FocusCoordinator _coord;
    readonly IAppShell _shell;

    public SitesViewModel(FocusCoordinator coord, IAppShell shell)
    {
        _coord = coord;
        _shell = shell;
        foreach (var s in coord.Config.Sites) Items.Add(new SiteItemViewModel(this, s));
    }

    public ObservableCollection<SiteItemViewModel> Items { get; } = [];
    public ObservableCollection<string> Connections { get; } = [];

    public string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    public string ExtensionId => _coord.Config.ExtensionId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newDomain = "";

    [ObservableProperty] private string _error = "";

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        var domain = DomainUtil.Normalize(NewDomain);
        if (domain is null)
        {
            Error = Loc.T("SitesBadDomain");
            return;
        }
        Error = "";
        NewDomain = "";
        var existing = Items.FirstOrDefault(i => i.Domain == domain);
        if (existing is not null)
        {
            existing.IsEnabled = true;
            return;
        }
        var rule = new SiteRule { Domain = domain };
        _coord.Config.Sites.Add(rule);
        Items.Add(new SiteItemViewModel(this, rule));
        _coord.RulesChanged();
    }

    bool CanAdd() => !string.IsNullOrWhiteSpace(NewDomain);

    /// <summary>智能拦截批量加入；调用方负责最后统一 RulesChanged。</summary>
    public void AddMany(IEnumerable<string> domains)
    {
        foreach (var raw in domains)
        {
            if (DomainUtil.Normalize(raw) is not { } domain) continue;
            var existing = Items.FirstOrDefault(i => i.Domain == domain);
            if (existing is not null)
            {
                existing.Rule.Enabled = true;
                existing.SyncFromRule();
                continue;
            }
            var rule = new SiteRule { Domain = domain };
            _coord.Config.Sites.Add(rule);
            Items.Add(new SiteItemViewModel(this, rule));
        }
    }

    [RelayCommand]
    void Remove(SiteItemViewModel item)
    {
        if (!AllowLoosen()) return;
        _coord.Config.Sites.Remove(item.Rule);
        Items.Remove(item);
        _coord.RulesChanged();
    }

    [RelayCommand]
    void OpenExtensionFolder()
    {
        if (Directory.Exists(ExtensionFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    void CopyExtensionsUrl()
    {
        Clipboard.SetText("chrome://extensions");
        _shell.ShowToast(ToastKind.Info, Loc.T("Copied"), Loc.T("CopiedExtensionsUrl"));
    }

    [RelayCommand]
    void CopyFolderPath()
    {
        Clipboard.SetText(ExtensionFolder);
        _shell.ShowToast(ToastKind.Info, Loc.T("CopiedFolderPath"), Loc.T("CopiedFolderPathBody"));
    }

    public void RefreshConnections(IReadOnlyList<BridgeConnectionInfo> conns)
    {
        var lines = conns.Select(c => c.OwnerPath is null ? c.Browser : $"{PathUtil.FriendlyName(c.OwnerPath)} · {c.OwnerPath}").ToList();
        if (lines.SequenceEqual(Connections)) return;
        Connections.Clear();
        foreach (var l in lines) Connections.Add(l);
    }

    internal bool AllowLoosen()
    {
        if (!_coord.StrictLocked) return true;
        _shell.ShowToast(ToastKind.Warning, Loc.T("StrictTitle"), Loc.T("StrictNoUnblockSites"));
        return false;
    }

    internal void Changed() => _coord.RulesChanged();
}

public partial class SiteItemViewModel : ObservableObject
{
    readonly SitesViewModel _owner;

    public SiteItemViewModel(SitesViewModel owner, SiteRule rule)
    {
        _owner = owner;
        Rule = rule;
        _isEnabled = rule.Enabled;
    }

    public SiteRule Rule { get; }
    public string Domain => Rule.Domain;
    public string Initial => Domain.Length > 0 ? Domain[..1].ToUpperInvariant() : "?";

    [ObservableProperty] private bool _isEnabled;

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
