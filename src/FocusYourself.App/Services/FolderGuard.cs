using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FocusYourself.Core;
using Microsoft.CSharp.RuntimeBinder;

namespace FocusYourself.Services;

public sealed class FolderBlockedEventArgs(string name, string rootPath, string openedPath, Action allow) : EventArgs
{
    /// <summary>被限制的文件夹名称。</summary>
    public string Name { get; } = name;
    /// <summary>规则里设置的文件夹。</summary>
    public string RootPath { get; } = rootPath;
    /// <summary>用户实际打开的位置（可能是子文件夹）。</summary>
    public string OpenedPath { get; } = openedPath;
    /// <summary>用户选择“继续访问”：本轮专注内放行，并把资源管理器带回刚才的位置。</summary>
    public Action Allow { get; } = allow;
}

/// <summary>
/// 专注期间监视资源管理器窗口（含标签页）：一旦进入受限文件夹，就退回到它的上一级并发出事件，
/// 由界面询问“是否继续访问”。只影响资源管理器，不修改任何文件或权限。
/// Shell COM 对象只能在 STA 线程上使用，所以所有 COM 调用都在一个专用线程里完成。
/// </summary>
public sealed class FolderGuard : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan PromptCooldown = TimeSpan.FromSeconds(3);

    readonly object _lock = new();
    readonly HashSet<string> _allowed = [];
    readonly Dictionary<string, DateTime> _lastPrompt = [];
    readonly ConcurrentQueue<Action<dynamic>> _work = new();
    readonly AutoResetEvent _wake = new(false);
    List<(string Root, string Name)> _rules = [];
    Thread? _thread;
    volatile bool _active;
    volatile bool _disposed;

    public event EventHandler<FolderBlockedEventArgs>? FolderBlocked;

    public bool IsActive => _active;

    /// <summary>本轮专注中已选择“继续访问”的文件夹（规范化路径）。</summary>
    public IReadOnlyCollection<string> AllowedRoots
    {
        get { lock (_lock) return _allowed.ToList(); }
    }

    public void Activate(IEnumerable<FolderRule> rules)
    {
        SetRules(rules);
        _active = true;
        EnsureThread();
        _wake.Set();
    }

    public void UpdateRules(IEnumerable<FolderRule> rules)
    {
        SetRules(rules);
        _wake.Set();
    }

    /// <summary>离开专注：停止监视，并清空“本轮放行”。</summary>
    public void Deactivate()
    {
        _active = false;
        lock (_lock)
        {
            _allowed.Clear();
            _lastPrompt.Clear();
        }
    }

    public bool IsAllowed(string root)
    {
        lock (_lock) return _allowed.Contains(PathUtil.Normalize(root));
    }

    void SetRules(IEnumerable<FolderRule> rules)
    {
        var list = rules.Where(r => r.Enabled && CanGuard(r.Path, out _))
            .Select(r => (Root: NormalizeFolder(r.Path), Name: DisplayName(r.Path)))
            .DistinctBy(r => r.Root)
            // 先匹配更深的规则，这样嵌套规则会提示最具体的那个文件夹
            .OrderByDescending(r => r.Root.Length)
            .ToList();
        lock (_lock) _rules = list;
    }

    // ---------- 路径工具 ----------

    public static string NormalizeFolder(string path) => PathUtil.Normalize(path).TrimEnd('\\');

    /// <summary><paramref name="path"/> 是否就是 <paramref name="root"/> 或在它里面。</summary>
    public static bool IsInside(string path, string root)
    {
        var p = NormalizeFolder(path);
        var r = NormalizeFolder(root);
        return p == r || p.StartsWith(r + "\\", StringComparison.Ordinal);
    }

    public static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public static bool CanGuard(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = Loc.T("FolderEmptyPath");
            return false;
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = Loc.T("FolderBadPath");
            return false;
        }
        if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
        {
            reason = Loc.T("FolderWholeDrive");
            return false;
        }
        reason = "";
        return true;
    }

    // ---------- 监视线程 ----------

    void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FolderGuard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Loop()
    {
        dynamic? shell = null;
        while (!_disposed)
        {
            _wake.WaitOne(PollInterval);
            if (_disposed) break;
            try
            {
                shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                while (_work.TryDequeue(out var job)) job(shell!);
                if (_active) Scan((object)shell!);
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or InvalidComObjectException)
            {
                // Explorer 重启等情况会让 COM 对象失效，下一轮重新创建
                if (shell is not null) Marshal.FinalReleaseComObject(shell);
                shell = null;
            }
        }
        if (shell is not null) Marshal.FinalReleaseComObject(shell);
    }

    void Scan(object shell)
    {
        List<(string Root, string Name)> rules;
        lock (_lock) rules = _rules;
        if (rules.Count == 0) return;

        foreach (var (window, hwnd, path) in ExplorerWindows(shell))
        {
            try
            {
                if (path is null) continue;
                var index = rules.FindIndex(r => IsInside(path, r.Root));
                if (index < 0) continue;
                var match = rules[index];

                bool prompt;
                lock (_lock)
                {
                    if (_allowed.Contains(match.Root)) continue;
                    prompt = !_lastPrompt.TryGetValue(match.Root, out var last) || DateTime.UtcNow - last > PromptCooldown;
                    if (prompt) _lastPrompt[match.Root] = DateTime.UtcNow;
                }

                var parent = Path.GetDirectoryName(match.Root) ?? match.Root;
                window.Navigate2(parent);

                if (prompt)
                {
                    var root = match.Root;
                    var opened = path;
                    FolderBlocked?.Invoke(this, new FolderBlockedEventArgs(match.Name, root, opened, () => Allow(root, hwnd, parent, opened)));
                }
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException) { }
            finally
            {
                Marshal.FinalReleaseComObject(window);
            }
        }
    }

    /// <summary>枚举资源管理器窗口/标签页及其当前文件夹路径（特殊位置如“此电脑”返回 null）。</summary>
    static List<(dynamic Window, long Hwnd, string? Path)> ExplorerWindows(object shellObject)
    {
        dynamic shell = shellObject;
        var result = new List<(dynamic, long, string?)>();
        dynamic windows = shell.Windows();
        try
        {
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? w = null;
                try
                {
                    w = windows.Item(i);
                    if (w is null) continue;
                    string fullName = w.FullName ?? "";
                    if (!Path.GetFileName(fullName).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Marshal.FinalReleaseComObject(w);
                        continue;
                    }
                    string? path = null;
                    try
                    {
                        path = (string?)w.Document.Folder.Self.Path;
                        if (path is not null && (path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathRooted(path)))
                            path = null;
                    }
                    catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException) { }
                    result.Add((w, (long)w.HWND, path));
                }
                catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException)
                {
                    if (w is not null) Marshal.FinalReleaseComObject(w);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(windows);
        }
        return result;
    }

    void Allow(string root, long hwnd, string parent, string opened)
    {
        lock (_lock) _allowed.Add(root);
        // 回到监视线程：找到刚才被退回上一级的那个窗口/标签页，导航回原位置
        _work.Enqueue(shell =>
        {
            bool reopened = false;
            foreach (var (window, h, path) in ExplorerWindows((object)shell))
            {
                try
                {
                    if (!reopened && h == hwnd && path is not null && NormalizeFolder(path) == NormalizeFolder(parent))
                    {
                        window.Navigate2(opened);
                        reopened = true;
                    }
                }
                catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
                finally
                {
                    Marshal.FinalReleaseComObject(window);
                }
            }
            if (!reopened && Directory.Exists(opened))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{opened}\"") { UseShellExecute = true });
        });
        _wake.Set();
    }

    public void Dispose()
    {
        _active = false;
        _disposed = true;
        _wake.Set();
    }
}
