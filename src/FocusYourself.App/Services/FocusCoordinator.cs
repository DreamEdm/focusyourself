using System.Diagnostics;
using System.IO;
using FocusYourself.Core;

namespace FocusYourself.Services;

public enum ToastKind { Focus, Break, Info, Warning, Paused }

/// <summary>协调器需要的界面能力，由 App 实现（负责切回 UI 线程）。</summary>
public interface IFocusUi
{
    void ShowToast(ToastKind kind, string title, string message);
    void ShowBlocked(string appName, string appPath);
    /// <summary>进入了受限文件夹：询问是否继续访问，用户确认后调用 <paramref name="allow"/>。</summary>
    void ShowFolderBlocked(string folderName, string openedPath, int continueDelaySeconds, Action allow);
    void PlayChime(Chime chime);
}

/// <summary>
/// 订阅番茄钟状态，统一开关所有拦截：进程拦截、IFEO、浏览器扩展、浏览器守护。
/// 除 <see cref="ProcessBlocker.AppBlocked"/> 回调外，所有方法都在 UI 线程上调用。
/// </summary>
public sealed class FocusCoordinator : IDisposable
{
    static readonly TimeSpan GuardSilenceLimit = TimeSpan.FromSeconds(60);
    static readonly TimeSpan GuardKillDelay = TimeSpan.FromSeconds(15);
    /// <summary>暂停后每隔这么久提醒一次，免得忘了继续。</summary>
    public static readonly TimeSpan PauseReminderInterval = TimeSpan.FromMinutes(5);

    readonly ConfigStore _store;
    readonly IFocusUi _ui;
    readonly IfeoBlocker _ifeo;
    readonly Dictionary<string, DateTime> _browserFirstSeen = [];
    readonly Dictionary<string, DateTime> _browserWarned = [];
    DateTime _lastGuardCheck = DateTime.MinValue;
    bool _strongWarned;
    /// <summary>本次暂停已经提醒过几次（第 n 次提醒在暂停满 n × 间隔时发出）。</summary>
    int _pauseReminders;

    public FocusCoordinator(ConfigStore store, AppConfig config, IFocusUi ui, IfeoBlocker? ifeo = null)
    {
        _store = store;
        _ui = ui;
        Config = config;
        _ifeo = ifeo ?? new IfeoBlocker();
        Engine = new PomodoroEngine(config.Pomodoro);
        Engine.PhaseChanged += OnPhaseChanged;
        Engine.PauseChanged += OnPauseChanged;
        Blocker.AppBlocked += OnAppBlocked;
        Folders.FolderBlocked += OnFolderBlocked;
    }

    public AppConfig Config { get; }
    public PomodoroEngine Engine { get; }
    public ProcessBlocker Blocker { get; } = new();
    public BrowserBridge Bridge { get; } = new();
    public FolderGuard Folders { get; } = new();
    public IfeoBlocker Ifeo => _ifeo;

    /// <summary>扫描到的程序，用于检查强力模式的同名冲突。</summary>
    public IReadOnlyList<(string Name, string Path)> KnownApps { get; set; } = [];

    public bool StrictLocked => Config.StrictMode && Engine.IsFocus;

    /// <summary>状态或拦截内容变化时触发，供界面刷新。</summary>
    public event EventHandler? StateChanged;

    public void Initialize(FocusState restored)
    {
        // 启动自检：先清掉上次崩溃可能留下的 IFEO 拦截，再按恢复的状态重新应用
        if (AdminHelper.IsAdmin) _ifeo.Clear();

        Bridge.ExtensionId = Config.ExtensionId;
        Bridge.Start(Config.BridgePort);

        Engine.Restore(restored);
        Apply(enteringFocus: Engine.IsFocus, resumed: true);
    }

    public void SaveConfig()
    {
        _store.SaveConfig(Config);
        Engine.Settings = Config.Pomodoro;
    }

    /// <summary>规则（程序/网站）被修改后调用：保存并立即生效。</summary>
    public void RulesChanged()
    {
        SaveConfig();
        if (Engine.IsFocus)
        {
            Blocker.UpdateRules(Config.Apps, TimeSpan.FromSeconds(Config.CloseGraceSeconds));
            Folders.UpdateRules(Config.Folders);
            ApplyIfeo();
        }
        Bridge.Broadcast(CurrentBridgeState());
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestartBridge()
    {
        Bridge.ExtensionId = Config.ExtensionId;
        Bridge.Start(Config.BridgePort);
        Bridge.Broadcast(CurrentBridgeState());
    }

    /// <summary>扩展连接变化时在 UI 线程调用：记录“扩展已安装”，从此启用浏览器守护。</summary>
    public void NoteBridgeActivity()
    {
        if (Config.ExtensionSeen || Bridge.Connections.Count == 0) return;
        Config.ExtensionSeen = true;
        SaveConfig();
    }

    /// <summary>每秒由 UI 计时器调用。</summary>
    public void Tick()
    {
        Engine.Tick();
        RemindIfPaused();
        if (Engine.IsFocus && DateTime.UtcNow - _lastGuardCheck > TimeSpan.FromSeconds(5))
        {
            _lastGuardCheck = DateTime.UtcNow;
            GuardBrowsers();
        }
    }

    /// <summary>
    /// 需要写 IFEO 的 exe 路径。目录型规则（整个游戏）展开成目录里文件名唯一的 exe；
    /// 同名冲突、太通用的文件名（launcher.exe 等）依旧只用标准模式拦截。
    /// </summary>
    public List<string> StrongPaths()
    {
        var known = KnownAppsWithBrowsers().ToList();
        var result = new List<string>();
        foreach (var rule in Config.Apps.Where(r => r.Enabled && r.Mode == BlockMode.Strong))
        {
            if (rule.Kind == AppRuleKind.Exe)
            {
                if (RuleSafety.CanUseStrong(rule.Path, known, out _)) result.Add(rule.Path);
                continue;
            }
            if (!RuleSafety.CanBlockFolder(rule.Path, out _)) continue;
            // 同一目录里重名的 exe（如多个 launcher.exe）按文件名无法区分，跳过
            var unique = FolderExes(rule.Path).GroupBy(PathUtil.FileName).Where(g => g.Count() == 1).Select(g => g.First());
            result.AddRange(unique.Where(p => RuleSafety.CanUseStrong(p, known, out _)));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<string> FolderExes(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public IEnumerable<(string Name, string Path)> KnownAppsWithBrowsers() =>
        KnownApps.Concat(Config.GuardedBrowsers.Select(p => (PathUtil.FriendlyName(p), p)));

    public BridgeState CurrentBridgeState()
    {
        var state = Engine.Phase switch
        {
            Phase.Focus => "focus",
            Phase.ShortBreak or Phase.LongBreak => "break",
            _ => "idle",
        };
        var domains = Engine.IsFocus
            ? Config.Sites.Where(s => s.Enabled).Select(s => DomainUtil.Normalize(s.Domain)).OfType<string>().Distinct().ToList()
            : [];
        return new BridgeState(state, domains, UnixMs(Engine.EndsAtUtc), UnixMs(Engine.PhaseStartedUtc), Engine.CompletedFocusRounds)
        {
            PausedAt = UnixMs(Engine.PausedAtUtc),
            Lang = Loc.Instance.Language,
            Theme = Views.ThemeManager.Current,
        };
    }

    static long? UnixMs(DateTime? utc) => utc is { } t ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds() : null;

    /// <summary>语言或主题切换后同步给扩展。</summary>
    public void BroadcastState() => Bridge.Broadcast(CurrentBridgeState());

    void OnPhaseChanged(object? sender, PhaseChangedEventArgs e)
    {
        _store.SaveState(Engine.Snapshot());
        Apply(enteringFocus: e.NewPhase == Phase.Focus && e.OldPhase != Phase.Focus, resumed: false);
        Announce(e);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnPauseChanged(object? sender, EventArgs e)
    {
        _pauseReminders = 0;
        _store.SaveState(Engine.Snapshot());
        // 暂停只冻结计时，拦截保持不变；扩展需要知道暂停时刻来冻结它显示的倒计时
        Bridge.Broadcast(CurrentBridgeState());
        _ui.PlayChime(Engine.IsPaused ? Chime.Pause : Chime.Resume);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>暂停期间每满 <see cref="PauseReminderInterval"/> 发一次通知。电脑休眠醒来后只补发一次。</summary>
    void RemindIfPaused()
    {
        if (!Engine.IsPaused) return;
        var due = (int)(Engine.PausedFor.Ticks / PauseReminderInterval.Ticks);
        if (due <= _pauseReminders) return;
        _pauseReminders = due;
        _ui.PlayChime(Chime.PauseReminder);
        _ui.ShowToast(ToastKind.Paused, Loc.F("ToastPausedTitle", (int)Engine.PausedFor.TotalMinutes),
            Loc.F(Engine.IsFocus ? "ToastPausedBodyFocus" : "ToastPausedBody", Views.PhaseVisuals.Format(Engine.Remaining)));
    }

    void Apply(bool enteringFocus, bool resumed)
    {
        if (Engine.IsFocus)
        {
            var grace = TimeSpan.FromSeconds(resumed ? 0 : Config.CloseGraceSeconds);
            if (enteringFocus && !resumed)
            {
                var running = ProcessBlocker.FindRunning(Config.Apps.Where(r => RuleSafety.CanBlockRule(r, out _)));
                if (running.Count > 0)
                {
                    var names = string.Join(Loc.T("ListSep"), running.Select(r => r.Name).Distinct().Take(4));
                    _ui.ShowToast(ToastKind.Warning, Loc.F("ToastClosingSoon", Config.CloseGraceSeconds, names), Loc.T("ToastClosingSoonBody"));
                }
            }
            if (enteringFocus) _strongWarned = false;
            Blocker.Activate(Config.Apps, grace);
            Folders.Activate(Config.Folders);
            ApplyIfeo();
            if (enteringFocus)
            {
                _browserFirstSeen.Clear();
                _browserWarned.Clear();
            }
        }
        else
        {
            Blocker.Deactivate();
            Folders.Deactivate();
            if (AdminHelper.IsAdmin) _ifeo.Clear();
        }
        Bridge.Broadcast(CurrentBridgeState());
    }

    void ApplyIfeo()
    {
        var strong = StrongPaths();
        if (strong.Count == 0)
        {
            if (AdminHelper.IsAdmin) _ifeo.Clear();
            return;
        }
        if (!AdminHelper.IsAdmin)
        {
            if (!_strongWarned)
            {
                _strongWarned = true;
                _ui.ShowToast(ToastKind.Warning, Loc.T("ToastStrongInactive"), Loc.T("ToastStrongInactiveBody"));
            }
            return;
        }
        // 先清理，保证被取消勾选的程序也同时解除
        _ifeo.Clear();
        var failed = _ifeo.Apply(strong);
        if (failed.Count > 0)
            _ui.ShowToast(ToastKind.Warning, Loc.T("ToastStrongPartial"), Loc.F("ToastStrongPartialBody", string.Join(Loc.T("ListSep"), failed)));
    }

    void Announce(PhaseChangedEventArgs e)
    {
        var p = Config.Pomodoro;
        var appCount = Config.Apps.Count(a => a.Enabled);
        var siteCount = Config.Sites.Count(s => s.Enabled);
        var folderCount = Config.Folders.Count(f => f.Enabled);
        var body = folderCount > 0
            ? Loc.F("ToastFocusBodyFolders", p.FocusMinutes, appCount, siteCount, folderCount)
            : Loc.F("ToastFocusBody", p.FocusMinutes, appCount, siteCount);
        switch (e.NewPhase)
        {
            case Phase.Focus:
                _ui.PlayChime(Chime.FocusStart);
                _ui.ShowToast(ToastKind.Focus, Loc.F("ToastFocusTitle", Engine.CompletedFocusRounds + 1), body);
                break;
            case Phase.ShortBreak:
                _ui.PlayChime(Chime.BreakStart);
                _ui.ShowToast(ToastKind.Break, Loc.T(e.NaturalEnd ? "ToastFocusDone" : "ToastBreakStart"),
                    Loc.F("ToastShortBreakBody", p.ShortBreakMinutes));
                break;
            case Phase.LongBreak:
                _ui.PlayChime(Chime.BreakStart);
                _ui.ShowToast(ToastKind.Break, Loc.F("ToastLongBreakTitle", Engine.CompletedFocusRounds),
                    Loc.F("ToastLongBreakBody", p.LongBreakMinutes));
                break;
            case Phase.Idle:
                _ui.PlayChime(Chime.Stop);
                _ui.ShowToast(ToastKind.Info, Loc.T(e.NaturalEnd ? "ToastBreakOver" : "ToastStopped"),
                    Loc.T(e.NaturalEnd ? "ToastBreakOverBody" : "ToastStoppedBody"));
                break;
        }
    }

    void OnAppBlocked(object? sender, AppBlockedEventArgs e)
    {
        if (e.Failed)
        {
            _ui.ShowToast(ToastKind.Warning, Loc.F("ToastCloseFailed", e.Name), Loc.T("ToastCloseFailedBody"));
            return;
        }
        _ui.PlayChime(Chime.Blocked);
        _ui.ShowBlocked(e.Name, e.Path);
    }

    void OnFolderBlocked(object? sender, FolderBlockedEventArgs e)
    {
        _ui.PlayChime(Chime.Blocked);
        // 严格模式下“继续访问”要多等一会儿
        _ui.ShowFolderBlocked(e.Name, e.OpenedPath, Config.StrictMode ? 15 : 3, e.Allow);
    }

    /// <summary>专注期内，受保护的浏览器在运行却长时间没有扩展心跳 → 先警告，再关闭。</summary>
    void GuardBrowsers()
    {
        if (!Config.GuardBrowsers || !Config.ExtensionSeen) return;
        var now = DateTime.UtcNow;
        var focusStart = Engine.PhaseStartedUtc ?? now;

        foreach (var browser in Config.GuardedBrowsers)
        {
            var key = PathUtil.Normalize(browser);
            var running = ProcessBlocker.FindRunning([new AppRule { Name = "browser", Path = browser }]);
            if (running.Count == 0)
            {
                _browserFirstSeen.Remove(key);
                _browserWarned.Remove(key);
                continue;
            }
            _browserFirstSeen.TryAdd(key, now);

            var reference = new[] { focusStart, _browserFirstSeen[key], Bridge.LastSeenFor(browser) ?? DateTime.MinValue }.Max();
            if (now - reference < GuardSilenceLimit)
            {
                _browserWarned.Remove(key);
                continue;
            }

            var name = PathUtil.FriendlyName(browser);
            if (!_browserWarned.TryGetValue(key, out var warnedAt))
            {
                _browserWarned[key] = now;
                _ui.ShowToast(ToastKind.Warning, Loc.F("ToastGuardWarn", name),
                    Loc.F("ToastGuardWarnBody", (int)GuardKillDelay.TotalSeconds, name));
            }
            else if (now - warnedAt >= GuardKillDelay)
            {
                foreach (var m in running)
                {
                    try { Process.GetProcessById(m.Pid).Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                _browserWarned.Remove(key);
                _browserFirstSeen.Remove(key);
                _ui.ShowToast(ToastKind.Warning, Loc.F("ToastGuardKilled", name), Loc.T("ToastGuardKilledBody"));
            }
        }
    }

    /// <summary>退出程序前调用。<paramref name="endFocus"/> 为 true 时通知扩展解除屏蔽。</summary>
    public void Shutdown(bool endFocus)
    {
        Blocker.Deactivate();
        Folders.Dispose();
        if (AdminHelper.IsAdmin) _ifeo.Clear();
        // 等扩展收到解除消息再断开，最多等 1 秒
        if (endFocus)
            Bridge.BroadcastAsync(BridgeState.Idle with { Lang = Loc.Instance.Language, Theme = Views.ThemeManager.Current })
                .Wait(TimeSpan.FromSeconds(1));
        Bridge.Stop();
    }

    public void Dispose()
    {
        Blocker.Dispose();
        Folders.Dispose();
        Bridge.Dispose();
    }
}
