using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FocusYourself.Core;
using Microsoft.Win32;

namespace FocusYourself.Services;

/// <summary>本机装的一个浏览器。</summary>
/// <param name="Supported">Chromium 内核才能装这个扩展（Firefox 等不行）。</param>
public sealed record InstalledBrowser(string Name, string ExePath, bool Supported)
{
    public BrowserFamily Family { get; } = BrowserInstaller.FamilyOf(ExePath);

    /// <summary>扩展管理页的地址。Brave、Vivaldi、Helium 等都认 chrome://，只有 Edge 和 Opera 要换前缀。</summary>
    public string ExtensionsUrl => Family switch
    {
        BrowserFamily.Edge => "edge://extensions",
        BrowserFamily.Opera => "opera://extensions",
        _ => "chrome://extensions",
    };

    /// <summary>浏览器的安装目录；它的子进程（包括连上桥接的那个）都在这下面。</summary>
    public string InstallDir => Path.GetDirectoryName(ExePath) ?? ExePath;

    /// <summary>某个桥接连接是不是来自这个浏览器。</summary>
    public bool Owns(BridgeConnectionInfo c) =>
        c.OwnerPath is { } owner && PathUtil.Normalize(owner).StartsWith(PathUtil.Normalize(InstallDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

public enum BrowserFamily { Chromium, Edge, Opera, Firefox, Other }

/// <summary>
/// 帮用户在各个浏览器里装扩展：找出装了哪些浏览器，并替用户打开扩展管理页。
/// Chromium 不允许外部程序静默安装扩展，命令行也打不开 chrome:// 页面，所以这里的做法是：
/// 开一个新窗口，确认它在前台后，模拟 Ctrl+L → 粘贴地址 → 回车，剩下的“开发者模式 + 拖入文件夹”由用户完成。
/// </summary>
public static class BrowserInstaller
{
    /// <summary>注册到“默认程序”里的浏览器，加上不一定注册的 Helium，去重后按名字排序。</summary>
    public static List<InstalledBrowser> Detect()
    {
        var found = new List<InstalledBrowser>();
        void add(string? name, string? exe)
        {
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            if (PathUtil.FileName(exe) == "iexplore.exe") return;
            if (found.Any(b => PathUtil.SamePath(b.ExePath, exe))) return;
            found.Add(Describe(exe, name));
        }

        (RegistryKey hive, string path)[] roots =
        [
            (Registry.CurrentUser, @"SOFTWARE\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"),
        ];
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var browser = key.OpenSubKey(sub);
                    using var command = key.OpenSubKey(sub + @"\shell\open\command");
                    add(browser?.GetValue(null) as string, ExeFromCommand(command?.GetValue(null) as string));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        add("Helium", BrowserDetector.FindHelium());
        return found.OrderBy(b => b.Supported ? 0 : 1).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>用户手动选的 exe（便携版浏览器不会出现在注册表里）。</summary>
    public static InstalledBrowser Describe(string exe, string? name = null)
    {
        var friendly = string.IsNullOrWhiteSpace(name) ? PathUtil.FriendlyName(exe) : name.Trim();
        var family = FamilyOf(exe);
        var supported = family is BrowserFamily.Chromium or BrowserFamily.Edge or BrowserFamily.Opera || LooksLikeChromium(exe);
        return new InstalledBrowser(friendly, exe, supported);
    }

    /// <summary>"C:\x\chrome.exe" --single-argument %1 → C:\x\chrome.exe</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    public static BrowserFamily FamilyOf(string exe) => PathUtil.FileName(exe) switch
    {
        "msedge.exe" => BrowserFamily.Edge,
        "opera.exe" or "launcher.exe" when exe.Contains("opera", StringComparison.OrdinalIgnoreCase) => BrowserFamily.Opera,
        "firefox.exe" or "waterfox.exe" or "librewolf.exe" or "floorp.exe" or "zen.exe" => BrowserFamily.Firefox,
        "chrome.exe" or "brave.exe" or "vivaldi.exe" or "chromium.exe" or "thorium.exe" => BrowserFamily.Chromium,
        _ => BrowserFamily.Other,
    };

    /// <summary>
    /// 国产浏览器大多是换了名字的 Chromium（quark.exe、115chrome.exe…）。
    /// Chromium 的安装目录或版本号子目录里一定有 resources.pak 和 xxx_elf.dll。
    /// </summary>
    public static bool LooksLikeChromium(string exe)
    {
        try
        {
            var dir = Path.GetDirectoryName(exe);
            if (dir is null || !Directory.Exists(dir)) return false;
            bool marked(string d) =>
                File.Exists(Path.Combine(d, "resources.pak")) && Directory.EnumerateFiles(d, "*_elf.dll").Any();
            return marked(dir) || Directory.EnumerateDirectories(dir).Take(20).Any(marked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 开一个新窗口并把地址栏导航到扩展管理页。返回 false 表示没等到浏览器窗口到前台，
    /// 这时地址已经在剪贴板里，界面会提示用户自己 Ctrl+L、Ctrl+V、回车。
    /// </summary>
    public static async Task<bool> OpenExtensionsPageAsync(InstalledBrowser browser, CancellationToken ct = default)
    {
        SetClipboard(browser.ExtensionsUrl);
        // 浏览器已经在运行时，真正开窗口的是那个老进程，得先允许它抢前台
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            Process.Start(new ProcessStartInfo(browser.ExePath, "--new-window") { UseShellExecute = false, WorkingDirectory = browser.InstallDir })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        var hwnd = IntPtr.Zero;
        for (var waited = 0; waited < 10_000 && hwnd == IntPtr.Zero; waited += 150)
        {
            await Task.Delay(150, ct);
            hwnd = ForegroundBrowserWindow(browser);
        }
        if (hwnd == IntPtr.Zero) return false;
        // 新标签页刚出来时地址栏可能还没准备好，稍等一下再确认它仍在前台
        await Task.Delay(700, ct);
        if (GetForegroundWindow() != hwnd) return false;

        SendKeys([(VK_CONTROL, false), (VK_L, false), (VK_L, true), (VK_CONTROL, true)]);
        await Task.Delay(120, ct);
        if (GetForegroundWindow() != hwnd) return false;
        SendKeys([(VK_CONTROL, false), (VK_V, false), (VK_V, true), (VK_CONTROL, true)]);
        await Task.Delay(120, ct);
        if (GetForegroundWindow() != hwnd) return false;
        SendKeys([(VK_RETURN, false), (VK_RETURN, true)]);
        return true;
    }

    /// <summary>在资源管理器里打开扩展所在的目录并选中它，方便直接拖进扩展页。</summary>
    public static void RevealFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{folder.TrimEnd('\\')}\"") { UseShellExecute = true })?.Dispose();
    }

    public static void SetClipboard(string text)
    {
        // 剪贴板可能正被别的程序占用，重试几次
        for (var i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>前台窗口如果是这个浏览器的主窗口就返回它的句柄。</summary>
    static IntPtr ForegroundBrowserWindow(InstalledBrowser browser)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return IntPtr.Zero;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var path = Native.GetProcessPath((int)pid);
        return path is not null && browser.Owns(new BridgeConnectionInfo("", path, DateTime.UtcNow)) ? hwnd : IntPtr.Zero;
    }

    // ================= Win32 =================

    const int ASFW_ANY = -1;
    const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_L = 0x4C, VK_V = 0x56;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    static void SendKeys((ushort vk, bool up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = k.vk, dwFlags = k.up ? KEYEVENTF_KEYUP : 0 } },
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // 联合体要包含最大的 MOUSEINPUT，结构体大小才和系统要求的一致
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
}
