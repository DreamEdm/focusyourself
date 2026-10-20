using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.Views;

/// <summary>智能拦截确认窗：说明 + 分类清单（可取消勾选），确认按钮倒计时后才能点。</summary>
public partial class SmartBlockWindow : Window
{
    public const int ConfirmDelaySeconds = 3;

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _countdown = ConfirmDelaySeconds;

    public SmartBlockWindow(IReadOnlyList<SmartSuggestion> suggestions)
    {
        InitializeComponent();
        Groups = SmartGroup.Build(suggestions, Changed);
        GroupList.ItemsSource = Groups;
        _timer.Tick += (_, _) =>
        {
            _countdown--;
            if (_countdown <= 0) _timer.Stop();
            UpdateButton();
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Changed();
    }

    public IReadOnlyList<SmartGroup> Groups { get; }

    public IReadOnlyList<SmartSuggestion> Selected =>
        Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Suggestion).ToList();

    /// <summary>测试用：跳过倒计时。</summary>
    public void SkipCountdown()
    {
        _countdown = 0;
        _timer.Stop();
        UpdateButton();
    }

    void Changed()
    {
        var count = Groups.Sum(g => g.Items.Count(i => i.IsChecked));
        CountText.Text = Loc.F("SmartSelectedCount", count);
        UpdateButton();
    }

    void UpdateButton()
    {
        var count = Groups.Sum(g => g.Items.Count(i => i.IsChecked));
        ConfirmButton.IsEnabled = _countdown <= 0 && count > 0;
        ConfirmButton.Content = _countdown > 0 ? Loc.F("SmartConfirmCountdown", _countdown) : Loc.T("SmartConfirm");
    }

    void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_countdown > 0) return;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public partial class SmartGroup : ObservableObject
{
    readonly Action _changed;
    bool _syncing;

    SmartGroup(SmartCategory category, IEnumerable<SmartSuggestion> items, Action changed)
    {
        _changed = changed;
        Category = category;
        foreach (var s in items) Items.Add(new SmartItem(s, OnItemChanged));
        _allChecked = true;
    }

    public SmartCategory Category { get; }
    public ObservableCollection<SmartItem> Items { get; } = [];

    public string Title => Loc.T(Category switch
    {
        SmartCategory.Platforms => "SmartCatPlatforms",
        SmartCategory.Games => "SmartCatGames",
        SmartCategory.Entertainment => "SmartCatEntertainment",
        SmartCategory.Sites => "SmartCatSites",
        _ => "SmartCatFolders",
    });

    public string Icon => Category switch
    {
        SmartCategory.Platforms => "\uE71D", // 平台/启动器
        SmartCategory.Games => "\uE7FC",
        SmartCategory.Entertainment => "\uE714", // 视频
        SmartCategory.Sites => "\uE774",         // 地球
        _ => "\uE8B7",                           // 文件夹
    };

    public string CountText => $"{Items.Count(i => i.IsChecked)} / {Items.Count}";

    /// <summary>全选框：部分勾选时显示为“—”。</summary>
    [ObservableProperty] private bool? _allChecked;

    partial void OnAllCheckedChanged(bool? value)
    {
        if (_syncing || value is null) return;
        _syncing = true;
        foreach (var i in Items) i.IsChecked = value.Value;
        _syncing = false;
        OnPropertyChanged(nameof(CountText));
        _changed();
    }

    void OnItemChanged()
    {
        if (!_syncing)
        {
            _syncing = true;
            var on = Items.Count(i => i.IsChecked);
            AllChecked = on == Items.Count ? true : on == 0 ? false : null;
            _syncing = false;
        }
        OnPropertyChanged(nameof(CountText));
        _changed();
    }

    public static List<SmartGroup> Build(IEnumerable<SmartSuggestion> suggestions, Action changed) =>
        suggestions.GroupBy(s => s.Category)
            .OrderBy(g => g.Key)
            .Select(g => new SmartGroup(g.Key, g, changed))
            .ToList();
}

public partial class SmartItem : ObservableObject
{
    readonly Action _changed;

    public SmartItem(SmartSuggestion suggestion, Action changed)
    {
        Suggestion = suggestion;
        _changed = changed;
    }

    public SmartSuggestion Suggestion { get; }
    public string Name => Suggestion.Name;
    public string? Detail => Suggestion.Category == SmartCategory.Sites
        ? Loc.F("SmartSiteDetail", Suggestion.Target)
        : Suggestion.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty] private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _changed();
}
