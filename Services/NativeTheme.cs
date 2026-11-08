using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ALambdaNote.Services;

/// <summary>
/// 让原生标题栏跟随应用主题：
/// 深浅模式标志 + Windows 11 的标题栏底色、边框色与文字色。
/// </summary>
public static class NativeTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = ThemeManager.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        // 这三个颜色属性是 Windows 11 才支持的；Windows 10 上只保留
        // 深色标题栏标志，避免旧 DWM 对未知属性做出异常处理。
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            SetColor(handle, DwmwaCaptionColor, ThemeManager.Brush("Surface"));
            SetColor(handle, DwmwaBorderColor, ThemeManager.Brush("Border"));
            SetColor(handle, DwmwaTextColor, ThemeManager.Brush("Text"));
        }
    }

    private static void SetColor(IntPtr handle, int attribute, Brush brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return;
        }

        var color = solid.Color;
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
