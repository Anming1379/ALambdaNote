using System.Windows;
using System.Windows.Media;

using System.IO;

namespace ALambdaNote.Services;

/// <summary>
/// 深浅主题与主题色。所有界面颜色都写进 Application.Resources，
/// XAML 里用 DynamicResource 取，切主题时整窗即时换肤。
/// </summary>
public static class ThemeManager
{
    /// <summary>一组高辨识度、饱和度更明显的主题色。</summary>
    public sealed record AccentPreset(string Name, string Light, string Dark);

    public static readonly AccentPreset[] Accents =
    {
        new("苔绿", "#3F8F58", "#7DD495"),
        new("雾紫", "#7052CC", "#B9A2FF"),
        new("陶土", "#C4573A", "#F29274"),
        new("青碧", "#238C86", "#5FD4CC"),
        new("黛蓝", "#375CD8", "#7FA0FF"),
        new("绛紫", "#A63A86", "#E77DC8"),
        new("玫红", "#C2475D", "#FF8D9E"),
    };

    private static readonly Color LightBackground = Hex("#FCFBF8");
    private static readonly Color LightSurface = Hex("#F5F3EE");
    private static readonly Color LightSurfaceAlt = Hex("#EDEAE3");
    private static readonly Color LightBorder = Hex("#E5E1D8");
    private static readonly Color LightBorderStrong = Hex("#CDC7BB");
    private static readonly Color LightText = Hex("#4F4A41");
    private static readonly Color LightTextMuted = Hex("#7A756B");
    private static readonly Color LightCodeBg = Hex("#F3F0E9");
    private static readonly Color LightScrollThumb = Hex("#D8D2C7");
    private static readonly Color LightScrollThumbHover = Hex("#BDB6A8");

    private static readonly Color DarkBackground = Hex("#1A1C1E");
    private static readonly Color DarkSurface = Hex("#222528");
    private static readonly Color DarkSurfaceAlt = Hex("#2A2E32");
    private static readonly Color DarkBorder = Hex("#34383C");
    private static readonly Color DarkBorderStrong = Hex("#464B50");
    private static readonly Color DarkText = Hex("#E7E5E1");
    private static readonly Color DarkTextMuted = Hex("#A2A7AE");
    private static readonly Color DarkCodeBg = Hex("#1E2022");
    private static readonly Color DarkScrollThumb = Hex("#41454A");
    private static readonly Color DarkScrollThumbHover = Hex("#575C63");

    private static int _randomIndex = -1;

    public static bool IsDark { get; private set; }

    public static Color Accent { get; private set; } = Hex("#375CD8");

    public static AccentPreset CurrentAccent { get; private set; } = Accents[0];

    public static int CurrentAccentIndex { get; private set; }

    public static event EventHandler? Changed;

    public static bool SystemUsesDark()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(AppSettings settings, bool randomizeAccent = false)
    {
        IsDark = settings.ThemeMode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => SystemUsesDark()
        };

        if (settings.RandomAccent)
        {
            if (randomizeAccent || _randomIndex < 0)
            {
                _randomIndex = Random.Shared.Next(Accents.Length);
            }

            CurrentAccentIndex = _randomIndex;
        }
        else
        {
            CurrentAccentIndex = Math.Clamp(settings.AccentIndex, 0, Accents.Length - 1);
        }

        CurrentAccent = Accents[CurrentAccentIndex];
        Accent = Hex(IsDark ? CurrentAccent.Dark : CurrentAccent.Light);
        PushResources();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static SolidColorBrush Brush(string key)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private static void PushResources()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        if (IsDark)
        {
            SetColor(resources, "Bg", DarkBackground);
            SetColor(resources, "Surface", DarkSurface);
            SetColor(resources, "SurfaceAlt", DarkSurfaceAlt);
            SetColor(resources, "Border", DarkBorder);
            SetColor(resources, "BorderStrong", DarkBorderStrong);
            SetColor(resources, "Text", DarkText);
            SetColor(resources, "TextMuted", DarkTextMuted);
            SetColor(resources, "CodeBg", DarkCodeBg);
            SetColor(resources, "ScrollThumb", DarkScrollThumb);
            SetColor(resources, "ScrollThumbHover", DarkScrollThumbHover);
            SetColor(resources, "Hover", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            SetColor(resources, "Pressed", Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            SetColor(resources, "Overlay", Color.FromArgb(0xF0, DarkBackground.R, DarkBackground.G, DarkBackground.B));
        }
        else
        {
            SetColor(resources, "Bg", LightBackground);
            SetColor(resources, "Surface", LightSurface);
            SetColor(resources, "SurfaceAlt", LightSurfaceAlt);
            SetColor(resources, "Border", LightBorder);
            SetColor(resources, "BorderStrong", LightBorderStrong);
            SetColor(resources, "Text", LightText);
            SetColor(resources, "TextMuted", LightTextMuted);
            SetColor(resources, "CodeBg", LightCodeBg);
            SetColor(resources, "ScrollThumb", LightScrollThumb);
            SetColor(resources, "ScrollThumbHover", LightScrollThumbHover);
            SetColor(resources, "Hover", Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
            SetColor(resources, "Pressed", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            SetColor(resources, "Overlay", Color.FromArgb(0xF0, LightBackground.R, LightBackground.G, LightBackground.B));
        }

        SetColor(resources, "Accent", Accent);
        // 代码文字跟随主题色，避免出现与主题无关的固定棕色。
        SetColor(resources, "CodeText", Accent);
        SetColor(resources, "AccentSoft", WithAlpha(Accent, IsDark ? (byte)0x38 : (byte)0x26));
        SetColor(resources, "AccentLine", WithAlpha(Accent, 0x80));
        SetColor(resources, "Selection", WithAlpha(Accent, IsDark ? (byte)0x3C : (byte)0x2E));
        SetColor(resources, "OnAccent", Luminance(Accent) > 0.44 ? Hex("#17181A") : Colors.White);
        SetColor(resources, "Danger", Hex("#D04A45"));
    }

    private static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public static Color Hex(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }
}
