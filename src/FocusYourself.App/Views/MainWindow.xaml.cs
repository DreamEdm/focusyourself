using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FocusYourself.ViewModels;

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
        DataContextChanged += OnDataContextChanged;
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    // ================= 圆环中间的时长编辑 =================
    // 不用数据绑定：编辑时框里只有分钟数，和平时显示的 mm:ss 不是同一个字符串，由这里手动同步。

    bool _editingTime;

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is MainViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            TimeEditor.Text = vm.TimeText;
        }
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TimeText) && !_editingTime && Vm is { } vm) TimeEditor.Text = vm.TimeText;
    }

    /// <summary>第一次点击只进入编辑并全选，不把光标定位到点击处，直接输入就能覆盖。</summary>
    void OnTimeEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TimeEditor.IsKeyboardFocusWithin) return;
        TimeEditor.Focus();
        e.Handled = true;
    }

    void OnTimeEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _editingTime = true;
        TimeEditor.Text = vm.FocusMinutes.ToString();
        TimeEditor.SelectAll();
    }

    void OnTimeEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndTimeEdit(commit: true);

    void EndTimeEdit(bool commit)
    {
        if (!_editingTime || Vm is not { } vm) return;
        _editingTime = false;
        if (commit && int.TryParse(TimeEditor.Text.Trim(), out var minutes)) vm.SetFocusMinutes(minutes);
        TimeEditor.Text = vm.TimeText;
    }

    void OnTimeEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Escape:
                EndTimeEdit(commit: e.Key == Key.Enter);
                Keyboard.ClearFocus();
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                StepEditingMinutes(e.Key == Key.Up ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Space:
                e.Handled = true; // 空格不是数字，PreviewTextInput 拦不住它
                break;
        }
    }

    void OnTimeEditorTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    /// <summary>滚轮：编辑中改框里的数字，没在编辑就直接改时长。</summary>
    void OnTimeEditorWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || e.Delta == 0) return;
        var step = e.Delta > 0 ? 1 : -1;
        if (_editingTime) StepEditingMinutes(step);
        else vm.SetFocusMinutes(vm.FocusMinutes + step);
        e.Handled = true;
    }

    void StepEditingMinutes(int step)
    {
        if (Vm is not { } vm) return;
        var current = int.TryParse(TimeEditor.Text.Trim(), out var m) ? m : vm.FocusMinutes;
        TimeEditor.Text = MainViewModel.ClampFocusMinutes(current + step).ToString();
        TimeEditor.SelectAll();
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
