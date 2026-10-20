using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusYourself.Core;
using Microsoft.Win32;

namespace FocusYourself.Services;

public enum AppSource { StartMenu, Installed, AppPaths, Steam, Epic, Running, Manual, Rule }

public enum AppCategory { App, Game, System }

public sealed class ScannedApp
{
    public required string Name { get; init; }
    /// <summary>exe 路径；<see cref="Kind"/> 为 Folder 时是游戏安装目录。</summary>
    public required string Path { get; init; }
    public required AppSource Source { get; init; }
    public AppRuleKind Kind { get; init; } = AppRuleKind.Exe;
    public AppCategory Category { get; init; } = AppCategory.App;
    /// <summary>用来提取图标的 exe（目录型条目取主程序）。</summary>
    public string? IconPath { get; init; }
    /// <summary>目录型条目里包含的 exe 数量。</summary>
    public int ExeCount { get; init; }
    public ImageSource? Icon { get; set; }
}

public sealed class ScanResult
{
    public List<ScannedApp> Apps { get; init; } = [];
    /// <summary>Steam 游戏库目录（含 Steam 自带库），用于智能拦截的文件夹提醒。</summary>
    public List<string> SteamLibraries { get; init; } = [];
    public string? SteamInstallPath { get; init; }
    /// <summary>Epic 游戏的公共父目录（如 C:\Program Files\Epic Games）。</summary>
    public List<string> EpicRoots { get; init; } = [];
}

/// <summary>
/// 汇总“用户能启动的程序”：开始菜单、注册表卸载信息、App Paths、Steam/Epic 游戏、正在运行的窗口程序。
/// 游戏按安装目录合并成一条，避免同一个游戏的启动器、下载器、内嵌浏览器等各占一行。
/// </summary>
public static partial class AppScanner
{
    static readonly string[] NoiseWords =
    [
        "unins", "uninst", "setup", "install", "update", "crash", "report", "vc_redist", "vcredist",
        "dxsetup", "dotnet", "helper", "elevate", "repair", "redist", "bugreport", "diagnos",
    ];

    public static List<ScannedApp> Scan(bool withIcons = true) => ScanAll(withIcons).Apps;

    public static ScanResult ScanAll(bool withIcons = true)
    {
        var found = new Dictionary<string, ScannedApp>();
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 按优先级加入：先加入的名字更友好，后面的来源只补充新路径
        void Add(string? path, string? name, AppSource source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsNoise(path) || IsInstallerCache(path)) return;
            if (!File.Exists(path)) return;
            var key = PathUtil.Normalize(path);
            if (found.ContainsKey(key)) return;
            found[key] = new ScannedApp
            {
                Name = string.IsNullOrWhiteSpace(name) ? PathUtil.FriendlyName(path) : name.Trim(),
                Path = path,
                Source = source,
                IconPath = path,
                Category = path.StartsWith(windowsDir + "\\", StringComparison.OrdinalIgnoreCase) ? AppCategory.System : AppCategory.App,
            };
        }

        Try(() => ScanStartMenu(Add));
        Try(() => ScanUninstall(Add));
        Try(() => ScanAppPaths(Add));
        Try(() => ScanRunning(Add));

        var games = new List<ScannedApp>();
        var steamLibraries = new List<string>();
        string? steamPath = null;
        var epicRoots = new List<string>();
        Try(() => steamPath = ScanSteam(games, steamLibraries));
        Try(() => ScanEpic(games, epicRoots));

        var list = MergeGameFolders(found.Values, games)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (withIcons)
            foreach (var app in list) app.Icon = LoadIcon(app.IconPath ?? app.Path);

        return new ScanResult { Apps = list, SteamLibraries = steamLibraries, SteamInstallPath = steamPath, EpicRoots = epicRoots };
    }

    /// <summary>去掉落在某个游戏目录里的单个 exe 条目（它们由游戏目录那一条统一拦截）。</summary>
    public static List<ScannedApp> MergeGameFolders(IEnumerable<ScannedApp> exes, IEnumerable<ScannedApp> gameFolders)
    {
        var folders = gameFolders
            .GroupBy(g => FolderGuard.NormalizeFolder(g.Path))
            .Select(g => g.First())
            .ToList();
        var result = exes.Where(e => !folders.Any(f => FolderGuard.IsInside(e.Path, f.Path))).ToList();
        result.AddRange(folders);
        return result;
    }

    public static bool IsNoise(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return NoiseWords.Any(name.Contains);
    }

    /// <summary>Windows Installer 缓存里的“exe”其实是图标文件，不是程序。</summary>
    public static bool IsInstallerCache(string path) =>
        path.Contains(@"\Microsoft\Installer\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase);

    static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or COMException or JsonException) { }
    }

    static void ScanStartMenu(Action<string?, string?, AppSource> add)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ];
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in SafeEnumerate(root, "*.lnk"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                if (IsNoise(name + ".exe")) continue;
                add(ResolveShortcut(lnk), name, AppSource.StartMenu);
            }
        }
    }

    static IEnumerable<string> SafeEnumerate(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 6,
        });

    static void ScanUninstall(Action<string?, string?, AppSource> add)
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(sub);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null || key.GetValue("SystemComponent") is 1) continue;
                add(PathUtil.ParseIconPath(key.GetValue("DisplayIcon") as string), key.GetValue("DisplayName") as string, AppSource.Installed);
            }
        }
    }

    static void ScanAppPaths(Action<string?, string?, AppSource> add)
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var root = hive.OpenSubKey(sub);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                add(key?.GetValue(null) as string, null, AppSource.AppPaths);
            }
        }
    }

    /// <summary>每个 Steam 游戏一条（目录型）。返回 Steam 安装目录。</summary>
    static string? ScanSteam(List<ScannedApp> games, List<string> libraries)
    {
        using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (steamKey?.GetValue("SteamPath") is not string steamPath) return null;
        // 注册表里的 SteamPath 是全小写的，换成磁盘上的真实大小写，界面上更好看
        steamPath = RealCase(System.IO.Path.GetFullPath(steamPath.Replace('/', '\\')));

        var libs = new List<string> { steamPath };
        var vdf = System.IO.Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            libs.AddRange(VdfPathRegex().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));

        foreach (var lib in libs.Select(l => l.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = System.IO.Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            libraries.Add(lib);
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var game = VdfNameRegex().Match(text).Groups[1].Value;
                var dir = VdfInstallDirRegex().Match(text).Groups[1].Value;
                if (string.IsNullOrEmpty(dir)) continue;
                // Steamworks 运行库之类不是游戏
                if (game.Contains("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)) continue;
                var gameDir = System.IO.Path.Combine(apps, "common", dir);
                if (GameFolder(gameDir, string.IsNullOrEmpty(game) ? dir : game, AppSource.Steam) is { } entry) games.Add(entry);
            }
        }
        return steamPath;
    }

    /// <summary>Epic 已安装游戏：读取启动器写在 ProgramData 里的 .item 清单。</summary>
    static void ScanEpic(List<ScannedApp> games, List<string> roots)
    {
        var manifests = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) return;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(item));
            var root = doc.RootElement;
            var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
            var location = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
            if (string.IsNullOrEmpty(location)) continue;
            if (GameFolder(location, name ?? System.IO.Path.GetFileName(location), AppSource.Epic) is not { } entry) continue;
            games.Add(entry);
            var parent = System.IO.Path.GetDirectoryName(location.TrimEnd('\\'));
            if (parent is not null && System.IO.Path.GetFileName(parent).Equals("Epic Games", StringComparison.OrdinalIgnoreCase) &&
                !roots.Contains(parent, StringComparer.OrdinalIgnoreCase))
                roots.Add(parent);
        }
    }

    static string RealCase(string path)
    {
        try
        {
            var info = new DirectoryInfo(path.TrimEnd('\\'));
            if (info.Parent is null) return info.FullName.ToUpperInvariant();
            var parent = RealCase(info.Parent.FullName);
            var match = Directory.EnumerateDirectories(parent, info.Name).FirstOrDefault();
            return match ?? System.IO.Path.Combine(parent, info.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    static ScannedApp? GameFolder(string dir, string name, AppSource source)
    {
        if (!Directory.Exists(dir)) return null;
        var exes = Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
        }).ToList();
        if (exes.Count == 0) return null;
        // 图标用最大的非辅助程序 exe，一般就是游戏本体
        var main = exes.Where(p => !IsNoise(p)).DefaultIfEmpty(exes[0])
            .MaxBy(p => { try { return new FileInfo(p).Length; } catch (IOException) { return 0; } });
        return new ScannedApp
        {
            Name = name,
            Path = dir.TrimEnd('\\'),
            Source = source,
            Kind = AppRuleKind.Folder,
            Category = AppCategory.Game,
            IconPath = main,
            ExeCount = exes.Count,
        };
    }

    static void ScanRunning(Action<string?, string?, AppSource> add)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var path = Native.GetProcessPath(p.Id);
                if (path is null || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    continue;
                add(path, null, AppSource.Running);
            }
        }
    }

    public static ImageSource? LoadIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException) { return null; }
    }

    // ---- .lnk 解析（IShellLinkW） ----

    static string? ResolveShortcut(string lnkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfNameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfInstallDirRegex();
}
