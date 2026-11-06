using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ALambdaNote.Services;
using Microsoft.Win32;

namespace ALambdaNote.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _source;
    private readonly AppSettings _working;
    private readonly List<(ToggleButton Button, int Index)> _swatches = new();

    private ToggleButton? _randomButton;
    private int _fontSize;
    private bool _saved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _source = settings;
        _working = Clone(settings);
        _fontSize = Math.Clamp((int)Math.Round(settings.EditorFontSize), 11, 30);

        FolderBox.Text = _working.ResolveSaveFolder();
        FontSizeText.Text = _fontSize.ToString();

        BuildAccentSwatches();
        RefreshThemeChecks();
        RefreshAccentColors();
        RefreshAccentChecks();

        SourceInitialized += (_, _) => NativeTheme.Apply(this);
        Loaded += (_, _) => NativeTheme.Apply(this);
        ThemeManager.Changed += ThemeManager_Changed;
        Closed += SettingsWindow_Closed;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private static AppSettings Clone(AppSettings settings)
    {
        return new AppSettings
        {
            ThemeMode = settings.ThemeMode,
            RandomAccent = settings.RandomAccent,
            AccentIndex = settings.AccentIndex,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            EditorFontSize = settings.EditorFontSize,
            SettingsVersion = settings.SettingsVersion,
            DefaultSaveFolder = settings.DefaultSaveFolder
        };
    }

    private void BuildAccentSwatches()
    {
        _randomButton = new ToggleButton
        {
            Style = (Style)FindResource("ChoiceToggle"),
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "每次启动随机一个主题色",
            Content = BuildIconLabel("\uE043", "随机")
        };
        _randomButton.Click += (_, _) =>
        {
            _working.RandomAccent = true;
            ThemeManager.Apply(_working, randomizeAccent: true);
            RefreshAccentChecks();
        };
        AccentPanel.Children.Add(_randomButton);

        for (var index = 0; index < ThemeManager.Accents.Length; index++)
        {
            var preset = ThemeManager.Accents[index];
            var swatch = new ToggleButton
            {
                Style = (Style)FindResource("SwatchToggle"),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = preset.Name
            };

            var captured = index;
            swatch.Click += (_, _) =>
            {
                _working.RandomAccent = false;
                _working.AccentIndex = captured;
                ThemeManager.Apply(_working);
                RefreshAccentChecks();
            };

            AccentPanel.Children.Add(swatch);
            _swatches.Add((swatch, index));
        }
    }

    private static FrameworkElement BuildIconLabel(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private void RefreshThemeChecks()
    {
        ThemeAutoToggle.IsChecked = _working.ThemeMode == AppThemeMode.Auto;
        ThemeLightToggle.IsChecked = _working.ThemeMode == AppThemeMode.Light;
        ThemeDarkToggle.IsChecked = _working.ThemeMode == AppThemeMode.Dark;
    }

    private void RefreshAccentColors()
    {
        foreach (var (button, index) in _swatches)
        {
            var preset = ThemeManager.Accents[index];
            var brush = new SolidColorBrush(ThemeManager.Hex(ThemeManager.IsDark ? preset.Dark : preset.Light));
            brush.Freeze();
            button.Background = brush;
        }
    }

    private void RefreshAccentChecks()
    {
        if (_randomButton is not null)
        {
            _randomButton.IsChecked = _working.RandomAccent;
        }

        foreach (var (button, index) in _swatches)
        {
            button.IsChecked = !_working.RandomAccent && index == _working.AccentIndex;
        }
    }

    private void ThemeManager_Changed(object? sender, EventArgs e)
    {
        NativeTheme.Apply(this);
        RefreshAccentColors();
        RefreshAccentChecks();
    }

    private void ThemeAuto_Click(object sender, RoutedEventArgs e)
    {
        _working.ThemeMode = AppThemeMode.Auto;
        ThemeManager.Apply(_working);
        RefreshThemeChecks();
    }

    private void ThemeLight_Click(object sender, RoutedEventArgs e)
    {
        _working.ThemeMode = AppThemeMode.Light;
        ThemeManager.Apply(_working);
        RefreshThemeChecks();
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        _working.ThemeMode = AppThemeMode.Dark;
        ThemeManager.Apply(_working);
        RefreshThemeChecks();
    }

    private void FontMinus_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Max(11, _fontSize - 1);
        FontSizeText.Text = _fontSize.ToString();
    }

    private void FontPlus_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Min(30, _fontSize + 1);
        FontSizeText.Text = _fontSize.ToString();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认保存位置",
            Multiselect = false
        };

        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = new AppSettings().ResolveSaveFolder();
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "路径不可用", ex.Message);
            return;
        }

        _source.ThemeMode = _working.ThemeMode;
        _source.RandomAccent = _working.RandomAccent;
        _source.AccentIndex = _working.AccentIndex;
        _source.EditorFontSize = _fontSize;
        _source.DefaultSaveFolder = folder;

        ThemeManager.Apply(_source);
        SettingsStore.Save(_source);

        _saved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        ThemeManager.Changed -= ThemeManager_Changed;

        if (!_saved)
        {
            ThemeManager.Apply(_source);
        }
    }
}
