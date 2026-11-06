using System.IO;

namespace ALambdaNote.Services;

/// <summary>外观主题模式。</summary>
public enum AppThemeMode
{
    Auto,
    Light,
    Dark
}

/// <summary>应用设置。字段保持简单，方便用 System.Text.Json 持久化。</summary>
public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Auto;

    /// <summary>启动时是否从预设主题色中随机挑一个。</summary>
    public bool RandomAccent { get; set; } = true;

    /// <summary>固定主题色时使用的预设索引。</summary>
    public int AccentIndex { get; set; }

    /// <summary>记住窗口宽度，不记住位置，每次打开都居中。</summary>
    public double WindowWidth { get; set; } = 640;

    public double WindowHeight { get; set; } = 840;

    public double EditorFontSize { get; set; } = 17;

    /// <summary>设置结构版本。旧版本没有这个字段，用来做一次默认字号迁移。</summary>
    public int? SettingsVersion { get; set; }

    /// <summary>新建文件的默认保存目录，为空时使用 文档\ALambda Note。</summary>
    public string? DefaultSaveFolder { get; set; }

    public string ResolveSaveFolder()
    {
        if (!string.IsNullOrWhiteSpace(DefaultSaveFolder))
        {
            return DefaultSaveFolder.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ALambda Note");
    }
}
