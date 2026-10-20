using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace FocusYourself.Services;

public static class AdminHelper
{
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>以管理员身份重新启动本程序。用户在 UAC 里点“否”时返回 false。</summary>
    public static bool RestartAsAdmin(string extraArgs = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--restarted {extraArgs}".Trim())
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// 开机自启。有管理员权限时用“以最高权限运行”的计划任务（强力模式开机即可用，且不弹 UAC），
/// 否则写 HKCU\...\Run。
/// </summary>
public static class AutoStart
{
    const string TaskName = "FocusYourself";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutoStartArg = "--autostart";

    public static string Command => $"\"{Environment.ProcessPath}\" {AutoStartArg}";

    public static bool Set(bool enabled)
    {
        Remove();
        if (!enabled) return true;

        if (AdminHelper.IsAdmin)
        {
            var (code, _) = RunSchtasks("/Create", "/TN", TaskName, "/TR", Command, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
            if (code == 0) return true;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(TaskName, Command);
        return true;
    }

    static void Remove()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        if (AdminHelper.IsAdmin) RunSchtasks("/Delete", "/TN", TaskName, "/F");
    }

    static (int Code, string Output) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }
}

public static class BrowserDetector
{
    /// <summary>查找 Helium 的安装位置（它的主程序名就叫 chrome.exe）。</summary>
    public static string? FindHelium()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"imput\Helium\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"imput\Helium\Application\chrome.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
