using System.IO;
using System.Security;
using FocusYourself.Core;
using Microsoft.Win32;

namespace FocusYourself.Services;

/// <summary>
/// 强力模式：在 IFEO 注册表里给目标 exe 设置 Debugger，让 Windows 启动它时改为启动
/// "FocusYourself.exe --blocked 原程序路径"，从而弹出拦截窗口而不是打开程序。
/// 只清理带有本程序标记的键，绝不改动别的软件写的 IFEO 配置。
/// </summary>
public sealed class IfeoBlocker
{
    public const string BlockedArg = "--blocked";
    const string RootPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    const string OwnedValue = "FocusYourselfOwned";
    /// <summary>1 = 子键原本就存在，只改了 Debugger；2 = 子键是本程序新建的，清理时整个删除。</summary>
    const int OwnedExistingKey = 1;
    const int OwnedCreatedKey = 2;

    readonly string _selfPath;
    readonly RegistryHive _hive;
    readonly string _root;

    /// <param name="rootOverride">测试用：改写到 HKCU 下的临时路径。</param>
    public IfeoBlocker(string? selfPath = null, RegistryHive hive = RegistryHive.LocalMachine, string? rootOverride = null)
    {
        _selfPath = selfPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
        _hive = hive;
        _root = rootOverride ?? RootPath;
    }

    // 64 位和 32 位两个视图都写，32 位程序启动别的程序时读取的是 WOW6432Node
    static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    public string DebuggerCommand => $"\"{_selfPath}\" {BlockedArg}";

    /// <summary>为这些 exe 写入拦截，返回失败（无权限或已被其他软件占用）的文件名。</summary>
    public List<string> Apply(IEnumerable<string> exePaths)
    {
        var failed = new List<string>();
        foreach (var name in exePaths.Select(PathUtil.FileName).Distinct())
        {
            try
            {
                foreach (var view in Views)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(_hive, view);
                    using var root = baseKey.CreateSubKey(_root, writable: true);
                    bool existed;
                    using (var probe = root.OpenSubKey(name)) existed = probe is not null;
                    using var key = root.CreateSubKey(name, writable: true);

                    var owned = key.GetValue(OwnedValue);
                    if (owned is null && key.GetValue("Debugger") is not null)
                    {
                        // 别的软件已经设置了 Debugger，不覆盖
                        failed.Add(name);
                        break;
                    }
                    key.SetValue("Debugger", DebuggerCommand, RegistryValueKind.String);
                    if (owned is null)
                        key.SetValue(OwnedValue, existed ? OwnedExistingKey : OwnedCreatedKey, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                failed.Add(name);
            }
        }
        return failed;
    }

    /// <summary>删除所有本程序写入的拦截。需要管理员权限；返回是否全部清理成功。</summary>
    public bool Clear()
    {
        bool ok = true;
        foreach (var view in Views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(_hive, view);
                using var root = baseKey.OpenSubKey(_root, writable: true);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    int? owned;
                    using (var key = root.OpenSubKey(name, writable: true))
                    {
                        owned = key?.GetValue(OwnedValue) as int?;
                        if (key is null || owned is null) continue;
                        if (owned == OwnedExistingKey)
                        {
                            key.DeleteValue("Debugger", throwOnMissingValue: false);
                            key.DeleteValue(OwnedValue, throwOnMissingValue: false);
                        }
                    }
                    if (owned == OwnedCreatedKey)
                        root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>是否有残留拦截（普通权限也能读）。</summary>
    public List<string> FindResidue()
    {
        var result = new List<string>();
        foreach (var view in Views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(_hive, view);
                using var root = baseKey.OpenSubKey(_root);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue(OwnedValue) is not null && !result.Contains(name)) result.Add(name);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        }
        return result;
    }
}
