using FocusYourself.Core;
using FocusYourself.Services;
using Microsoft.Win32;

namespace FocusYourself.Tests;

public class PomodoroEngineTests
{
    DateTime _now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    PomodoroEngine Create(int rounds = 2, bool autoNext = true) =>
        new(new PomodoroSettings { FocusMinutes = 25, ShortBreakMinutes = 5, LongBreakMinutes = 15, RoundsBeforeLongBreak = rounds, AutoStartNextFocus = autoNext },
            () => _now);

    [Fact]
    public void Focus_then_short_break_then_long_break()
    {
        var e = Create(rounds: 2);
        var phases = new List<Phase>();
        e.PhaseChanged += (_, a) => phases.Add(a.NewPhase);

        e.StartFocus();
        Assert.Equal(TimeSpan.FromMinutes(25), e.Remaining);

        _now = _now.AddMinutes(25);
        e.Tick();
        Assert.Equal(Phase.ShortBreak, e.Phase);
        Assert.Equal(1, e.CompletedFocusRounds);

        _now = _now.AddMinutes(5);
        e.Tick();
        Assert.Equal(Phase.Focus, e.Phase); // 自动开始下一轮

        _now = _now.AddMinutes(25);
        e.Tick();
        Assert.Equal(Phase.LongBreak, e.Phase);
        Assert.Equal(TimeSpan.FromMinutes(15), e.Remaining);

        Assert.Equal([Phase.Focus, Phase.ShortBreak, Phase.Focus, Phase.LongBreak], phases);
    }

    [Fact]
    public void Break_ends_in_idle_when_auto_start_disabled()
    {
        var e = Create(autoNext: false);
        e.StartFocus();
        _now = _now.AddMinutes(25);
        e.Tick();
        _now = _now.AddMinutes(5);
        e.Tick();
        Assert.Equal(Phase.Idle, e.Phase);
    }

    [Fact]
    public void Skipping_focus_does_not_count_as_completed()
    {
        var e = Create();
        e.StartFocus();
        e.Skip();
        Assert.Equal(Phase.ShortBreak, e.Phase);
        Assert.Equal(0, e.CompletedFocusRounds);
    }

    [Fact]
    public void Progress_and_remaining_follow_wall_clock()
    {
        var e = Create();
        e.StartFocus();
        _now = _now.AddMinutes(10); // 例如电脑休眠了 10 分钟
        Assert.Equal(TimeSpan.FromMinutes(15), e.Remaining);
        Assert.Equal(0.4, e.Progress, 3);
    }

    [Fact]
    public void Restore_resumes_unfinished_focus_and_drops_expired()
    {
        var e = Create();
        e.StartFocus();
        var snapshot = e.Snapshot();

        var resumed = Create();
        resumed.Restore(snapshot);
        Assert.Equal(Phase.Focus, resumed.Phase);

        _now = _now.AddHours(1);
        var expired = Create();
        expired.Restore(snapshot);
        Assert.Equal(Phase.Idle, expired.Phase);
    }
}

public class DomainUtilTests
{
    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("https://www.bilibili.com/video/BV1xx?p=1", "bilibili.com")]
    [InlineData("  M.YouTube.com:443/watch ", "m.youtube.com")]
    [InlineData("http://user@example.org/", "example.org")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, DomainUtil.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("not a domain")]
    [InlineData("localhost")]
    [InlineData("javascript:alert(1)")]
    public void Rejects(string input) => Assert.Null(DomainUtil.Normalize(input));
}

public class RuleSafetyTests
{
    const string Helium = @"C:\Users\x\AppData\Local\imput\Helium\Application\chrome.exe";
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Fact]
    public void Critical_programs_cannot_be_blocked() =>
        Assert.False(RuleSafety.CanBlock(@"C:\Windows\explorer.exe", out _));

    [Fact]
    public void Chrome_exe_cannot_use_strong_mode_because_helium_shares_its_name()
    {
        Assert.True(RuleSafety.CanBlock(Chrome, out _));
        Assert.False(RuleSafety.CanUseStrong(Chrome, [("Helium", Helium)], out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Same_file_name_in_another_folder_blocks_strong_mode()
    {
        var known = new[] { ("Other Game", @"D:\Games\B\game_b.exe"), ("Game A", @"D:\Games\A\game_b.exe") };
        Assert.False(RuleSafety.CanUseStrong(@"D:\Games\A\game_b.exe", known, out var reason));
        Assert.Contains("Other Game", reason);
    }

    [Fact]
    public void Unique_name_can_use_strong_mode() =>
        Assert.True(RuleSafety.CanUseStrong(@"C:\Program Files\Steam\steam.exe", [("Helium", Helium)], out _));

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app2.exe\",0", @"C:\Program Files\App\app2.exe")]
    [InlineData(@"C:\Tools\tool.exe", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Tools\tool.ico", null)]
    public void Parses_display_icon(string value, string? expected) => Assert.Equal(expected, PathUtil.ParseIconPath(value));
}

public class ConfigStoreTests
{
    [Fact]
    public void Round_trips_config_and_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(dir);
            var config = store.LoadConfig();
            Assert.Contains(config.Sites, s => s.Domain == "youtube.com");

            config.Apps.Add(new AppRule { Name = "Steam", Path = @"C:\Steam\steam.exe", Mode = BlockMode.Strong });
            store.SaveConfig(config);
            store.SaveState(new FocusState { Phase = Phase.Focus, EndsAtUtc = DateTime.UtcNow.AddMinutes(5), CompletedFocusRounds = 3 });

            var again = new ConfigStore(dir);
            Assert.Equal(BlockMode.Strong, again.LoadConfig().Apps.Single().Mode);
            Assert.Equal(3, again.LoadState().CompletedFocusRounds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_config_falls_back_to_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ not json");
            var config = new ConfigStore(dir).LoadConfig();
            Assert.Equal(25, config.Pomodoro.FocusMinutes);
            Assert.True(File.Exists(Path.Combine(dir, "config.json.broken")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>在 HKCU 下的临时键里测试 IFEO 逻辑，不触碰真实的 HKLM。</summary>
public class IfeoBlockerTests : IDisposable
{
    readonly string _root = $@"Software\FocusYourselfTests\{Guid.NewGuid():N}";
    readonly IfeoBlocker _ifeo;

    public IfeoBlockerTests() => _ifeo = new IfeoBlocker(@"C:\App\FocusYourself.exe", RegistryHive.CurrentUser, _root);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root.Substring(0, _root.LastIndexOf('\\')), throwOnMissingSubKey: false);

    RegistryKey Root => Registry.CurrentUser.CreateSubKey(_root);

    [Fact]
    public void Apply_writes_debugger_and_clear_removes_created_keys()
    {
        Assert.Empty(_ifeo.Apply([@"D:\Games\game.exe"]));
        using (var key = Root.OpenSubKey("game.exe"))
            Assert.Equal("\"C:\\App\\FocusYourself.exe\" --blocked", key!.GetValue("Debugger"));
        Assert.Equal(["game.exe"], _ifeo.FindResidue());

        Assert.True(_ifeo.Clear());
        Assert.Null(Root.OpenSubKey("game.exe"));
        Assert.Empty(_ifeo.FindResidue());
    }

    [Fact]
    public void Existing_key_is_kept_and_foreign_debugger_is_not_overwritten()
    {
        using (var k = Root.CreateSubKey("mitigated.exe")) k.SetValue("MitigationOptions", 1);
        using (var k = Root.CreateSubKey("foreign.exe")) k.SetValue("Debugger", "vsjitdebugger.exe");

        var failed = _ifeo.Apply([@"C:\a\mitigated.exe", @"C:\b\foreign.exe"]);
        Assert.Equal(["foreign.exe"], failed);
        using (var k = Root.OpenSubKey("foreign.exe")) Assert.Equal("vsjitdebugger.exe", k!.GetValue("Debugger"));

        _ifeo.Clear();
        using (var k = Root.OpenSubKey("mitigated.exe"))
        {
            Assert.NotNull(k);
            Assert.Null(k!.GetValue("Debugger"));
            Assert.Equal(1, k.GetValue("MitigationOptions"));
        }
        using (var k = Root.OpenSubKey("foreign.exe")) Assert.Equal("vsjitdebugger.exe", k!.GetValue("Debugger"));
    }
}

public class ThemeModeTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("blue", "auto")]
    public void Normalizes_unknown_and_missing_to_auto(string? input, string expected) =>
        Assert.Equal(expected, Views.ThemeManager.NormalizeMode(input));

    [Fact]
    public void Cycles_light_dark_auto()
    {
        Assert.Equal("dark", Views.ThemeManager.NextMode("light"));
        Assert.Equal("auto", Views.ThemeManager.NextMode("dark"));
        Assert.Equal("light", Views.ThemeManager.NextMode("auto"));
    }
}
