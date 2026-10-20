using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusYourself.Core;

public sealed class ConfigStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _configPath;
    readonly string _statePath;

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusYourself");
        System.IO.Directory.CreateDirectory(Directory);
        _configPath = System.IO.Path.Combine(Directory, "config.json");
        _statePath = System.IO.Path.Combine(Directory, "state.json");
    }

    public string Directory { get; }

    public AppConfig LoadConfig() => Load<AppConfig>(_configPath) ?? new AppConfig();

    public void SaveConfig(AppConfig config) => Save(_configPath, config);

    public FocusState LoadState() => Load<FocusState>(_statePath) ?? new FocusState();

    public void SaveState(FocusState state) => Save(_statePath, state);

    static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 配置损坏时保留一份备份，然后用默认值启动
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { }
            return null;
        }
    }

    static void Save<T>(string path, T value)
    {
        // 先写临时文件再替换，避免写到一半崩溃导致配置损坏
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
