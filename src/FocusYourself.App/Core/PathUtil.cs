using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace FocusYourself.Core;

public static partial class PathUtil
{
    /// <summary>规范化 exe 路径用于比较：完整路径、小写。</summary>
    public static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    public static bool SamePath(string a, string b) => Normalize(a) == Normalize(b);

    public static string FileName(string path) => System.IO.Path.GetFileName(path).ToLowerInvariant();

    /// <summary>不带扩展名的小写文件名，和 Process.ProcessName 对应。</summary>
    public static string ProcessName(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    /// <summary>取程序的友好名称：文件描述 → 产品名 → 文件名。</summary>
    public static string FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>解析注册表 DisplayIcon 这类 "C:\a\b.exe",0 形式的值。</summary>
    public static string? ParseIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = IconPathRegex().Match(value.Trim());
        if (!m.Success) return null;
        var path = m.Groups["p"].Value.Trim();
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    [GeneratedRegex("""^"?(?<p>[^",]+?)"?(\s*,\s*-?\d+)?$""")]
    private static partial Regex IconPathRegex();
}
