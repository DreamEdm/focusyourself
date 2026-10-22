using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.Tests;

public class BrowserBridgeTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task<ClientWebSocket> ConnectAsync(int port, string origin)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), new CancellationTokenSource(5000).Token);
        return ws;
    }

    static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, new CancellationTokenSource(5000).Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }

    [Fact]
    public async Task Extension_origin_receives_state_and_broadcasts()
    {
        using var bridge = new BrowserBridge { ExtensionId = ExtensionInfo.DefaultId };
        var port = FreePort();
        Assert.True(bridge.Start(port));

        using var ws = await ConnectAsync(port, $"chrome-extension://{ExtensionInfo.DefaultId}");
        var initial = await ReceiveJsonAsync(ws);
        Assert.Equal("state", initial.GetProperty("type").GetString());
        Assert.Equal("idle", initial.GetProperty("state").GetString());

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"hello","browser":"Helium"}"""), WebSocketMessageType.Text, true, default);
        await ReceiveJsonAsync(ws); // hello 之后会再发一次当前状态

        await bridge.BroadcastAsync(new BridgeState("focus", ["youtube.com"], 123, 100, 2));
        var focus = await ReceiveJsonAsync(ws);
        Assert.Equal("focus", focus.GetProperty("state").GetString());
        Assert.Equal("youtube.com", focus.GetProperty("domains")[0].GetString());
        Assert.Equal(2, focus.GetProperty("rounds").GetInt32());

        // 能识别出连接来自哪个进程（这里是测试进程本身）
        var conn = Assert.Single(bridge.Connections);
        Assert.Equal("Helium", conn.Browser);
        Assert.NotNull(conn.OwnerPath);
        Assert.True(PathUtil.SamePath(conn.OwnerPath!, Environment.ProcessPath!));
        Assert.NotNull(bridge.LastSeenFor(Environment.ProcessPath!));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Other_origins_are_rejected(string origin)
    {
        using var bridge = new BrowserBridge { ExtensionId = ExtensionInfo.DefaultId };
        var port = FreePort();
        bridge.Start(port);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(port, origin));
        Assert.Empty(bridge.Connections);
    }
}

public class ProcessBlockerTests
{
    static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    static Process StartPing() => Process.Start(new ProcessStartInfo(Ping, "-n 60 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    static AppRule PingRule => new() { Name = "Ping", Path = Ping };

    [Fact]
    public async Task Kills_new_matching_process_and_reports_it()
    {
        using var blocker = new ProcessBlocker();
        var blocked = new TaskCompletionSource<AppBlockedEventArgs>();
        blocker.AppBlocked += (_, e) => blocked.TrySetResult(e);
        blocker.Activate([PingRule], TimeSpan.Zero);

        using var p = StartPing();
        var e = await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Ping", e.Name);
        Assert.False(e.Failed);
        Assert.True(p.WaitForExit(5000));
    }

    [Fact]
    public async Task Already_running_process_gets_grace_period()
    {
        using var p = StartPing();
        await Task.Delay(300);
        using var blocker = new ProcessBlocker();
        blocker.Activate([PingRule], TimeSpan.FromSeconds(3));

        await Task.Delay(1500);
        Assert.False(p.HasExited); // 宽限期内不关闭

        Assert.True(p.WaitForExit(10_000)); // 宽限期过后被关闭
    }

    [Fact]
    public async Task Deactivated_blocker_leaves_processes_alone()
    {
        using var blocker = new ProcessBlocker();
        blocker.Activate([PingRule], TimeSpan.Zero);
        blocker.Deactivate();

        using var p = StartPing();
        await Task.Delay(1500);
        Assert.False(p.HasExited);
        p.Kill();
    }

    [Fact]
    public void Path_matching_is_exact_not_by_file_name()
    {
        // 同名但路径不同的规则不应命中
        var other = new AppRule { Name = "Fake", Path = @"C:\Nowhere\PING.EXE" };
        using var p = StartPing();
        try
        {
            Assert.Empty(ProcessBlocker.FindRunning([other]));
            Assert.Contains(ProcessBlocker.FindRunning([PingRule]), m => m.Pid == p.Id);
        }
        finally
        {
            p.Kill();
        }
    }
}

public class AppScannerTests
{
    [Fact]
    public void Finds_installed_apps_without_noise()
    {
        var apps = AppScanner.Scan(withIcons: false);
        Assert.NotEmpty(apps);
        Assert.DoesNotContain(apps, a => AppScanner.IsNoise(a.Path));
        Assert.Equal(apps.Count, apps.Select(a => PathUtil.Normalize(a.Path)).Distinct().Count());
    }
}

public class FocusCoordinatorTests
{
    sealed class FakeUi : IFocusUi
    {
        public List<(ToastKind Kind, string Title)> Toasts { get; } = [];
        public void Clear()
        {
            Toasts.Clear();
            Chimes.Clear();
        }
        public List<Chime> Chimes { get; } = [];
        public void ShowToast(ToastKind kind, string title, string message) => Toasts.Add((kind, title));
        public void ShowBlocked(string appName, string appPath) { }
        public void ShowFolderBlocked(string folderName, string openedPath, int continueDelaySeconds, Action allow) { }
        public void PlayChime(Chime chime) => Chimes.Add(chime);
    }

    [Fact]
    public void Focus_enables_site_blocking_and_break_lifts_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-test-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        var config = store.LoadConfig();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        config.BridgePort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        config.Sites.Add(new SiteRule { Domain = "twitch.tv", Enabled = false });

        var ui = new FakeUi();
        using var coord = new FocusCoordinator(store, config, ui);
        try
        {
            coord.Initialize(new FocusState());
            Assert.Equal("idle", coord.CurrentBridgeState().State);

            coord.Engine.StartFocus();
            var focus = coord.CurrentBridgeState();
            Assert.Equal("focus", focus.State);
            Assert.Equal(["youtube.com", "bilibili.com"], focus.Domains);
            Assert.True(coord.Blocker.IsActive);
            Assert.True(coord.Folders.IsActive);
            Assert.Contains(ui.Toasts, t => t.Kind == ToastKind.Focus);
            Assert.Contains(Chime.FocusStart, ui.Chimes);
            Assert.Equal(Phase.Focus, store.LoadState().Phase); // 状态已持久化

            coord.Engine.Skip();
            Assert.Equal("break", coord.CurrentBridgeState().State);
            Assert.Empty(coord.CurrentBridgeState().Domains);
            Assert.False(coord.Blocker.IsActive);
            Assert.False(coord.Folders.IsActive);
            Assert.Contains(Chime.BreakStart, ui.Chimes);
        }
        finally
        {
            coord.Shutdown(endFocus: true);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pause_keeps_blocking_plays_chimes_and_reminds_every_five_minutes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fy-test-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        var config = store.LoadConfig();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        config.BridgePort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var ui = new FakeUi();
        using var coord = new FocusCoordinator(store, config, ui);
        try
        {
            coord.Initialize(new FocusState());
            coord.Engine.StartFocus();
            ui.Clear();

            coord.Engine.Pause();
            Assert.Equal([Chime.Pause], ui.Chimes);
            Assert.True(coord.Blocker.IsActive); // 暂停不解除拦截
            var bridge = coord.CurrentBridgeState();
            Assert.Equal("focus", bridge.State);
            Assert.NotEmpty(bridge.Domains);
            Assert.NotNull(bridge.PausedAt);
            Assert.NotNull(store.LoadState().PausedAtUtc); // 暂停状态已持久化

            // 刚暂停：不提醒
            coord.Tick();
            Assert.Empty(ui.Toasts);

            // 引擎用的是真实时钟，这里通过恢复一个“5 分钟前暂停”的状态来模拟时间流逝
            var paused = store.LoadState();
            paused.PausedAtUtc = DateTime.UtcNow - FocusCoordinator.PauseReminderInterval - TimeSpan.FromSeconds(1);
            coord.Engine.Restore(paused);
            coord.Tick();
            var reminder = Assert.Single(ui.Toasts);
            Assert.Equal(ToastKind.Paused, reminder.Kind);
            Assert.Equal(Chime.PauseReminder, ui.Chimes[^1]);
            coord.Tick(); // 同一个 5 分钟内不重复提醒
            Assert.Single(ui.Toasts);

            // 休眠了很久：只补发一次
            paused.PausedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(23);
            coord.Engine.Restore(paused);
            coord.Tick();
            Assert.Equal(2, ui.Toasts.Count);
            coord.Tick();
            Assert.Equal(2, ui.Toasts.Count);

            ui.Clear();
            coord.Engine.Resume();
            Assert.Equal([Chime.Resume], ui.Chimes);
            Assert.Null(coord.CurrentBridgeState().PausedAt);
            Assert.Null(store.LoadState().PausedAtUtc);
            coord.Tick();
            Assert.Empty(ui.Toasts);
        }
        finally
        {
            coord.Shutdown(endFocus: true);
            Directory.Delete(dir, recursive: true);
        }
    }
}
