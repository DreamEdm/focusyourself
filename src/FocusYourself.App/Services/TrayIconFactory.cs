using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FocusYourself.Core;
using FocusYourself.Views;

namespace FocusYourself.Services;

/// <summary>运行时绘制番茄图标，颜色随阶段变化（专注红、休息绿、长休息蓝、空闲灰）。</summary>
public static class TrayIconFactory
{
    /// <param name="paused">暂停时番茄变成琥珀色，中间画两道暂停竖线。</param>
    public static Bitmap DrawBitmap(Phase phase, int size = 64, bool paused = false)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size / 32f;

        var body = new RectangleF(3 * s, 6 * s, 26 * s, 24 * s);
        var main = PhaseVisuals.DrawingColor(phase, paused);
        var dark = Color.FromArgb(main.A, (int)(main.R * 0.78), (int)(main.G * 0.78), (int)(main.B * 0.78));
        using (var brush = new LinearGradientBrush(body, main, dark, 60f))
            g.FillEllipse(brush, body);

        // 高光
        using (var hl = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            g.FillEllipse(hl, 8 * s, 11 * s, 7 * s, 5 * s);

        // 叶子
        var leaf = Color.FromArgb(0x4C, 0xC3, 0x8A);
        if (phase is Phase.ShortBreak) leaf = Color.FromArgb(0x2E, 0x8F, 0x62);
        using (var lb = new SolidBrush(leaf))
        {
            PointF[] pts =
            [
                new(16 * s, 2 * s), new(13 * s, 7 * s), new(8 * s, 7 * s), new(12.5f * s, 9.5f * s),
                new(16 * s, 8.5f * s), new(19.5f * s, 9.5f * s), new(24 * s, 7 * s), new(19 * s, 7 * s),
            ];
            g.FillPolygon(lb, pts);
        }

        if (paused && phase != Phase.Idle)
        {
            using var bars = new SolidBrush(Color.White);
            g.FillRectangle(bars, 11.5f * s, 12 * s, 3.5f * s, 13 * s);
            g.FillRectangle(bars, 17 * s, 12 * s, 3.5f * s, 13 * s);
        }
        return bmp;
    }

    public static Icon CreateIcon(Phase phase, bool paused = false)
    {
        using var bmp = DrawBitmap(phase, 32, paused);
        var h = bmp.GetHicon();
        try
        {
            // Icon.FromHandle 不拥有句柄，克隆一份后销毁原句柄，避免泄漏
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    public static BitmapSource CreateImageSource(Phase phase, bool paused = false)
    {
        using var bmp = DrawBitmap(phase, 64, paused);
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);
}
