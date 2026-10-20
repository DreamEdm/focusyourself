using System.Diagnostics;
using System.Text.RegularExpressions;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.Tests;

public class LocalizationTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FocusYourself.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order());

    [Fact]
    public void English_and_Chinese_have_the_same_keys_and_placeholders()
    {
        Assert.Equal(Strings.En.Keys.Order(), Strings.Zh.Keys.Order());
        foreach (var key in Strings.En.Keys)
            Assert.True(Holes(Strings.En[key]) == Holes(Strings.Zh[key]), $"placeholders differ for {key}");
        Assert.Equal(Strings.QuotesEn.Length, Strings.QuotesZh.Length);
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_exists()
    {
        var src = Path.Combine(RepoRoot(), "src", "FocusYourself.App");
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            // 文档注释里的示例不算
            var text = string.Join('\n', File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("///")));
            foreach (Match m in Regex.Matches(text, @"\{l:T (\w+)\}")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Loc\.[TF]\(""(\w+)""")) used.Add(m.Groups[1].Value);
            // Loc.T(cond ? "A" : "B") 以及 switch 里映射到键的写法
            foreach (Match m in Regex.Matches(text, @"Loc\.[TF]\([^;]*?\?\s*""(\w+)""\s*:\s*""(\w+)"""))
            {
                used.Add(m.Groups[1].Value);
                used.Add(m.Groups[2].Value);
            }
            foreach (Match m in Regex.Matches(text, @"=>\s*""((?:Phase|Source|SmartCat|Hint|Tray|Strong)\w+)""")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Item\(""(\w+)""")) used.Add(m.Groups[1].Value);
        }
        Assert.True(used.Count > 150, $"only found {used.Count} keys — the scan regex is broken");
        var missing = used.Where(k => !Strings.En.ContainsKey(k)).Order().ToList();
        Assert.True(missing.Count == 0, "missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Format_and_language_switch()
    {
        var before = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(Loc.Chinese);
            Assert.Equal("已完成 3 个番茄", Loc.F("RoundsDone", 3));
            Assert.Equal("番茄钟", Loc.Instance["NavPomodoro"]);
            Loc.Instance.SetLanguage("fr");
            Assert.Equal(Loc.English, Loc.Instance.Language);
            Assert.Equal("3 pomodoros completed", Loc.F("RoundsDone", 3));
            Assert.Equal("NoSuchKey", Loc.T("NoSuchKey"));
        }
        finally
        {
            Loc.Instance.SetLanguage(before);
        }
    }
}

public class SmartBlockTests
{
    static ScannedApp Exe(string name, string path, AppCategory cat = AppCategory.App) =>
        new() { Name = name, Path = path, Source = AppSource.StartMenu, Category = cat };

    static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-smart-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("League of Legends", true)]
    [InlineData("原神", true)]
    [InlineData("Minecraft Launcher", true)]
    [InlineData("Anecdota Notes", false)]   // 不能因为包含 "dota" 就命中
    [InlineData("Visual Studio Code", false)]
    public void Game_keywords_use_word_boundaries(string name, bool expected) =>
        Assert.Equal(expected, SmartBlock.MatchesAny(name,
            ["league of legends", "原神", "minecraft", "dota 2", "dota"]));

    [Fact]
    public void Suggests_platforms_games_video_sites_and_never_touches_browsers_chat_dev_music()
    {
        var gameDir = TempDir("Hades");
        var steamSoftware = TempDir("Wallpaper Engine");
        var scan = new ScanResult
        {
            Apps =
            [
                Exe("Steam", @"C:\Program Files (x86)\Steam\steam.exe"),
                Exe("Epic Games Launcher", @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"),
                Exe("哔哩哔哩", @"C:\Program Files\bilibili\哔哩哔哩.exe"),
                Exe("Genshin Impact", @"D:\Games\Genshin Impact\launcher.exe"),
                Exe("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
                Exe("Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe"),
                Exe("WeChat", @"C:\Program Files\Tencent\WeChat\WeChat.exe"),
                Exe("Visual Studio Code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
                Exe("Spotify", @"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe"),
                Exe("Microsoft Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"),
                Exe("Task Manager", @"C:\Windows\System32\Taskmgr.exe", AppCategory.System),
                new ScannedApp { Name = "Hades", Path = gameDir, Source = AppSource.Steam, Kind = AppRuleKind.Folder, Category = AppCategory.Game },
                new ScannedApp { Name = "Wallpaper Engine", Path = steamSoftware, Source = AppSource.Steam, Kind = AppRuleKind.Folder, Category = AppCategory.Game },
            ],
        };
        var config = new AppConfig();
        config.Sites.Clear();
        config.Sites.Add(new SiteRule { Domain = "youtube.com" }); // 已拦截的不再列出

        var s = SmartBlock.Suggest(scan, config);
        string[] Names(SmartCategory c) => s.Where(x => x.Category == c).Select(x => x.Name).ToArray();

        Assert.Equal(["Epic Games Launcher", "Steam"], Names(SmartCategory.Platforms).Order());
        Assert.Contains("Hades", Names(SmartCategory.Games));
        Assert.Contains("Genshin Impact", Names(SmartCategory.Games));
        Assert.DoesNotContain("Wallpaper Engine", Names(SmartCategory.Games));
        Assert.Equal(["哔哩哔哩"], Names(SmartCategory.Entertainment));
        Assert.Equal(AppRuleKind.Folder, s.Single(x => x.Name == "Hades").Kind);
        // 按名字认出的游戏：目录名也像游戏，就整体拦截目录
        Assert.Equal(AppRuleKind.Folder, s.Single(x => x.Name == "Genshin Impact").Kind);
        Assert.Equal(@"D:\Games\Genshin Impact", s.Single(x => x.Name == "Genshin Impact").Target);

        var sites = Names(SmartCategory.Sites);
        Assert.DoesNotContain("youtube.com", sites);
        foreach (var d in new[] { "bilibili.com", "reddit.com", "twitter.com", "x.com", "facebook.com", "twitch.tv" })
            Assert.Contains(d, sites);

        var all = s.Select(x => x.Name).ToList();
        foreach (var never in new[] { "Google Chrome", "Discord", "WeChat", "Visual Studio Code", "Spotify", "Microsoft Word", "Task Manager" })
            Assert.DoesNotContain(never, all);
    }

    [Fact]
    public void Game_library_folders_use_library_root_or_steamapps_common()
    {
        var root = Path.Combine(Path.GetTempPath(), "fy-lib-" + Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(root, "Steam");
        var library = Path.Combine(root, "SteamLibrary");
        var epic = Path.Combine(root, "Epic Games");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common"));
        Directory.CreateDirectory(epic);
        try
        {
            var folders = SmartBlock.GameFolders(new ScanResult
            {
                SteamInstallPath = steam,
                SteamLibraries = [steam, library],
                EpicRoots = [epic],
            });
            Assert.Equal(
                [Path.Combine(steam, "steamapps", "common"), library, epic],
                folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exe_rows_inside_a_game_folder_are_merged_into_one_row()
    {
        var game = new ScannedApp { Name = "Hades", Path = @"D:\SteamLibrary\steamapps\common\Hades", Source = AppSource.Steam, Kind = AppRuleKind.Folder, Category = AppCategory.Game };
        var merged = AppScanner.MergeGameFolders(
            [
                Exe("Hades", @"D:\SteamLibrary\steamapps\common\Hades\x64\Hades.exe"),
                Exe("Hades (DX12)", @"D:\SteamLibrary\steamapps\common\Hades\x64Vk\Hades.exe"),
                Exe("Notepad++", @"C:\Program Files\Notepad++\notepad++.exe"),
            ],
            [game, game]);
        Assert.Equal(["Hades", "Notepad++"], merged.Select(m => m.Name).Order());
        Assert.Equal(AppRuleKind.Folder, merged.Single(m => m.Name == "Hades").Kind);
    }

    [Fact]
    public void Installer_icon_cache_is_not_an_app()
    {
        Assert.True(AppScanner.IsInstallerCache(@"C:\Users\me\AppData\Roaming\Microsoft\Installer\{GUID}\icon.exe"));
        Assert.True(AppScanner.IsInstallerCache(@"C:\Windows\Installer\{GUID}\ARPPRODUCTICON.exe"));
        Assert.False(AppScanner.IsInstallerCache(@"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void Folder_rules_reject_broad_locations()
    {
        Assert.False(RuleSafety.CanBlockFolder(@"C:\", out _));
        Assert.False(RuleSafety.CanBlockFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), out _));
        Assert.False(RuleSafety.CanBlockFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _));
        Assert.False(RuleSafety.CanBlockFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), out _));
        Assert.False(RuleSafety.CanBlockFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
        Assert.False(RuleSafety.CanBlockFolder(AppContext.BaseDirectory, out _)); // 包含本进程
        Assert.True(RuleSafety.CanBlockFolder(@"D:\SteamLibrary\steamapps\common\Hades", out _));
    }

    [Fact]
    public async Task Folder_rule_blocks_any_exe_started_inside_it()
    {
        var gameDir = TempDir("FakeGame");
        var bin = Directory.CreateDirectory(Path.Combine(gameDir, "bin")).FullName;
        var exe = Path.Combine(bin, "fakegame.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        try
        {
            var rule = new AppRule { Name = "Fake Game", Path = gameDir, Kind = AppRuleKind.Folder };
            using var p = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            await Task.Delay(200);
            Assert.Single(ProcessBlocker.FindRunning([rule]), m => m.Pid == p.Id);

            using var blocker = new ProcessBlocker();
            var blocked = new TaskCompletionSource<AppBlockedEventArgs>();
            blocker.AppBlocked += (_, e) => blocked.TrySetResult(e);
            blocker.Activate([rule], TimeSpan.Zero);
            var e = await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Fake Game", e.Name);
            Assert.True(p.WaitForExit(5000));
        }
        finally
        {
            for (int i = 0; i < 10; i++)
            {
                try { Directory.Delete(Path.GetDirectoryName(gameDir)!, recursive: true); break; }
                catch (UnauthorizedAccessException) { await Task.Delay(300); }
                catch (IOException) { await Task.Delay(300); }
            }
        }
    }
}

public class SmartBlockDumpTests
{
    /// <summary>只读：把本机扫描结果对应的智能拦截建议写到文件里，人工检查用。FY_SMART_DUMP=输出文件</summary>
    [Fact]
    public void Dump_real_suggestions()
    {
        var output = Environment.GetEnvironmentVariable("FY_SMART_DUMP");
        if (string.IsNullOrEmpty(output)) return;
        var scan = AppScanner.ScanAll(withIcons: false);
        var lines = new List<string>
        {
            $"apps={scan.Apps.Count} games={scan.Apps.Count(a => a.Category == AppCategory.Game)} system={scan.Apps.Count(a => a.Category == AppCategory.System)}",
            $"steam={scan.SteamInstallPath} libs={string.Join(" | ", scan.SteamLibraries)} epic={string.Join(" | ", scan.EpicRoots)}",
        };
        lines.AddRange(scan.Apps.Where(a => a.Kind == AppRuleKind.Folder).Select(a => $"GAME {a.Name} [{a.ExeCount} exe] {a.Path}"));
        lines.AddRange(SmartBlock.Suggest(scan, new AppConfig()).Select(s => $"{s.Category}: {s.Name} -> {s.Target}"));
        File.WriteAllLines(output, lines);
    }
}
