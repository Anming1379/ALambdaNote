using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ALambdaNote.Services;
using ALambdaNote.Views;

namespace ALambdaNote;

public partial class App : Application
{
    /// <summary>本次运行使用的设置对象，主窗口与设置窗口共享同一份。</summary>
    public static AppSettings Settings { get; private set; } = new();

    private DispatcherTimer? _themeTimer;
    private bool _lastSystemDark;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Settings = SettingsStore.Load();
        ThemeManager.Apply(Settings, randomizeAccent: true);

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ALambdaNote",
                    "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
            }
            catch
            {
                // 忽略日志失败。
            }

            if (MainWindow is { WindowState: WindowState.Minimized } minimized)
            {
                minimized.WindowState = WindowState.Normal;
                minimized.Activate();
            }

            FlatDialog.Info(MainWindow, "出了点小问题", args.Exception.Message);
            args.Handled = true;
        };

        _lastSystemDark = ThemeManager.SystemUsesDark();
        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _themeTimer.Tick += (_, _) => WatchSystemTheme();
        _themeTimer.Start();

        var file = e.Args.FirstOrDefault(IsFileArgument);
        var window = new MainWindow(Settings, file, openedExternally: file is not null);
        MainWindow = window;
        window.Show();
    }

    private void WatchSystemTheme()
    {
        if (Settings.ThemeMode != AppThemeMode.Auto)
        {
            return;
        }

        var dark = ThemeManager.SystemUsesDark();
        if (dark == _lastSystemDark)
        {
            return;
        }

        _lastSystemDark = dark;
        ThemeManager.Apply(Settings);
    }

    private static bool IsFileArgument(string arg)
    {
        return !string.IsNullOrWhiteSpace(arg)
               && !arg.StartsWith("-", StringComparison.Ordinal)
               && File.Exists(arg);
    }
}
