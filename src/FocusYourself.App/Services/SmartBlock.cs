using System.IO;
using System.Text.RegularExpressions;
using FocusYourself.Core;

namespace FocusYourself.Services;

public enum SmartCategory { Platforms, Games, Entertainment, Sites, Folders }

/// <summary>智能拦截给出的一条建议：程序（exe 或整个游戏目录）、网站域名，或资源管理器文件夹提醒。</summary>
public sealed record SmartSuggestion(SmartCategory Category, string Name, string Target, AppRuleKind Kind = AppRuleKind.Exe, string? Detail = null);

/// <summary>
/// 智能拦截规则：只根据文件名、显示名和安装位置做模式匹配，不读取任何文件内容。
/// 永远不碰浏览器、聊天软件、开发工具、办公软件和音乐播放器。
/// </summary>
public static partial class SmartBlock
{
    public static readonly string[] Sites =
    [
        "youtube.com", "bilibili.com", "reddit.com", "twitter.com", "x.com", "facebook.com", "instagram.com",
        "tiktok.com", "douyin.com", "twitch.tv", "weibo.com", "zhihu.com", "xiaohongshu.com", "netflix.com",
        "iqiyi.com", "v.qq.com", "youku.com", "douyu.com", "huya.com", "hupu.com", "kuaishou.com", "douban.com",
    ];

    /// <summary>游戏平台、启动器、模拟器（按 exe 文件名）。</summary>
    static readonly HashSet<string> PlatformExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "epicgameslauncher.exe", "battle.net.exe", "battle.net launcher.exe", "upc.exe", "ubisoftconnect.exe",
        "origin.exe", "eadesktop.exe", "galaxyclient.exe", "riotclientservices.exe", "riotclientux.exe", "leagueclient.exe",
        "wegame.exe", "hoyoplay.exe", "minecraftlauncher.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe",
        "amazon games ui.exe", "itch.exe", "heroiclauncher.exe",
        // 模拟器
        "yuzu.exe", "ryujinx.exe", "dolphin.exe", "pcsx2.exe", "pcsx2-qt.exe", "rpcs3.exe", "retroarch.exe", "cemu.exe",
        "ppssppwindows64.exe", "ppssppwindows.exe", "duckstation-qt-x64-releaseltcg.exe", "citra-qt.exe", "xemu.exe",
        "dnplayer.exe", "mumuplayer.exe", "nemuplayer.exe", "nox.exe", "hd-player.exe", "memu.exe",
    };

    /// <summary>不在 Steam/Epic 里的常见游戏（按显示名关键词）。</summary>
    static readonly string[] GameKeywords =
    [
        "genshin", "原神", "honkai", "崩坏", "star rail", "zenless", "绝区零", "wuthering waves", "鸣潮", "minecraft",
        "league of legends", "英雄联盟", "valorant", "无畏契约", "pubg", "绝地求生", "counter-strike", "dota 2",
        "overwatch", "守望先锋", "apex legends", "fortnite", "roblox", "hearthstone", "炉石传说", "world of warcraft",
        "魔兽世界", "osu!", "arknights", "明日方舟", "naraka", "永劫无间", "地下城与勇士", "穿越火线", "crossfire",
        "王者荣耀", "和平精英", "逆水寒", "梦幻西游", "lost ark", "diablo", "暗黑破坏神", "starcraft", "星际争霸",
        "rainbow six", "warframe", "path of exile", "流放之路", "金铲铲", "第五人格", "蛋仔派对",
    ];

    /// <summary>视频、直播、短视频客户端（按显示名或 exe 名关键词）。</summary>
    static readonly string[] EntertainmentKeywords =
    [
        "bilibili", "哔哩哔哩", "腾讯视频", "qqlive", "爱奇艺", "iqiyi", "qyclient", "优酷", "youku", "抖音", "douyin",
        "快手", "kuaishou", "斗鱼", "douyu", "虎牙", "huya", "netflix", "twitch", "芒果tv", "mgtv", "西瓜视频", "tiktok",
        "disney+", "prime video",
    ];

    /// <summary>无论名字多像，都不拦：浏览器、聊天、开发工具、办公、音乐。</summary>
    static readonly HashSet<string> NeverExes = new(StringComparer.OrdinalIgnoreCase)
    {
        // 浏览器
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe", "arc.exe", "thorium.exe",
        // 聊天
        "wechat.exe", "weixin.exe", "qq.exe", "qqnt.exe", "tim.exe", "discord.exe", "telegram.exe", "dingtalk.exe", "feishu.exe",
        "lark.exe", "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "wemeetapp.exe", "skype.exe", "whatsapp.exe", "signal.exe",
        // 开发工具
        "code.exe", "devenv.exe", "idea64.exe", "pycharm64.exe", "rider64.exe", "webstorm64.exe", "clion64.exe", "goland64.exe",
        "studio64.exe", "cursor.exe", "windowsterminal.exe", "wt.exe", "git-bash.exe", "sublime_text.exe", "notepad++.exe", "unity hub.exe",
        "unity.exe", "unrealeditor.exe", "blender.exe", "godot.exe",
        // 办公
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe", "wps.exe", "et.exe", "wpp.exe", "notion.exe",
        "obsidian.exe", "acrobat.exe", "acrord32.exe",
        // 音乐
        "spotify.exe", "cloudmusic.exe", "qqmusic.exe", "kugou.exe", "kwmusic.exe", "foobar2000.exe", "itunes.exe", "applemusic.exe",
        "musicbee.exe", "aimp.exe",
    };

    /// <summary>Steam 上的软件（不是游戏）。</summary>
    static readonly string[] SteamSoftwareKeywords =
    [
        "wallpaper engine", "blender", "steamvr", "aseprite", "obs studio", "krita", "rpg maker", "substance", "houdini",
        "3dmark", "soundpad", "lossless scaling", "vtube studio", "steamworks", "proton", "spacewar", "godot", "gamemaker",
        "fl studio", "bitwig", "voicemod", "duet display", "spacedesk", "vr performance", "dedicated server",
    ];

    /// <summary>根据扫描结果给出建议。已经在拦截中的条目不再列出。</summary>
    public static List<SmartSuggestion> Suggest(ScanResult scan, AppConfig config)
    {
        var result = new List<SmartSuggestion>();
        var blockedApps = config.Apps.Where(a => a.Enabled).Select(a => Key(a.Path)).ToHashSet();
        var blockedSites = config.Sites.Where(s => s.Enabled).Select(s => s.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedFolders = config.Folders.Where(f => f.Enabled).Select(f => Key(f.Path)).ToHashSet();
        var seen = new HashSet<string>();

        void AddApp(SmartCategory cat, ScannedApp app)
        {
            var key = Key(app.Path);
            if (blockedApps.Contains(key) || !seen.Add(key)) return;
            var ok = app.Kind == AppRuleKind.Folder ? RuleSafety.CanBlockFolder(app.Path, out _) : RuleSafety.CanBlock(app.Path, out _);
            if (!ok) return;
            result.Add(new SmartSuggestion(cat, app.Name, app.Path, app.Kind, app.Path));
        }

        foreach (var app in scan.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (app.Category == AppCategory.System) continue;
            if (app.Kind == AppRuleKind.Folder)
            {
                if (!IsSteamSoftware(app.Name)) AddApp(SmartCategory.Games, app);
                continue;
            }
            if (IsNever(app.Path)) continue;
            var file = PathUtil.FileName(app.Path);
            if (PlatformExes.Contains(file)) AddApp(SmartCategory.Platforms, app);
            else if (MatchesAny(app.Name, GameKeywords)) AddApp(SmartCategory.Games, GameFolderOf(app) ?? app);
            else if (MatchesAny(app.Name, EntertainmentKeywords) || MatchesAny(System.IO.Path.GetFileNameWithoutExtension(file), EntertainmentKeywords))
                AddApp(SmartCategory.Entertainment, app);
        }

        // 只从注册表知道 Steam 路径、开始菜单里却没有 steam.exe 的情况
        if (scan.SteamInstallPath is { } steamDir)
        {
            var steamExe = System.IO.Path.Combine(steamDir, "steam.exe");
            if (File.Exists(steamExe))
                AddApp(SmartCategory.Platforms, new ScannedApp { Name = "Steam", Path = steamExe, Source = AppSource.Steam });
        }

        foreach (var domain in Sites.Where(d => !blockedSites.Contains(d)))
            result.Add(new SmartSuggestion(SmartCategory.Sites, domain, domain));

        foreach (var folder in GameFolders(scan))
        {
            var key = Key(folder);
            if (blockedFolders.Contains(key) || !seen.Add("dir:" + key)) continue;
            result.Add(new SmartSuggestion(SmartCategory.Folders, folder, folder, AppRuleKind.Folder));
        }
        return result;
    }

    /// <summary>游戏库目录：Steam 库（安装目录本身或盘符根目录时取 steamapps\common）和 Epic Games 目录。</summary>
    public static List<string> GameFolders(ScanResult scan)
    {
        var folders = new List<string>();
        foreach (var lib in scan.SteamLibraries)
        {
            var isInstallDir = scan.SteamInstallPath is { } s && Key(s) == Key(lib);
            var isRoot = lib.TrimEnd('\\').Length <= 2;
            var folder = isInstallDir || isRoot ? System.IO.Path.Combine(lib, "steamapps", "common") : lib.TrimEnd('\\');
            if (Directory.Exists(folder)) folders.Add(folder);
        }
        folders.AddRange(scan.EpicRoots.Where(Directory.Exists));
        return folders
            .Where(f => FolderGuard.CanGuard(f, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 按名字认出的游戏（如 “鸣潮” → D:\Game\Wuthering Waves\launcher.exe）：启动器只是入口，
    /// 所在目录名也像游戏时，改为拦截整个目录，游戏本体一起拦住。
    /// </summary>
    static ScannedApp? GameFolderOf(ScannedApp app)
    {
        var dir = System.IO.Path.GetDirectoryName(app.Path);
        if (dir is null) return null;
        var dirName = System.IO.Path.GetFileName(dir);
        if (!MatchesAny(dirName, GameKeywords) && !dirName.Equals(app.Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (!RuleSafety.CanBlockFolder(dir, out _)) return null;
        return new ScannedApp
        {
            Name = app.Name, Path = dir, Source = app.Source, Kind = AppRuleKind.Folder,
            Category = AppCategory.Game, IconPath = app.Path, Icon = app.Icon,
        };
    }

    public static bool IsNever(string path) => NeverExes.Contains(PathUtil.FileName(path));

    public static bool IsSteamSoftware(string name) => MatchesAny(name, SteamSoftwareKeywords);

    /// <summary>中文按子串匹配；英文按词边界匹配，避免 “dota” 命中 “anecdota”。</summary>
    public static bool MatchesAny(string text, IEnumerable<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var k in keywords)
        {
            if (k.Any(c => c > 0x2E80))
            {
                if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(k) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }
        return false;
    }

    static string Key(string path) => FolderGuard.NormalizeFolder(path);
}
