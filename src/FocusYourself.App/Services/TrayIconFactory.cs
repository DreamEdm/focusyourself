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
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size / 32f;

        // 果实：左上偏亮的径向渐变（与 tools/make_icons.py 的 exe 图标一致）
        var main = PhaseVisuals.DrawingColor(phase, paused);
        using (var body = ToPath(TomatoShape.BodyData, s))
        using (var brush = new PathGradientBrush(body))
        {
            brush.CenterPoint = new PointF(11.5f * s, 15 * s);
            brush.CenterColor = Mix(main, Color.White, 0.22);
            brush.SurroundColors = [Mix(main, Color.Black, 0.22)];
            g.FillPath(brush, body);
        }

        // 高光
        using (var hl = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
            g.FillEllipse(hl, 6.6f * s, 12.4f * s, 6.6f * s, 4.8f * s);

        // 叶子 + 果柄
        var leaf = phase is Phase.ShortBreak ? Color.FromArgb(0x2E, 0x8F, 0x62) : Color.FromArgb(0x4C, 0xC3, 0x8A);
        using (var calyx = ToPath(TomatoShape.CalyxData + " " + TomatoShape.StemData, s))
        using (var lb = new LinearGradientBrush(new PointF(0, 2.5f * s), new PointF(0, 14 * s), Mix(leaf, Color.White, 0.18), Mix(leaf, Color.Black, 0.2)))
            g.FillPath(lb, calyx);

        if (paused && phase != Phase.Idle)
        {
            using var bars = new SolidBrush(Color.White);
            g.FillRectangle(bars, 11.5f * s, 14 * s, 3.5f * s, 11 * s);
            g.FillRectangle(bars, 17 * s, 14 * s, 3.5f * s, 11 * s);
        }
        return bmp;
    }

    static GraphicsPath ToPath(string data, float scale)
    {
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var figure in TomatoShape.Flatten(data, scale))
        {
            path.StartFigure();
            path.AddPolygon(figure);
        }
        return path;
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

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
