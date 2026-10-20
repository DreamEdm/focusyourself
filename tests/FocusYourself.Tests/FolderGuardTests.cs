using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.Tests;

public class FolderGuardTests
{
    [Theory]
    [InlineData(@"D:\Games", @"D:\Games", true)]
    [InlineData(@"D:\Games\Steam\common", @"D:\Games", true)]
    [InlineData(@"d:\games\", @"D:\Games", true)]
    [InlineData(@"D:\GamesBackup", @"D:\Games", false)]
    [InlineData(@"D:\", @"D:\Games", false)]
    [InlineData(@"E:\Games", @"D:\Games", false)]
    public void Matches_folder_and_subfolders_only(string path, string root, bool expected) =>
        Assert.Equal(expected, FolderGuard.IsInside(path, root));

    [Theory]
    [InlineData(@"D:\", false)]
    [InlineData(@"C:\", false)]
    [InlineData("", false)]
    [InlineData(@"D:\Games", true)]
    [InlineData(@"C:\Users\me\Videos", true)]
    public void Whole_drives_cannot_be_restricted(string path, bool expected) =>
        Assert.Equal(expected, FolderGuard.CanGuard(path, out _));

    [Fact]
    public void Display_name_is_last_segment() => Assert.Equal("Videos", FolderGuard.DisplayName(@"D:\Downloads\Videos\"));

    /// <summary>
    /// 真实打开一个资源管理器窗口验证：进入受限文件夹会被退回上一级并触发事件，“继续访问”会导航回原位置。
    /// 会在桌面上弹出窗口，因此只在设置 FY_EXPLORER_TEST=1 时运行。
    /// </summary>
    [Fact]
    public async Task Explorer_window_is_sent_back_and_can_continue()
    {
        if (Environment.GetEnvironmentVariable("FY_EXPLORER_TEST") != "1") return;

        var root = Path.Combine(Path.GetTempPath(), "fy-folder-" + Guid.NewGuid().ToString("N")[..8]);
        var blocked = Path.Combine(root, "Blocked");
        var inner = Path.Combine(blocked, "Inner");
        Directory.CreateDirectory(inner);
        using var guard = new FolderGuard();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{inner}\"") { UseShellExecute = true });
            Assert.True(await WaitAsync(() => ExplorerAt(inner), TimeSpan.FromSeconds(10)), "资源管理器窗口没有打开");

            var hit = new TaskCompletionSource<FolderBlockedEventArgs>();
            guard.FolderBlocked += (_, e) => hit.TrySetResult(e);
            guard.Activate([new FolderRule { Path = blocked }]);

            var e = await hit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Blocked", e.Name);
            Assert.True(FolderGuard.IsInside(e.OpenedPath, inner) && FolderGuard.IsInside(inner, e.OpenedPath));
            Assert.True(await WaitAsync(() => ExplorerAt(root), TimeSpan.FromSeconds(5)), "没有退回上一级");

            e.Allow();
            Assert.True(guard.IsAllowed(blocked));
            Assert.True(await WaitAsync(() => ExplorerAt(inner), TimeSpan.FromSeconds(5)), "继续访问后没有回到原位置");

            await Task.Delay(1500); // 已放行：不应再被退回
            Assert.True(ExplorerAt(inner));

            guard.Deactivate();
            Assert.False(guard.IsAllowed(blocked)); // 下一轮专注重新生效
        }
        finally
        {
            guard.Deactivate();
            CloseExplorerUnder(root);
            await Task.Delay(500);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    static bool ExplorerAt(string path) => RunSta(() => ExplorerPaths().Any(p => FolderGuard.NormalizeFolder(p) == FolderGuard.NormalizeFolder(path)));

    static void CloseExplorerUnder(string root) => RunSta(() =>
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic windows = shell.Windows();
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            try
            {
                dynamic w = windows.Item(i);
                string? p = (string?)w?.Document?.Folder?.Self?.Path;
                if (p is not null && FolderGuard.IsInside(p, root)) w!.Quit();
            }
            catch (COMException) { }
        }
        return true;
    });

    static List<string> ExplorerPaths()
    {
        var result = new List<string>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic windows = shell.Windows();
        for (int i = 0; i < windows.Count; i++)
        {
            try
            {
                dynamic w = windows.Item(i);
                string? p = (string?)w?.Document?.Folder?.Self?.Path;
                if (p is not null) result.Add(p);
            }
            catch (COMException) { }
        }
        return result;
    }

    static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }
}
