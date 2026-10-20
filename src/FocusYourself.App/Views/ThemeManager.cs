using System.Windows;
using Microsoft.Win32;

namespace FocusYourself.Views;

/// <summary>
/// 在运行时替换配色字典，实现亮色/暗色/自动切换。
/// <see cref="Mode"/> 是用户选择（dark / light / auto），<see cref="Current"/> 是实际生效的配色（dark / light）。
/// 自动模式跟随 Windows“应用模式”，系统切换时立即生效。
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string Auto = "auto";

    const string DarkSource = "pack://application:,,,/FocusYourself;component/Themes/Colors.Dark.xaml";
    const string LightSource = "pack://application:,,,/FocusYourself;component/Themes/Colors.Light.xaml";

    /// <summary>用户选择的模式：dark / light / auto。</summary>
    public static string Mode { get; private set; } = Auto;
    /// <summary>实际生效的配色：dark / light。</summary>
    public static string Current { get; private set; } = Dark;
    public static bool IsDark => Current == Dark;
    public static bool IsAuto => Mode == Auto;

    /// <summary>模式或实际配色变化时触发（总在 UI 线程上）。</summary>
    public static event EventHandler? ThemeChanged;

    static Application? _app;
    static bool _listening;

    /// <summary>未知值（含旧配置里的空值）一律当作自动。</summary>
    public static string NormalizeMode(string? mode) => mode is Dark or Light ? mode : Auto;

    /// <summary>亮 → 暗 → 自动 → 亮 …</summary>
    public static string NextMode(string mode) => NormalizeMode(mode) switch
    {
        Light => Dark,
        Dark => Auto,
        _ => Light,
    };

    /// <summary>读取 Windows“应用模式”的亮暗设置。</summary>
    public static string SystemDefault()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1 ? Light : Dark;
    }

    public static string Resolve(string mode) => mode == Auto ? SystemDefault() : mode;

    public static void Apply(string? mode, Application? app = null)
    {
        app ??= Application.Current;
        _app = app;
        var nextMode = NormalizeMode(mode);
        var modeChanged = Mode != nextMode;
        Mode = nextMode;
        if (Mode == Auto) StartListening();
        if (!ApplyColors(app, Resolve(Mode)) && modeChanged) ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>替换配色字典；配色有变化时触发 <see cref="ThemeChanged"/> 并返回 true。</summary>
    static bool ApplyColors(Application app, string theme)
    {
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString is { } s && (s.Contains("Colors.Dark.xaml") || s.Contains("Colors.Light.xaml")));
        var changed = Current != theme;
        if (existing is null || changed)
        {
            var next = new ResourceDictionary { Source = new Uri(theme == Light ? LightSource : DarkSource) };
            if (existing is null) dictionaries.Insert(0, next);
            else dictionaries[dictionaries.IndexOf(existing)] = next;
        }

        Current = theme;
        if (changed) ThemeChanged?.Invoke(null, EventArgs.Empty);
        return changed;
    }

    static void StartListening()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // 切换系统亮暗时 Windows 发送 General 类别（ImmersiveColorSet）
        if (e.Category != UserPreferenceCategory.General) return;
        var app = _app;
        app?.Dispatcher.BeginInvoke(() =>
        {
            if (Mode == Auto) ApplyColors(app, SystemDefault());
        });
    }
}
