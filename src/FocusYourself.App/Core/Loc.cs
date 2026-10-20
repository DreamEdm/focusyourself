using System.ComponentModel;
using System.Globalization;

namespace FocusYourself.Core;

/// <summary>
/// 界面文字。XAML 里用 {l:T Key} 绑定到 <see cref="Instance"/> 的索引器，切换语言时所有绑定自动刷新；
/// 代码里用 <see cref="T"/> / <see cref="F"/>。文字定义在 <see cref="Strings"/>。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";
    public const string Chinese = "zh-CN";

    public static Loc Instance { get; } = new();

    Loc() { }

    public string Language { get; private set; } = English;
    public bool IsChinese => Language == Chinese;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言切换后触发，供需要重新生成文字的视图模型使用。</summary>
    public static event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    /// <summary>系统界面语言是中文（含繁体）时用简体中文，其余一律英文。</summary>
    public static string SystemDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? Chinese : English;

    public static string Normalize(string? lang) => lang == Chinese ? Chinese : English;

    public void SetLanguage(string? lang)
    {
        var normalized = Normalize(lang);
        if (normalized == Language) return;
        Language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChinese)));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    string Get(string key)
    {
        var table = IsChinese ? Strings.Zh : Strings.En;
        if (table.TryGetValue(key, out var value)) return value;
        return Strings.En.TryGetValue(key, out var en) ? en : key;
    }
}
