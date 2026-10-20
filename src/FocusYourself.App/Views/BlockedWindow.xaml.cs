using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusYourself.Services;

using FocusYourself.Core;

namespace FocusYourself.Views;

/// <summary>剩余时间信息：是否在专注期、剩余时长、已完成比例。</summary>
public readonly record struct FocusClock(bool IsFocus, TimeSpan Remaining, double Progress);

/// <summary>被拦截程序的提示窗。同一时间只保留一个，再次拦截时更新内容。</summary>
public partial class BlockedWindow : Window
{
    static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(20);
    static BlockedWindow? _current;

    readonly Func<FocusClock> _clock;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _closing;
    Action? _onContinue;
    int _continueCountdown;

    BlockedWindow(Func<FocusClock> clock)
    {
        InitializeComponent();
        _clock = clock;
        _timer.Tick += (_, _) =>
        {
            TickContinue();
            Refresh();
        };
        Loaded += (_, _) => PlayIntro();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current == this) _current = null;
        };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) Dismiss(); };
    }

    /// <summary>在应用内显示（标准/强力模式拦截后）。</summary>
    public static void ShowFor(string appName, string appPath, Func<FocusClock> clock)
    {
        var w = _current ?? new BlockedWindow(clock);
        _current = w;
        w.SetApp(appName, appPath);
        if (!w.IsVisible) w.Show();
        else w.RestartLife();
        w.Activate();
    }

    /// <summary>
    /// 进入受限文件夹时显示：询问是否继续访问。
    /// “继续访问”要等 <paramref name="delaySeconds"/> 秒后才能点，点击后调用 <paramref name="onContinue"/>。
    /// </summary>
    public static void ShowFolder(string folderName, string openedPath, Func<FocusClock> clock, int delaySeconds, Action onContinue)
    {
        var w = _current ?? new BlockedWindow(clock);
        _current = w;
        w.SetFolder(folderName, openedPath, delaySeconds, onContinue);
        if (!w.IsVisible) w.Show();
        else w.RestartLife();
        w.Activate();
    }

    /// <summary>IFEO 拦截时由独立进程显示，关闭后进程退出。</summary>
    public static BlockedWindow CreateStandalone(string appName, string appPath, Func<FocusClock> clock)
    {
        var w = new BlockedWindow(clock);
        w.ShowInTaskbar = true;
        w.SetApp(appName, appPath);
        return w;
    }

    void SetFolder(string folderName, string openedPath, int delaySeconds, Action onContinue)
    {
        AppIcon.Source = null;
        FallbackGlyph.Text = "\uE8B7"; // 文件夹
        FallbackGlyph.Visibility = Visibility.Visible;
        QuoteText.Text = $"“{PhaseVisuals.RandomQuote()}”";

        TitleText.Text = Loc.T("FolderBlockedTitle");
        MessageText.Text = Loc.F("FolderBlockedBody", folderName, openedPath);
        TimePill.Visibility = Visibility.Visible;
        QuestionText.Visibility = Visibility.Visible;
        OpenAppButton.Visibility = Visibility.Collapsed;
        OkButton.Content = Loc.T("BackToFocus");

        _onContinue = onContinue;
        _continueCountdown = Math.Max(0, delaySeconds);
        ContinueButton.Visibility = Visibility.Visible;
        UpdateContinueButton();
        Refresh();
    }

    void TickContinue()
    {
        if (_onContinue is null || _continueCountdown <= 0) return;
        _continueCountdown--;
        UpdateContinueButton();
    }

    void UpdateContinueButton()
    {
        ContinueButton.IsEnabled = _continueCountdown <= 0;
        ContinueButton.Content = _continueCountdown > 0 ? Loc.F("ContinueCountdown", _continueCountdown) : Loc.T("Continue");
    }

    void SetApp(string appName, string appPath)
    {
        // 从文件夹模式切回程序模式
        _onContinue = null;
        ContinueButton.Visibility = Visibility.Collapsed;
        QuestionText.Visibility = Visibility.Collapsed;
        FallbackGlyph.Text = "\uE916";

        var icon = string.IsNullOrEmpty(appPath) ? null : AppScanner.LoadIcon(appPath);
        AppIcon.Source = icon;
        FallbackGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        QuoteText.Text = $"“{PhaseVisuals.RandomQuote()}”";

        if (_clock().IsFocus)
        {
            TitleText.Text = Loc.T("AppBlockedTitle");
            MessageText.Text = Loc.F("AppBlockedBody", appName);
            TimePill.Visibility = Visibility.Visible;
            OpenAppButton.Visibility = Visibility.Collapsed;
            OkButton.Content = Loc.T("OkBackToFocus");
        }
        else
        {
            TitleText.Text = Loc.T("FocusOverTitle");
            MessageText.Text = Loc.F("StrongResidueBody", appName);
            TimePill.Visibility = Visibility.Collapsed;
            OpenAppButton.Visibility = Visibility.Visible;
            OkButton.Content = Loc.T("GotIt");
        }
        Refresh();
    }

    void Refresh()
    {
        var c = _clock();
        TimeText.Text = PhaseVisuals.Format(c.Remaining);
        Ring.Progress = c.IsFocus ? c.Progress : 0;
    }

    void PlayIntro()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });

        // 图标轻微“呼吸”
        var breath = new DoubleAnimation(1, 1.06, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var st = (ScaleTransform)Breath.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, breath);

        _timer.Start();
        RestartLife();
    }

    void RestartLife()
    {
        var full = Card.ActualWidth - 52;
        var life = new DoubleAnimation(full, 0, AutoClose);
        life.Completed += (_, _) => Dismiss();
        Life.BeginAnimation(WidthProperty, life);
    }

    void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(200)));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(200)));
    }

    void OnOk(object sender, RoutedEventArgs e) => Dismiss();

    void OnContinue(object sender, RoutedEventArgs e)
    {
        var action = _onContinue;
        _onContinue = null;
        Dismiss();
        action?.Invoke();
    }

    void OnOpenApp(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception) { }
        Dismiss();
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            DragMove();
    }
}
