using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FocusYourself.Services;

namespace FocusYourself.Views;

public partial class ToastWindow : Window
{
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    bool _closing;

    public ToastWindow(ToastKind kind, string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        var (brushKey, glyph) = kind switch
        {
            ToastKind.Focus => ("FocusBrush", "\uE916"),   // 秒表
            ToastKind.Break => ("BreakBrush", "\uE706"),   // 太阳
            ToastKind.Warning => ("WarnBrush", "\uE7BA"),  // 警告
            _ => ("LongBreakBrush", "\uE946"),             // 信息
        };
        var brush = (Brush)FindResource(brushKey);
        Accent.Background = brush;
        IconBg.Fill = brush;
        IconGlyph.Foreground = brush;
        IconGlyph.Text = glyph;
        Life.Background = brush;

        SourceInitialized += (_, _) => Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => Animate();
    }

    public event EventHandler? Clicked;

    void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        Life.Width = Card.ActualWidth - 32;
        var life = new DoubleAnimation(Life.Width, 0, Lifetime);
        life.Completed += (_, _) => Dismiss();
        Life.BeginAnimation(WidthProperty, life);
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(220)));
    }

    void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    void OnCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Button>(d) is not null) return;
        Clicked?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}

/// <summary>在屏幕右下角（任务栏上方）堆叠显示通知卡片，最新的在最下面。</summary>
public static class ToastManager
{
    const int MaxToasts = 4;
    static readonly List<ToastWindow> Open = [];

    public static event EventHandler? ToastClicked;

    public static void Show(ToastKind kind, string title, string message)
    {
        var toast = new ToastWindow(kind, title, message);
        toast.Clicked += (_, _) => ToastClicked?.Invoke(null, EventArgs.Empty);
        toast.Closed += (_, _) =>
        {
            Open.Remove(toast);
            Reposition();
        };

        var area = SystemParameters.WorkArea;
        toast.Left = area.Right - toast.Width;
        toast.Top = area.Bottom; // 先放到屏幕外，测量出高度后再排列
        Open.Add(toast);
        toast.Show();
        toast.UpdateLayout();
        Reposition();

        while (Open.Count > MaxToasts) Open[0].Dismiss();
    }

    static void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double y = area.Bottom;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            y -= t.ActualHeight;
            t.Left = area.Right - t.Width;
            t.Top = y;
        }
    }
}
