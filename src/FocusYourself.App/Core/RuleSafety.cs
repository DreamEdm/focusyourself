namespace FocusYourself.Core;

/// <summary>判断一个程序能否被拦截、能否使用强力模式。</summary>
public static class RuleSafety
{
    /// <summary>系统关键程序：任何模式都不允许拦截。</summary>
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "csrss.exe", "winlogon.exe", "lsass.exe", "services.exe",
        "smss.exe", "wininit.exe", "dwm.exe", "conhost.exe", "sihost.exe", "ctfmon.exe",
        "fontdrvhost.exe", "runtimebroker.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "applicationframehost.exe", "dllhost.exe", "rundll32.exe",
        "taskhostw.exe", "audiodg.exe", "spoolsv.exe", "msmpeng.exe", "securityhealthsystray.exe",
        "logonui.exe", "userinit.exe", "textinputhost.exe", "lockapp.exe",
    };

    /// <summary>可以用标准模式拦截，但不能做 IFEO 的程序（拦了可能导致无法修复系统）。</summary>
    static readonly HashSet<string> NoStrong = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "regedit.exe", "mmc.exe", "taskmgr.exe",
        "msiexec.exe", "systemsettings.exe", "windowsterminal.exe", "wt.exe",
    };

    /// <summary>太通用的文件名：IFEO 按文件名生效，容易误伤其它程序。</summary>
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher.exe", "app.exe", "game.exe", "start.exe", "main.exe", "client.exe",
        "update.exe", "setup.exe", "run.exe", "player.exe", "chrome.exe", "electron.exe",
        "python.exe", "pythonw.exe", "java.exe", "javaw.exe", "node.exe",
    };

    public static string SelfFileName { get; set; } =
        System.IO.Path.GetFileName(Environment.ProcessPath ?? "FocusYourself.exe");

    public static bool CanBlock(string path, out string reason)
    {
        var name = PathUtil.FileName(path);
        if (Critical.Contains(name))
        {
            reason = Loc.T("SafetyCritical");
            return false;
        }
        if (string.Equals(name, SelfFileName, StringComparison.OrdinalIgnoreCase))
        {
            reason = Loc.T("SafetySelf");
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>目录型规则（整个游戏目录）：不允许系统目录、Program Files 根目录、盘符根目录，也不能包含本程序。</summary>
    public static bool CanBlockFolder(string folder, out string reason)
    {
        reason = Loc.T("SafetyFolderTooBroad");
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var normalized = Services.FolderGuard.NormalizeFolder(folder);
        if (normalized.Length <= 3) return false;
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];
        foreach (var b in broad.Where(b => !string.IsNullOrEmpty(b)))
        {
            // 目录本身就是（或包含）这些系统位置
            if (Services.FolderGuard.IsInside(b, normalized)) return false;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && Services.FolderGuard.IsInside(normalized, windows)) return false;
        if (Environment.ProcessPath is { } self && Services.FolderGuard.IsInside(self, normalized))
        {
            reason = Loc.T("SafetySelf");
            return false;
        }
        reason = "";
        return true;
    }

    public static bool CanBlockRule(AppRule rule, out string reason) =>
        rule.Kind == AppRuleKind.Folder ? CanBlockFolder(rule.Path, out reason) : CanBlock(rule.Path, out reason);

    /// <param name="knownPaths">已知的其它程序路径（扫描结果、受保护浏览器等），用于检查同名冲突。</param>
    public static bool CanUseStrong(string path, IEnumerable<(string Name, string Path)> knownPaths, out string reason)
    {
        if (!CanBlock(path, out reason)) return false;

        var name = PathUtil.FileName(path);
        if (NoStrong.Contains(name))
        {
            reason = Loc.T("SafetyNoStrong");
            return false;
        }
        if (GenericNames.Contains(name))
        {
            reason = Loc.F("SafetyGeneric", name);
            return false;
        }

        var self = PathUtil.Normalize(path);
        foreach (var (otherName, otherPath) in knownPaths)
        {
            if (PathUtil.FileName(otherPath) == name && PathUtil.Normalize(otherPath) != self)
            {
                reason = Loc.F("SafetySameName", otherName, name);
                return false;
            }
        }

        reason = "";
        return true;
    }
}
