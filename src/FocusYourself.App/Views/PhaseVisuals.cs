using System.Windows;
using System.Windows.Media;
using FocusYourself.Core;

namespace FocusYourself.Views;

/// <summary>各阶段统一的文字和配色。</summary>
public static class PhaseVisuals
{
    public static string Label(Phase phase) => Loc.T(phase switch
    {
        Phase.Focus => "PhaseFocus",
        Phase.ShortBreak => "PhaseShortBreak",
        Phase.LongBreak => "PhaseLongBreak",
        _ => "PhaseIdle",
    });

    public static string ShortLabel(Phase phase) => Loc.T(phase switch
    {
        Phase.Focus => "PhaseFocusShort",
        Phase.ShortBreak => "PhaseShortBreakShort",
        Phase.LongBreak => "PhaseLongBreakShort",
        _ => "PhaseIdleShort",
    });

    public static Brush Brush(Phase phase) => (Brush)Application.Current.FindResource(phase switch
    {
        Phase.Focus => "FocusBrush",
        Phase.ShortBreak => "BreakBrush",
        Phase.LongBreak => "LongBreakBrush",
        _ => "TextFaintBrush",
    });

    public static System.Drawing.Color DrawingColor(Phase phase) => phase switch
    {
        Phase.Focus => System.Drawing.Color.FromArgb(0xFF, 0x6B, 0x5B),
        Phase.ShortBreak => System.Drawing.Color.FromArgb(0x4C, 0xC3, 0x8A),
        Phase.LongBreak => System.Drawing.Color.FromArgb(0x5B, 0x9C, 0xFF),
        _ => System.Drawing.Color.FromArgb(0x9A, 0xA0, 0xAA),
    };

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    public static string RandomQuote()
    {
        var quotes = Loc.Instance.IsChinese ? Strings.QuotesZh : Strings.QuotesEn;
        return quotes[Random.Shared.Next(quotes.Length)];
    }
}
