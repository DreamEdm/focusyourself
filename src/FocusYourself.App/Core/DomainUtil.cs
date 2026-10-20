using System.Text.RegularExpressions;

namespace FocusYourself.Core;

public static partial class DomainUtil
{
    /// <summary>
    /// 把用户输入（可能是完整网址）规范成裸域名，如 "https://www.bilibili.com/video/xx" → "bilibili.com"。
    /// 无效输入返回 null。
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var end = s.IndexOfAny(['/', '?', '#']);
        if (end >= 0) s = s[..end];

        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];

        s = s.Trim('.');
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];

        return DomainRegex().IsMatch(s) ? s : null;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex DomainRegex();
}
