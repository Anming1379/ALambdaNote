using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALambdaNote.Services;

/// <summary>设置持久化：%APPDATA%\ALambdaNote\settings.json</summary>
public static class SettingsStore
{
    private const int CurrentVersion = 3;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ALambdaNote");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings { SettingsVersion = CurrentVersion };
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            var version = settings.SettingsVersion ?? 0;
            if (version < CurrentVersion)
            {
                // 默认字号从 15 / 16 迁到 17。旧配置没有版本号，或者停在 v2；
                // 这是最后一次默认值迁移，之后用户手动选择的字号会保留。
                if (Math.Abs(settings.EditorFontSize - 15) < 0.01
                    || Math.Abs(settings.EditorFontSize - 16) < 0.01)
                {
                    settings.EditorFontSize = 17;
                }

                // 旧版第 4 个主题色是芥黄，删除这个预设后需要修正索引。
                if (settings.AccentIndex >= 5)
                {
                    settings.AccentIndex--;
                }
                else if (settings.AccentIndex == 4)
                {
                    settings.AccentIndex = 3;
                }

                settings.SettingsVersion = CurrentVersion;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            settings.SettingsVersion ??= CurrentVersion;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 设置写不进去不应该影响正常编辑。
        }
    }
}
