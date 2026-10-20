using System.Diagnostics;
using System.Management;
using FocusYourself.Core;

namespace FocusYourself.Services;

public sealed record RunningMatch(int Pid, string Path, string Name);

public sealed class AppBlockedEventArgs(string name, string path, bool failed) : EventArgs
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    /// <summary>关闭失败（通常是目标以管理员身份运行而本程序没有）。</summary>
    public bool Failed { get; } = failed;
}

/// <summary>
/// 标准模式：专注期内监视进程，命中规则就先 WM_CLOSE、2 秒后强制结束。
/// 始终 500ms 轮询；有管理员权限时额外订阅 WMI 进程启动事件，反应更快。
/// </summary>
public sealed class ProcessBlocker : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    readonly object _lock = new();
    readonly Dictionary<int, DateTime> _graceUntil = [];
    readonly HashSet<int> _closing = [];
    readonly Dictionary<int, string?> _pathCache = [];
    readonly HashSet<string> _failedPaths = [];
    readonly int _selfPid = Environment.ProcessId;

    RuleMatcher _rules = RuleMatcher.Empty;
    Timer? _timer;
    ManagementEventWatcher? _watcher;
    bool _active;

    public event EventHandler<AppBlockedEventArgs>? AppBlocked;

    public bool IsActive { get { lock (_lock) return _active; } }

    /// <summary>开始拦截。当前已在运行的命中程序会在 <paramref name="grace"/> 之后才被关闭。</summary>
    public void Activate(IEnumerable<AppRule> rules, TimeSpan grace)
    {
        lock (_lock)
        {
            _active = true;
            _failedPaths.Clear();
            SetRulesLocked(rules, grace);
        }
        _timer ??= new Timer(_ => Scan(), null, PollInterval, PollInterval);
        StartWatcher();
    }

    /// <summary>专注期内修改了规则：新命中的已运行程序同样给出宽限时间。</summary>
    public void UpdateRules(IEnumerable<AppRule> rules, TimeSpan grace)
    {
        lock (_lock)
        {
            if (!_active) return;
            SetRulesLocked(rules, grace);
        }
    }

    public void Deactivate()
    {
        lock (_lock)
        {
            _active = false;
            _graceUntil.Clear();
            _pathCache.Clear();
        }
        _timer?.Dispose();
        _timer = null;
        StopWatcher();
    }

    /// <summary>列出当前正在运行、且命中给定规则的进程。</summary>
    public static List<RunningMatch> FindRunning(IEnumerable<AppRule> rules) =>
        FindRunning(new RuleMatcher(rules.Where(r => r.Enabled)), Environment.ProcessId);

    static List<RunningMatch> FindRunning(RuleMatcher matcher, int selfPid)
    {
        var result = new List<RunningMatch>();
        if (matcher.IsEmpty) return result;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == selfPid || !matcher.NeedsPath(p.ProcessName)) continue;
                var path = Native.GetProcessPath(p.Id);
                if (path is not null && matcher.Match(path) is { } rule)
                    result.Add(new RunningMatch(p.Id, path, rule.Name));
            }
        }
        return result;
    }

    void SetRulesLocked(IEnumerable<AppRule> rules, TimeSpan grace)
    {
        _rules = new RuleMatcher(rules.Where(r => r.Enabled && RuleSafety.CanBlockRule(r, out _)));
        _pathCache.Clear();

        var deadline = DateTime.UtcNow + grace;
        foreach (var m in FindRunning(_rules, _selfPid))
            _graceUntil.TryAdd(m.Pid, deadline);
    }

    void Scan()
    {
        RuleMatcher rules;
        lock (_lock)
        {
            if (!_active) return;
            rules = _rules;
        }
        if (rules.IsEmpty) return;

        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                alive.Add(p.Id);
                if (rules.NeedsPath(p.ProcessName))
                    Check(p.Id, rules);
            }
        }

        lock (_lock)
        {
            foreach (var pid in _pathCache.Keys.Where(pid => !alive.Contains(pid)).ToList()) _pathCache.Remove(pid);
            foreach (var pid in _graceUntil.Keys.Where(pid => !alive.Contains(pid)).ToList()) _graceUntil.Remove(pid);
        }
    }

    void Check(int pid, RuleMatcher rules)
    {
        if (pid == _selfPid) return;

        string? path;
        lock (_lock)
        {
            if (!_pathCache.TryGetValue(pid, out path))
                _pathCache[pid] = path = Native.GetProcessPath(pid);
        }
        if (path is null || rules.Match(path) is not { } rule) return;

        lock (_lock)
        {
            if (_graceUntil.TryGetValue(pid, out var until) && DateTime.UtcNow < until) return;
            if (!_closing.Add(pid)) return;
        }
        _ = CloseAsync(pid, path, rule.Name);
    }

    async Task CloseAsync(int pid, string path, string name)
    {
        bool failed = false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (Native.CloseProcessWindows(pid) > 0)
            {
                try { await proc.WaitForExitAsync().WaitAsync(CloseTimeout); }
                catch (TimeoutException) { }
            }
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // 进程已经退出
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failed = true;
        }
        finally
        {
            lock (_lock) _closing.Remove(pid);
        }

        // 同一个程序关闭失败只提示一次，避免每 500ms 弹一次
        if (failed)
        {
            lock (_lock)
            {
                if (!_failedPaths.Add(PathUtil.Normalize(path))) return;
            }
        }
        AppBlocked?.Invoke(this, new AppBlockedEventArgs(name, path, failed));
    }

    void StartWatcher()
    {
        if (_watcher is not null || !AdminHelper.IsAdmin) return;
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT ProcessID, ProcessName FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += (_, e) =>
            {
                RuleMatcher rules;
                lock (_lock)
                {
                    if (!_active) return;
                    rules = _rules;
                }
                var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                var name = PathUtil.ProcessName(e.NewEvent["ProcessName"] as string ?? "");
                if (rules.NeedsPath(name)) Check(pid, rules);
            };
            _watcher.Start();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // WMI 不可用时退回纯轮询
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    void StopWatcher()
    {
        if (_watcher is null) return;
        try { _watcher.Stop(); } catch (ManagementException) { }
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose() => Deactivate();
}

/// <summary>
/// 把规则编译成便于匹配的形式：exe 规则按完整路径精确匹配（先用进程名预筛），
/// 目录规则匹配目录里的任意 exe（整个游戏目录一行拦）。
/// </summary>
sealed class RuleMatcher
{
    public static readonly RuleMatcher Empty = new([]);

    readonly Dictionary<string, AppRule> _exact;
    readonly HashSet<string> _names;
    readonly List<(string Root, AppRule Rule)> _folders;

    public RuleMatcher(IEnumerable<AppRule> rules)
    {
        var list = rules.ToList();
        _exact = list.Where(r => r.Kind == AppRuleKind.Exe)
            .GroupBy(r => PathUtil.Normalize(r.Path))
            .ToDictionary(g => g.Key, g => g.First());
        _names = _exact.Keys.Select(PathUtil.ProcessName).ToHashSet();
        _folders = list.Where(r => r.Kind == AppRuleKind.Folder && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => (FolderGuard.NormalizeFolder(r.Path), r))
            .ToList();
    }

    public bool IsEmpty => _exact.Count == 0 && _folders.Count == 0;

    /// <summary>这个进程名是否值得查询完整路径（有目录规则时任何进程都可能命中）。</summary>
    public bool NeedsPath(string processName) =>
        _folders.Count > 0 || _names.Contains(processName.ToLowerInvariant());

    public AppRule? Match(string path)
    {
        if (_exact.TryGetValue(PathUtil.Normalize(path), out var rule)) return rule;
        if (_folders.Count == 0) return null;
        // 目录里的系统关键程序或本程序依旧放行
        if (!RuleSafety.CanBlock(path, out _)) return null;
        foreach (var (root, folderRule) in _folders)
            if (FolderGuard.IsInside(path, root)) return folderRule;
        return null;
    }
}
