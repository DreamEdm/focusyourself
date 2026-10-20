using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusYourself.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseRoundedCorners();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        // WindowChrome 最大化时窗口会超出屏幕边缘一圈，补一个边距
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    /// <summary>为 true 时真正关闭，否则点击关闭只是隐藏到托盘。</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UseRoundedCorners()
    {
        // Windows 11：DWMWA_WINDOW_CORNER_PREFERENCE = 33，DWMWCP_ROUND = 2；旧系统上调用无效果
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        ApplyDarkFrame();
    }

    void OnThemeChanged(object? sender, EventArgs e) => ApplyDarkFrame();

    /// <summary>让系统绘制的边框/阴影跟随当前主题。</summary>
    void ApplyDarkFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDark ? 1 : 0; // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }
}
