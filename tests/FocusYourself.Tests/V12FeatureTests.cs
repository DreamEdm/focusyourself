using System.Text.RegularExpressions;
using FocusYourself.Core;
using FocusYourself.Services;
using FocusYourself.ViewModels;
using FocusYourself.Views;

namespace FocusYourself.Tests;

/// <summary>1.2.0：新图标、番茄进度、扩展安装向导。</summary>
public class TomatoShapeTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FocusYourself.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Icon_generator_uses_the_same_outline_as_the_app()
    {
        var py = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "make_icons.py"));
        string get(string name) => Regex.Match(py, $@"^{name} = ""([^""]+)""", RegexOptions.Multiline).Groups[1].Value;
        Assert.Equal(TomatoShape.BodyData, get("BODY"));
        Assert.Equal(TomatoShape.CalyxData, get("CALYX"));
        Assert.Equal(TomatoShape.StemData, get("STEM"));
    }

    [Fact]
    public void Outline_fits_the_32_unit_box()
    {
        foreach (var data in new[] { TomatoShape.BodyData, TomatoShape.CalyxData, TomatoShape.StemData })
        {
            var figures = TomatoShape.Flatten(data, 1);
            Assert.NotEmpty(figures);
            Assert.All(figures.SelectMany(f => f), p => Assert.True(p.X is >= 0 and <= 32 && p.Y is >= 0 and <= 32, $"{p} out of box"));
        }
    }

    [Fact]
    public void Tray_icon_draws_for_every_phase()
    {
        foreach (var phase in Enum.GetValues<Phase>())
        {
            using var bmp = TrayIconFactory.DrawBitmap(phase, 32);
            Assert.Equal(0, bmp.GetPixel(0, 31).A);   // 左下角透明
            Assert.Equal(255, bmp.GetPixel(16, 22).A); // 果实中心不透明
        }
    }

    [Fact]
    public void Round_dot_fill_grows_over_the_body()
    {
        var dot = new RoundDot();
        Assert.Equal(0, dot.FillHeight);
        dot.Fill = 0.5;
        Assert.Equal(1.6 + 11.4, dot.FillHeight, 3);
        dot.Fill = 1;
        Assert.Equal(32, dot.FillHeight);
    }

    [Fact]
    public void Focus_minutes_are_clamped_like_settings()
    {
        Assert.Equal(1, MainViewModel.ClampFocusMinutes(0));
        Assert.Equal(45, MainViewModel.ClampFocusMinutes(45));
        Assert.Equal(180, MainViewModel.ClampFocusMinutes(999));
    }
}

public class BrowserInstallerTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("C:\\Program Files\\Internet Explorer\\iexplore.exe", "C:\\Program Files\\Internet Explorer\\iexplore.exe")]
    [InlineData("C:\\Apps\\Browser\\browser.exe -osint -url %1", "C:\\Apps\\Browser\\browser.exe")]
    [InlineData("", null)]
    public void Exe_is_parsed_from_the_open_command(string command, string? expected) =>
        Assert.Equal(expected, BrowserInstaller.ExeFromCommand(command));

    [Theory]
    [InlineData("C:\\x\\msedge.exe", "edge://extensions")]
    [InlineData("C:\\x\\Opera\\opera.exe", "opera://extensions")]
    [InlineData("C:\\x\\chrome.exe", "chrome://extensions")]
    [InlineData("C:\\x\\quark.exe", "chrome://extensions")]
    public void Extensions_page_depends_on_the_browser(string exe, string url) =>
        Assert.Equal(url, new InstalledBrowser("x", exe, true).ExtensionsUrl);

    [Fact]
    public void Firefox_is_listed_but_not_supported()
    {
        Assert.Equal(BrowserFamily.Firefox, BrowserInstaller.FamilyOf(@"C:\Program Files\Mozilla Firefox\firefox.exe"));
        Assert.False(BrowserInstaller.Describe(@"C:\Program Files\Mozilla Firefox\firefox.exe", "Firefox").Supported);
        Assert.True(BrowserInstaller.Describe(@"C:\x\msedge.exe", "Edge").Supported);
    }

    [Fact]
    public void Renamed_chromium_is_recognized_by_its_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-chromium-" + Guid.NewGuid().ToString("N"));
        var version = Directory.CreateDirectory(Path.Combine(dir, "4.1.2.384")).FullName;
        try
        {
            var exe = Path.Combine(dir, "quark.exe");
            File.WriteAllText(exe, "");
            Assert.False(BrowserInstaller.LooksLikeChromium(exe));
            File.WriteAllText(Path.Combine(version, "resources.pak"), "");
            File.WriteAllText(Path.Combine(version, "quark_elf.dll"), "");
            Assert.True(BrowserInstaller.LooksLikeChromium(exe));
            Assert.True(BrowserInstaller.Describe(exe, "Quark").Supported);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Connection_is_matched_by_install_folder()
    {
        var helium = new InstalledBrowser("Helium", @"C:\Users\me\AppData\Local\imput\Helium\Application\chrome.exe", true);
        Assert.True(helium.Owns(new BridgeConnectionInfo("Chrome", @"C:\Users\me\AppData\Local\imput\Helium\Application\chrome.exe", DateTime.UtcNow)));
        Assert.False(helium.Owns(new BridgeConnectionInfo("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", DateTime.UtcNow)));
        // 目录名只是前缀相同不算
        Assert.False(helium.Owns(new BridgeConnectionInfo("Chrome", @"C:\Users\me\AppData\Local\imput\Helium\Application2\chrome.exe", DateTime.UtcNow)));
        Assert.False(helium.Owns(new BridgeConnectionInfo("Chrome", null, DateTime.UtcNow)));
    }
}

public class ExtensionSetupTests
{
    const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string EdgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    const string FirefoxExe = @"C:\Program Files\Mozilla Firefox\firefox.exe";

    static (ExtensionSetupViewModel vm, List<BridgeConnectionInfo> conns, List<string> opened) Create()
    {
        var conns = new List<BridgeConnectionInfo>();
        var opened = new List<string>();
        var vm = new ExtensionSetupViewModel(
            [new InstalledBrowser("Chrome", ChromeExe, true), new InstalledBrowser("Edge", EdgeExe, true), new InstalledBrowser("Firefox", FirefoxExe, false)],
            @"C:\FocusYourself\extension", () => conns,
            b => { opened.Add(b.Name); return Task.FromResult(true); },
            _ => { });
        return (vm, conns, opened);
    }

    [Fact]
    public void Unsupported_and_connected_browsers_start_unchecked()
    {
        var conns = new List<BridgeConnectionInfo> { new("Chrome", ChromeExe, DateTime.UtcNow) };
        var vm = new ExtensionSetupViewModel(
            [new InstalledBrowser("Chrome", ChromeExe, true), new InstalledBrowser("Edge", EdgeExe, true), new InstalledBrowser("Firefox", FirefoxExe, false)],
            "x", () => conns, _ => Task.FromResult(true), _ => { });
        Assert.Equal([false, true, false], vm.Browsers.Select(b => b.IsChecked));
        Assert.True(vm.Browsers[0].IsConnected);
        vm.Browsers[2].IsChecked = true; // 不支持的勾不上
        Assert.False(vm.Browsers[2].IsChecked);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public void Walks_through_each_browser_and_summarizes()
    {
        var (vm, conns, opened) = Create();
        Assert.Equal(SetupPage.Choose, vm.Page);
        vm.StartCommand.Execute(null);

        Assert.Equal(SetupPage.Install, vm.Page);
        Assert.Equal(["Chrome"], opened);
        Assert.Equal("Chrome", vm.Current?.Name);
        Assert.Equal(StepState.Active, vm.Current?.State);
        Assert.False(vm.OpenFailed);

        // 扩展在 Chrome 里连上了
        conns.Add(new BridgeConnectionInfo("Chrome", ChromeExe, DateTime.UtcNow));
        vm.Tick();
        Assert.True(vm.CurrentConnected);
        Assert.Equal(StepState.Connected, vm.Queue[0].State);

        vm.NextCommand.Execute(null);
        Assert.Equal("Edge", vm.Current?.Name);
        Assert.Equal(["Chrome", "Edge"], opened);
        Assert.False(vm.CurrentConnected);
        Assert.True(vm.IsLast);

        vm.SkipCommand.Execute(null);
        Assert.Equal(SetupPage.Done, vm.Page);
        Assert.Equal(StepState.Skipped, vm.Queue[1].State);
        Assert.False(vm.AllConnected);
        Assert.Equal(Loc.F("SetupDoneSome", 1, 2), vm.DoneText);
    }

    [Fact]
    public void Failed_open_shows_the_manual_hint()
    {
        var vm = new ExtensionSetupViewModel([new InstalledBrowser("Edge", EdgeExe, true)], "x", () => [], _ => Task.FromResult(false), _ => { });
        vm.StartCommand.Execute(null);
        Assert.True(vm.OpenFailed);
        Assert.Equal(Loc.F("SetupStep1Failed", "edge://extensions", "Edge"), vm.OpenText);
    }

    [Fact]
    public void Close_is_requested()
    {
        var (vm, _, _) = Create();
        vm.CloseCommand.Execute(null);
        Assert.True(vm.CloseRequested);
    }
}
