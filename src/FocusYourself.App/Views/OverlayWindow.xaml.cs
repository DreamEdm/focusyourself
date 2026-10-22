using System.Windows;
using System.Windows.Interop;
using FocusYourself.Core;
using FocusYourself.Services;

namespace FocusYourself.Views;

/// <summary>置顶、半透明、点击穿透的悬浮倒计时。</summary>
public partial class OverlayWindow : Window
{
    const double EdgeMargin = 8;
    OverlayCorner _corner = OverlayCorner.TopRight;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => PlaceInCorner();
    }

    public void Update(Phase phase, bool paused, TimeSpan remaining, double progress, OverlayCorner corner, double opacity)
    {
        var brush = PhaseVisuals.Brush(phase, paused);
        Dot.Fill = brush;
        Bar.Background = brush;
        PhaseText.Text = PhaseVisuals.ShortLabel(phase, paused);
        TimeText.Text = PhaseVisuals.Format(remaining);
        Opacity = Math.Clamp(opacity, 0.2, 1);
        if (ActualWidth > 0) Bar.Width = Math.Max(0, (((FrameworkElement)Bar.Parent).ActualWidth) * progress);

        if (corner != _corner)
        {
            _corner = corner;
            PlaceInCorner();
        }
    }

    void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? area.Left + EdgeMargin : area.Right - ActualWidth - EdgeMargin;
        Top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? area.Top + EdgeMargin : area.Bottom - ActualHeight - EdgeMargin;
    }
}
