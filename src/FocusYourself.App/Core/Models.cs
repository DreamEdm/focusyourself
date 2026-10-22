namespace FocusYourself.Core;

public enum BlockMode
{
    /// <summary>检测到进程后关闭，无需管理员权限。</summary>
    Standard,
    /// <summary>通过 IFEO 让程序根本无法启动，需要管理员权限。</summary>
    Strong,
}

public enum AppRuleKind
{
    /// <summary>单个 exe（按完整路径匹配）。</summary>
    Exe,
    /// <summary>整个安装目录（例如一个 Steam 游戏）：从该目录启动的所有程序都会被拦截。</summary>
    Folder,
}

public enum Phase
{
    Idle,
    Focus,
    ShortBreak,
    LongBreak,
}

public enum OverlayCorner
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
}

public sealed class AppRule
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public BlockMode Mode { get; set; } = BlockMode.Standard;
    public AppRuleKind Kind { get; set; } = AppRuleKind.Exe;
}

public sealed class SiteRule
{
    public string Domain { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class FolderRule
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class PomodoroSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int RoundsBeforeLongBreak { get; set; } = 4;
    /// <summary>休息结束后自动开始下一轮专注。</summary>
    public bool AutoStartNextFocus { get; set; } = true;
}

public sealed class AppConfig
{
    public List<AppRule> Apps { get; set; } = [];

    public List<SiteRule> Sites { get; set; } =
    [
        new() { Domain = "youtube.com" },
        new() { Domain = "bilibili.com" },
    ];

    /// <summary>专注期间在资源管理器中受限的文件夹。</summary>
    public List<FolderRule> Folders { get; set; } = [];

    public PomodoroSettings Pomodoro { get; set; } = new();

    /// <summary>专注开始时，给已在运行的被禁程序留出保存工作的秒数。</summary>
    public int CloseGraceSeconds { get; set; } = 10;

    public bool StrictMode { get; set; }
    public int StrictUnlockSeconds { get; set; } = 60;

    public int BridgePort { get; set; } = 47621;
    public string ExtensionId { get; set; } = ExtensionInfo.DefaultId;

    /// <summary>专注期内，这些浏览器若没有连接扩展就会被关闭。</summary>
    public bool GuardBrowsers { get; set; } = true;
    public List<string> GuardedBrowsers { get; set; } = [];
    public bool GuardedBrowsersInitialized { get; set; }

    /// <summary>扩展是否至少连接过一次。之前不启用浏览器守护，避免还没装扩展就把浏览器关掉。</summary>
    public bool ExtensionSeen { get; set; }

    public bool SoundEnabled { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;
    public double OverlayOpacity { get; set; } = 0.85;

    public bool AutoStart { get; set; }

    /// <summary>界面语言（en / zh-CN）；为空时按系统语言决定。</summary>
    public string? Language { get; set; }

    /// <summary>界面主题（dark / light / auto）；auto 或为空时跟随 Windows 设置。</summary>
    public string? Theme { get; set; }

    /// <summary>首次使用的“智能拦截”引导是否已经处理过（用过或关掉）。</summary>
    public bool SmartBlockOffered { get; set; }
}

/// <summary>番茄钟运行状态，单独持久化，用于程序被关闭后恢复以及 IFEO 拦截实例读取剩余时间。</summary>
public sealed class FocusState
{
    public Phase Phase { get; set; } = Phase.Idle;
    public DateTime? EndsAtUtc { get; set; }
    public DateTime? PhaseStartedUtc { get; set; }
    /// <summary>暂停的时刻；没有暂停为 null。</summary>
    public DateTime? PausedAtUtc { get; set; }
    public int CompletedFocusRounds { get; set; }
}

public static class ExtensionInfo
{
    /// <summary>由 extension/manifest.json 中的 key 推导出的固定扩展 ID。</summary>
    public const string DefaultId = "gfjfpbehokfclllomgjpppgmkaghlgjo";
}
