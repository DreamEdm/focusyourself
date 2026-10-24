using System.Windows.Media;

namespace FocusYourself.Views;

/// <summary>
/// 番茄造型的轮廓（32x32 坐标系）。托盘图标、侧边栏 logo、番茄进度和 tools/make_icons.py 共用同一套路径，
/// 三个字符串必须与 make_icons.py 里的 BODY / CALYX / STEM 逐字相同（有单元测试检查）。
/// </summary>
public static class TomatoShape
{
    public const string BodyData = "M 16,9.6 C 19.2,7.6 24.6,7.6 27.6,11 C 30.6,14.4 30.4,21.4 27.4,25.4 C 24.8,28.8 20.6,30.4 16,30.4 C 11.4,30.4 7.2,28.8 4.6,25.4 C 1.6,21.4 1.4,14.4 4.4,11 C 7.4,7.6 12.8,7.6 16,9.6 Z";
    public const string CalyxData = "M 16,7.4 C 13.4,5.6 9.8,5.8 6.6,8.4 C 9.4,9 11.6,9.4 13,9.8 C 11.8,11 10.8,12.6 10.6,14.2 C 12.8,13.2 14.6,11.8 16,10.6 C 17.4,11.8 19.2,13.2 21.4,14.2 C 21.2,12.6 20.2,11 19,9.8 C 20.4,9.4 22.6,9 25.4,8.4 C 22.2,5.8 18.6,5.6 16,7.4 Z";
    public const string StemData = "M 15.2,8.4 C 15,6.2 15.6,4.2 17,2.8 C 17.5,2.3 18.4,2.5 18.6,3.2 C 18.7,3.6 18.5,4 18.2,4.3 C 17.2,5.4 16.8,6.8 16.9,8.4 Z";

    public static Geometry Body { get; } = Parse(BodyData);
    /// <summary>叶子和果柄合在一起。</summary>
    public static Geometry Leaf { get; } = Parse(CalyxData + " " + StemData);

    static Geometry Parse(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>把路径压平成折线，按 scale 缩放，给 GDI+ 绘图用。</summary>
    public static System.Drawing.PointF[][] Flatten(string data, float scale)
    {
        var flat = Geometry.Parse(data).GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
        return flat.Figures.Select(f =>
        {
            var pts = new List<System.Drawing.PointF> { new((float)f.StartPoint.X * scale, (float)f.StartPoint.Y * scale) };
            foreach (var seg in f.Segments)
            {
                IEnumerable<System.Windows.Point> points = seg switch
                {
                    PolyLineSegment p => p.Points,
                    LineSegment l => [l.Point],
                    _ => [],
                };
                pts.AddRange(points.Select(p => new System.Drawing.PointF((float)p.X * scale, (float)p.Y * scale)));
            }
            return pts.ToArray();
        }).ToArray();
    }
}
