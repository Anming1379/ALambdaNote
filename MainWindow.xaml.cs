using System.Windows.Documents;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ALambdaNote.Services;
using ALambdaNote.Views;
using Microsoft.Win32;

namespace ALambdaNote;

public partial class MainWindow : Window
{
    private const string AppName = "ALambda Note";
    private const double MaxTextWidth = 720;

    private readonly AppSettings _settings;
    private MarkdownRenderer? _renderer;
    private ImageLoader? _images;
    private readonly MarkdownColorizer _colorizer;
    private readonly DispatcherTimer _highlightTimer;
    private readonly DispatcherTimer _dragLeaveTimer;

    private string? _filePath;
    private string? _pendingFileName;
    private string _savedText = string.Empty;
    private EncodingOption _encoding = TextFileService.Ansi;
    private string _newline = "\r\n";
    private bool _previewMode;
    private bool _suppressTextChanged;
    private bool _previewCacheValid;
    private FlowDocument? _cachedPreview;
    private bool _smoothScrollActive;
    private double _smoothScrollTarget;
    private double _smoothScrollStartOffset;
    private long _smoothScrollStartTicks;
    private bool _smoothScrollPreview;
    private ScrollViewer? _smoothPreviewScroll;
    private bool _skipScrollTransfer;
    private bool _renaming;
    private Popup? _openMenu;

    public MainWindow(AppSettings settings, string? initialFile, bool openedExternally)
    {
        _settings = settings;

        InitializeComponent();

        _colorizer = new MarkdownColorizer(EditorPalette.FromTheme(Editor.FontFamily));
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);

        ConfigureEditor();
        ApplyEditorTheme();

        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _colorizer.UpdateStructure(Editor.Document);
            Editor.TextArea.TextView.Redraw();
        };

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            HideDropOverlay();
        };

        ThemeManager.Changed += ThemeManager_Changed;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewDragOver += MainWindow_DragOver;
        PreviewDragLeave += MainWindow_DragLeave;
        PreviewDrop += MainWindow_Drop;
        Loaded += MainWindow_Loaded;
        ContentRendered += (_, _) => RefreshNativeWindow();
        StateChanged += MainWindow_StateChanged;
        SourceInitialized += (_, _) => NativeTheme.Apply(this);
        PreviewHost.SizeChanged += (_, _) => UpdatePreviewWidth();
        PreviewViewer.PreviewMouseRightButtonUp += PreviewViewer_PreviewMouseRightButtonUp;
        PreviewViewer.PreviewMouseWheel += MainWindow_PreviewMouseWheel;
        Editor.PreviewMouseWheel += MainWindow_PreviewMouseWheel;

        RestoreWindowSize();

        if (initialFile is not null)
        {
            OpenFile(initialFile, asPreview: true);
        }
        else
        {
            NewDocument();
        }

        if (!openedExternally)
        {
            Editor.Focus();
        }
    }

    private bool IsDirty
    {
        get
        {
            if (Editor.Document.TextLength != _savedText.Length)
            {
                return true;
            }

            return !string.Equals(Editor.Text, _savedText, StringComparison.Ordinal);
        }
    }

    private void ConfigureEditor()
    {
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.EnableImeSupport = true;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.AllowScrollBelowDocument = false;
        Editor.Options.CutCopyWholeLine = true;
        Editor.FontFamily = CreateEditorFontFamily();
        Editor.FontSize = Math.Clamp(_settings.EditorFontSize, 11, 32);
        Editor.TextChanged += Editor_TextChanged;
    }

    private void RestoreWindowSize()
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, area.Width - 40);
        var maxHeight = Math.Max(MinHeight, area.Height - 40);

        Width = Math.Clamp(_settings.WindowWidth > 300 ? _settings.WindowWidth : 640, MinWidth, maxWidth);
        Height = Math.Clamp(_settings.WindowHeight > 300 ? _settings.WindowHeight : 840, MinHeight, maxHeight);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        NativeTheme.Apply(this);
        UpdatePreviewWidth();

        // 兜底：如果启动/打开路径把空文档留在了预览模式，这里再切回编辑。
        if (_previewMode && string.IsNullOrWhiteSpace(Editor.Text))
        {
            _skipScrollTransfer = true;
            SetMode(false);
            _skipScrollTransfer = false;
        }

        // 不在启动时把整窗从透明渐变到可见。
        // 如果用户在动画期间最小化，动画可能停在 0，恢复/最大化后就会
        // 只剩窗口背景，看起来像白屏或黑屏。内容始终保持可见。
        RestoreContentVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            RefreshNativeWindow();
        }));
        if (!_previewMode)
        {
            Editor.Focus();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            StopSmoothScroll();
            return;
        }

        // 从最小化恢复或最大化后，强制把内容层恢复为可见，并重绘一次。
        // Windows 10 上窗口合成状态切换后偶尔不会自动刷新客户区。
        RestoreContentVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            NativeTheme.Apply(this);
            InvalidateVisual();
            UpdateLayout();
            Editor.TextArea.TextView.Redraw();
            PreviewViewer.InvalidateVisual();
            RefreshNativeWindow();
        }));
    }

    private void RestoreContentVisibility()
    {
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 1;

        EditorHost.BeginAnimation(OpacityProperty, null);
        EditorHost.Opacity = 1;
        PreviewHost.BeginAnimation(OpacityProperty, null);
        PreviewHost.Opacity = 1;
        DropOverlay.BeginAnimation(OpacityProperty, null);
        DropOverlay.Opacity = 1;
    }

    /// <summary>
    /// Windows 10 的 DWM 偶尔不会把 WPF 已经绘制好的客户区提交到屏幕，
    /// 表现为白屏/黑屏但窗口仍然响应。强制原生重绘并等待 DWM 刷新。
    /// </summary>
    private void RefreshNativeWindow()
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            const uint rdwInvalidate = 0x0001;
            const uint rdwErase = 0x0004;
            const uint rdwAllChildren = 0x0080;
            const uint rdwUpdateNow = 0x0100;
            const uint rdwFrame = 0x0400;
            RedrawWindow(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                rdwInvalidate | rdwErase | rdwAllChildren | rdwUpdateNow | rdwFrame);
            DwmFlush();
        }
        catch
        {
            // 刷新失败不应影响编辑。
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        StopSmoothScroll();
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SettingsStore.Save(_settings);
        ThemeManager.Changed -= ThemeManager_Changed;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            BeginRename();
            e.Handled = true;
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.S:
                SaveDocument(shift);
                e.Handled = true;
                break;
            case Key.O:
                if (!shift)
                {
                    OpenDocument(asPreview: false);
                    e.Handled = true;
                }
                break;
            case Key.N:
                if (!shift && ConfirmDiscard())
                {
                    NewDocument();
                }
                e.Handled = true;
                break;
            case Key.P:
                if (!shift)
                {
                    SetMode(!_previewMode);
                    e.Handled = true;
                }
                break;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        _colorizer.InvalidateStructure();
        InvalidatePreview();
        _highlightTimer.Stop();
        _highlightTimer.Start();
        UpdateDocumentInfo();
    }

    private void NewDocument()
    {
        _suppressTextChanged = true;
        Editor.Text = string.Empty;
        Editor.Document.UndoStack.ClearAll();
        _suppressTextChanged = false;

        _filePath = null;
        _pendingFileName = null;
        InvalidatePreview();
        _encoding = TextFileService.Ansi;
        _newline = "\r\n";
        _savedText = Editor.Text;
        Editor.CaretOffset = 0;

        _colorizer.UpdateStructure(Editor.Document);
        SetMode(false);
        UpdateDocumentInfo();
        Editor.Focus();
    }

    private void OpenDocument(bool asPreview)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var initial = _filePath is not null
            ? Path.GetDirectoryName(_filePath)
            : _settings.ResolveSaveFolder();

        var dialog = new OpenFileDialog
        {
            Title = "打开",
            Filter = "Markdown 与文本文件|*.md;*.markdown;*.mdown;*.mkd;*.txt|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName, asPreview);
        }
    }

    private void OpenFile(string path, bool asPreview)
    {
        try
        {
            var data = TextFileService.Read(path);

            _suppressTextChanged = true;
            Editor.Text = data.Text;
            Editor.Document.UndoStack.ClearAll();
            _suppressTextChanged = false;

            _filePath = Path.GetFullPath(path);
            _pendingFileName = null;
            InvalidatePreview();
            _encoding = data.Encoding;
            _newline = data.Newline;
            _savedText = Editor.Text;
            Editor.CaretOffset = 0;

            _colorizer.UpdateStructure(Editor.Document);
            Editor.TextArea.TextView.Redraw();

            // 空文档（包括只有 BOM / 换行的文件）没有可预览的内容；
            // 从资源管理器、拖入或命令行打开时都直接进入编辑模式。
            var effectivePreview = asPreview && !string.IsNullOrWhiteSpace(Editor.Text);

            _skipScrollTransfer = true;
            SetMode(effectivePreview);
            _skipScrollTransfer = false;
            if (effectivePreview)
            {
                RestorePreviewScroll(0);
            }
            else
            {
                Editor.ScrollToVerticalOffset(0);
            }

            UpdateDocumentInfo();

            if (!effectivePreview)
            {
                Editor.Focus();
            }
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "打开失败", ex.Message);
        }
    }

    private bool SaveDocument(bool saveAs)
    {
        var path = _filePath;

        if (saveAs || path is null)
        {
            var defaultFolder = _settings.ResolveSaveFolder();
            try
            {
                Directory.CreateDirectory(defaultFolder);
            }
            catch
            {
                // 如果默认目录不可用，让保存对话框自己决定。
            }

            var folder = path is not null
                ? Path.GetDirectoryName(path) ?? defaultFolder
                : defaultFolder;

            if (!Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存",
                FileName = path is not null ? Path.GetFileName(path) : _pendingFileName ?? "未命名.md",
                Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".md",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = folder
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TextFileService.Write(path, Editor.Text, _encoding, _newline);
            _filePath = Path.GetFullPath(path);
            _pendingFileName = null;
            InvalidatePreview();
            _savedText = Editor.Text;
            UpdateDocumentInfo();
            return true;
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "保存失败", ex.Message);
            return false;
        }
    }

    private bool ConfirmDiscard()
    {
        if (!IsDirty)
        {
            return true;
        }

        // 最小化时 Owner 也是 IsVisible=true，但确认框会跟着藏到任务栏后面，
        // 用户看起来就是“点了关闭却关不掉”。先把主窗恢复并激活。
        if (WindowState == WindowState.Minimized)
        {
            // 这里可能正处在 Closing 中，不能调用 Show()（WPF 会抛
            // “窗口关闭期间无法 Show”），只恢复状态并激活即可。
            WindowState = WindowState.Normal;
            Activate();
        }

        var choice = FlatDialog.AskSave(this);
        return choice switch
        {
            DialogChoice.Save => SaveDocument(saveAs: false),
            DialogChoice.DontSave => true,
            _ => false
        };
    }

    private void SetMode(bool preview)
    {
        var changed = _previewMode != preview;
        var scrollRatio = changed && !_skipScrollTransfer
            ? (preview ? CaptureEditorScrollRatio() : CapturePreviewScrollRatio())
            : double.NaN;
        StopSmoothScroll();
        _previewMode = preview;
        EditorHost.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggle.IsChecked = preview;
        PreviewIcon.Text = preview ? "\uE150" : "\uF1C5";
        PreviewToggle.ToolTip = preview ? "编辑 (Ctrl+P)" : "预览 (Ctrl+P)";

        if (changed)
        {
            AnimateIn(preview ? PreviewHost : EditorHost);
        }

        if (preview)
        {
            RebuildPreview();
            if (!double.IsNaN(scrollRatio))
            {
                RestorePreviewScroll(scrollRatio);
            }
        }
        else
        {
            Editor.Focus();
            if (!double.IsNaN(scrollRatio))
            {
                RestoreEditorScroll(scrollRatio);
            }
        }
    }

    private double CaptureEditorScrollRatio()
    {
        var maximum = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        return maximum < 0.5 ? 0 : Math.Clamp(Editor.VerticalOffset / maximum, 0, 1);
    }

    private double CapturePreviewScrollRatio()
    {
        var scroll = FindVisualChild<ScrollViewer>(PreviewViewer);
        var maximum = scroll?.ScrollableHeight ?? 0;
        return maximum < 0.5 ? 0 : Math.Clamp(scroll!.VerticalOffset / maximum, 0, 1);
    }

    private void RestorePreviewScroll(double ratio)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyPreviewScroll(ratio, 0)));
    }

    private void ApplyPreviewScroll(double ratio, int attempt)
    {
        if (!_previewMode)
        {
            return;
        }

        PreviewViewer.UpdateLayout();
        var scroll = FindVisualChild<ScrollViewer>(PreviewViewer);
        if (scroll is null)
        {
            if (attempt < 2)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => ApplyPreviewScroll(ratio, attempt + 1)));
            }

            return;
        }

        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(Math.Clamp(ratio, 0, 1) * Math.Max(0, scroll.ScrollableHeight));
    }

    private void RestoreEditorScroll(double ratio)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_previewMode)
            {
                return;
            }

            Editor.UpdateLayout();
            var maximum = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
            Editor.ScrollToVerticalOffset(Math.Clamp(ratio, 0, 1) * maximum);
        }));
    }

    private void RebuildPreview()
    {
        UpdatePreviewWidth();
        if (_previewCacheValid && _cachedPreview is not null)
        {
            PreviewViewer.Document = _cachedPreview;
            return;
        }

        var style = MarkdownStyle.FromTheme(Math.Clamp(_settings.EditorFontSize, 11, 32));
        var baseDirectory = _filePath is not null ? Path.GetDirectoryName(_filePath) : Environment.CurrentDirectory;
        var renderer = _renderer ??= new MarkdownRenderer();
        var images = _images ??= new ImageLoader();
        renderer.SetMaxContentWidth(PreviewViewer.Width - 20);
        _cachedPreview = renderer.Render(Editor.Text, style, baseDirectory, images);
        _previewCacheValid = true;
        PreviewViewer.Document = _cachedPreview;
    }

    private void InvalidatePreview()
    {
        _previewCacheValid = false;
        _cachedPreview = null;
    }

    private void UpdatePreviewWidth()
    {
        var available = PreviewHost.ActualWidth - PreviewHost.Padding.Left - PreviewHost.Padding.Right;
        if (double.IsNaN(available) || available <= 0)
        {
            available = Math.Max(260, Width - 120);
        }

        PreviewViewer.Width = Math.Clamp(available, 260, MaxTextWidth);
        _renderer?.SetMaxContentWidth(PreviewViewer.Width - 20);
    }

    private void UpdateDocumentInfo()
    {
        var name = _filePath is not null
            ? Path.GetFileName(_filePath)
            : _pendingFileName ?? "未命名";
        var dirty = IsDirty;

        FileNameText.Text = name;
        FileNameText.ToolTip = (_filePath ?? (_pendingFileName is not null ? $"尚未保存：{_pendingFileName}" : "尚未保存"))
            + Environment.NewLine + "单击重命名，右键更多操作";
        Title = $"{(dirty ? "* " : string.Empty)}{name} - {AppName}";
        PathText.Text = _filePath ?? "尚未保存";
        PathText.ToolTip = _filePath ?? "Ctrl+S 保存到默认目录";
        EncodingText.Text = _encoding.Display;
        EncodingButton.ToolTip = $"保存时使用 {_encoding.Display}";

        DirtyLight.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dirty ? "Accent" : "TextMuted");
        DirtyLight.ToolTip = dirty ? "有未保存的修改" : "已保存";
    }

    /// <summary>
    /// AvalonEdit 没有公开的 LineHeight 属性；给字体家族设置 2.0 倍行距，
    /// 让 TextLine、VisualLine、命中测试和光标共享同一套字体度量。
    /// 这比直接改 VisualLine 高度稳定，不会造成选择/光标错位。
    /// </summary>
    private static FontFamily CreateEditorFontFamily()
    {
        var family = new FontFamily { LineSpacing = 2.0 };
        family.FamilyMaps.Add(new FontFamilyMap
        {
            Target = "Dubai",
            Unicode = "0000-FFFF"
        });
        return family;
    }

    private void ApplyEditorTheme()
    {
        var accent = ThemeManager.Brush("Accent");
        var selection = ThemeManager.Brush("Selection");

        Editor.TextArea.Caret.CaretBrush = accent;
        Editor.TextArea.SelectionBrush = selection;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionCornerRadius = 0;
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.TextView.Redraw();
        PreviewViewer.SelectionBrush = selection;
        PreviewViewer.SelectionOpacity = 1.0;
        PreviewViewer.IsInactiveSelectionHighlightEnabled = true;
        _colorizer.SetPalette(EditorPalette.FromTheme(Editor.FontFamily));
        _colorizer.UpdateStructure(Editor.Document);
    }

    private void ThemeManager_Changed(object? sender, EventArgs e)
    {
        var ratio = _previewMode ? CapturePreviewScrollRatio() : double.NaN;
        NativeTheme.Apply(this);
        ApplyEditorTheme();
        InvalidatePreview();
        if (_previewMode)
        {
            RebuildPreview();
            if (!double.IsNaN(ratio))
            {
                RestorePreviewScroll(ratio);
            }
        }
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        SetMode(!_previewMode);
    }

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            var ratio = _previewMode ? CapturePreviewScrollRatio() : double.NaN;
            ThemeManager.Apply(_settings);
            Editor.FontSize = Math.Clamp(_settings.EditorFontSize, 11, 32);
            InvalidatePreview();
            SettingsStore.Save(_settings);
            UpdateDocumentInfo();
            if (_previewMode)
            {
                RebuildPreview();
                if (!double.IsNaN(ratio))
                {
                    RestorePreviewScroll(ratio);
                }
            }
        }
    }

    private void EncodingButton_Click(object sender, RoutedEventArgs e)
    {
        var items = TextFileService.All
            .Select(option => new MenuItemSpec(
                option.Display,
                () =>
                {
                    _encoding = option;
                    UpdateDocumentInfo();
                },
                Checked: string.Equals(option.Id, _encoding.Id, StringComparison.Ordinal)))
            .ToList();

        ShowMenu(EncodingButton, PlacementMode.Bottom, items, minWidth: 170);
    }

    private void FileNameButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRename();
    }

    private void BeginRename()
    {
        if (_renaming)
        {
            return;
        }

        if (_filePath is not null && !File.Exists(_filePath))
        {
            FlatDialog.Info(this, "重命名", "源文件不存在，可能已经被移动或删除。");
            return;
        }

        _renaming = true;
        FileNameBox.Text = _filePath is null
            ? _pendingFileName ?? "未命名"
            : Path.GetFileName(_filePath);
        FileNameButton.Visibility = Visibility.Collapsed;
        FileNameBox.Visibility = Visibility.Visible;
        FileNameBox.Focus();
        FileNameBox.SelectAll();
    }

    private void CancelRename()
    {
        if (!_renaming)
        {
            return;
        }

        _renaming = false;
        FileNameBox.Visibility = Visibility.Collapsed;
        FileNameButton.Visibility = Visibility.Visible;
        if (!_previewMode)
        {
            Editor.Focus();
        }
    }

    private void CommitRename()
    {
        if (!_renaming)
        {
            return;
        }

        var oldPath = _filePath;
        var typed = FileNameBox.Text.Trim();

        _renaming = false;
        FileNameBox.Visibility = Visibility.Collapsed;
        FileNameButton.Visibility = Visibility.Visible;

        if (oldPath is null)
        {
            if (typed.Length == 0)
            {
                _pendingFileName = null;
                UpdateDocumentInfo();
                return;
            }

            if (typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !string.Equals(Path.GetFileName(typed), typed, StringComparison.Ordinal))
            {
                FlatDialog.Info(this, "重命名", "文件名不能包含这些字符：/ : * ? < > |");
                UpdateDocumentInfo();
                return;
            }

            if (!Path.HasExtension(typed))
            {
                typed += ".md";
            }

            _pendingFileName = typed;
            UpdateDocumentInfo();
            if (!_previewMode)
            {
                Editor.Focus();
            }

            return;
        }

        var oldName = Path.GetFileName(oldPath);
        if (typed.Length == 0 || string.Equals(typed, oldName, StringComparison.Ordinal))
        {
            UpdateDocumentInfo();
            return;
        }

        if (typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(typed), typed, StringComparison.Ordinal))
        {
            FlatDialog.Info(this, "重命名", "文件名不能包含这些字符：\\ / : * ? < > |");
            UpdateDocumentInfo();
            return;
        }

        if (!Path.HasExtension(typed))
        {
            typed += Path.GetExtension(oldPath);
        }

        var directory = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrEmpty(directory))
        {
            UpdateDocumentInfo();
            return;
        }

        var newPath = Path.Combine(directory, typed);
        var caseOnly = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(newPath, oldPath, StringComparison.Ordinal);

        try
        {
            if (caseOnly)
            {
                var temporary = oldPath + ".rename-" + Guid.NewGuid().ToString("N");
                File.Move(oldPath, temporary);
                try
                {
                    File.Move(temporary, newPath);
                }
                catch
                {
                    File.Move(temporary, oldPath);
                    throw;
                }
            }
            else
            {
                if (File.Exists(newPath))
                {
                    FlatDialog.Info(this, "重命名", "同一目录下已经有这个文件名啦，换一个吧。");
                    UpdateDocumentInfo();
                    return;
                }

                File.Move(oldPath, newPath);
            }

            _filePath = Path.GetFullPath(newPath);
            UpdateDocumentInfo();

            if (_previewMode)
            {
                RebuildPreview();
            }
            else
            {
                Editor.Focus();
            }
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "重命名失败", ex.Message);
            UpdateDocumentInfo();
        }
    }

    private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void FileNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_renaming)
        {
            CommitRename();
        }
    }

    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            StopSmoothScroll();
            return;
        }

        var notches = e.Delta / 120.0;
        if (notches == 0)
        {
            return;
        }

        if (ReferenceEquals(sender, Editor))
        {
            var step = Math.Max(96, Editor.TextArea.TextView.DefaultLineHeight * 5);
            if (StartSmoothScroll(preview: false, -notches * step))
            {
                e.Handled = true;
            }
        }
        else if (ReferenceEquals(sender, PreviewViewer))
        {
            var scroll = FindVisualChild<ScrollViewer>(PreviewViewer);
            if (scroll is not null)
            {
                var step = Math.Max(48, scroll.ViewportHeight * 0.18);
                if (StartSmoothScroll(preview: true, -notches * step))
                {
                    e.Handled = true;
                }
            }
        }
    }

    private bool StartSmoothScroll(bool preview, double delta)
    {
        var scroll = preview ? FindVisualChild<ScrollViewer>(PreviewViewer) : null;
        if (preview && scroll is null)
        {
            return false;
        }

        var current = preview ? scroll!.VerticalOffset : Editor.VerticalOffset;
        var maximum = preview
            ? Math.Max(0, scroll!.ScrollableHeight)
            : Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);

        var continuing = _smoothScrollActive && _smoothScrollPreview == preview;
        var target = continuing
            ? _smoothScrollTarget + delta
            : current + delta;

        _smoothScrollTarget = Math.Clamp(target, 0, maximum);
        _smoothScrollPreview = preview;
        _smoothPreviewScroll = scroll;
        _smoothScrollStartOffset = current;
        _smoothScrollStartTicks = Stopwatch.GetTimestamp();

        if (Math.Abs(_smoothScrollTarget - current) < 0.25)
        {
            SetSmoothScrollOffset(current);
            StopSmoothScroll();
            return true;
        }

        if (!_smoothScrollActive)
        {
            _smoothScrollActive = true;
            CompositionTarget.Rendering += SmoothScrollFrame;
        }

        return true;
    }

    private void SmoothScrollFrame(object? sender, EventArgs e)
    {
        if (!_smoothScrollActive)
        {
            return;
        }

        if (_smoothScrollPreview && _smoothPreviewScroll is null)
        {
            StopSmoothScroll();
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _smoothScrollStartTicks) / (double)Stopwatch.Frequency;
        var progress = Math.Clamp(elapsed / 0.22, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var offset = _smoothScrollStartOffset + (_smoothScrollTarget - _smoothScrollStartOffset) * eased;
        SetSmoothScrollOffset(offset);

        if (progress >= 1)
        {
            StopSmoothScroll();
        }
    }

    private void SetSmoothScrollOffset(double offset)
    {
        if (_smoothScrollPreview)
        {
            _smoothPreviewScroll?.ScrollToVerticalOffset(offset);
        }
        else
        {
            Editor.ScrollToVerticalOffset(offset);
        }
    }

    private void StopSmoothScroll()
    {
        if (_smoothScrollActive)
        {
            CompositionTarget.Rendering -= SmoothScrollFrame;
            _smoothScrollActive = false;
        }

        _smoothPreviewScroll = null;
    }

    private static void AnimateIn(FrameworkElement element)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0.35;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });

        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void FileNameText_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var items = new List<MenuItemSpec>
        {
            new("新建", () =>
            {
                if (ConfirmDiscard())
                {
                    NewDocument();
                }
            }, Glyph: "\uE89C", Shortcut: "Ctrl+N"),
            new("打开", () => OpenDocument(asPreview: false), Glyph: "\uE2C8", Shortcut: "Ctrl+O"),
            new("保存", () => SaveDocument(saveAs: false), Glyph: "\uE161", Shortcut: "Ctrl+S"),
            new("另存为", () => SaveDocument(saveAs: true), Glyph: "\uE161", Shortcut: "Ctrl+Shift+S"),
            new("重命名", BeginRename, Glyph: "\uE150", Shortcut: "F2"),
            new("在文件夹中显示", ShowInExplorer, Glyph: "\uEAF3", Enabled: _filePath is not null)
        };

        ShowMenu(FileNameText, PlacementMode.MousePoint, items);
    }

    private void PreviewViewer_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var items = new List<MenuItemSpec>
        {
            new("复制", () => ApplicationCommands.Copy.Execute(null, PreviewViewer), Glyph: "\uE14D", Shortcut: "Ctrl+C"),
            new("全选", () => ApplicationCommands.SelectAll.Execute(null, PreviewViewer), Glyph: "\uE162", Shortcut: "Ctrl+A")
        };

        ShowMenu(PreviewViewer, PlacementMode.MousePoint, items, minWidth: 150);
    }

    private void ShowInExplorer()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            var arguments = File.Exists(_filePath)
                ? $"/select,\"{_filePath}\""
                : $"\"{Path.GetDirectoryName(_filePath)}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 忽略。
        }
    }

    private sealed record MenuItemSpec(
        string Header,
        Action Action,
        string? Glyph = null,
        string? Shortcut = null,
        bool Checked = false,
        bool Enabled = true);

    private void ShowMenu(UIElement target, PlacementMode placement, IReadOnlyList<MenuItemSpec> items, double minWidth = 190)
    {
        _openMenu?.SetCurrentValue(Popup.IsOpenProperty, false);

        var iconFont = (FontFamily)FindResource("IconFont");
        var textBrush = ThemeManager.Brush("Text");
        var mutedBrush = ThemeManager.Brush("TextMuted");
        var accentBrush = ThemeManager.Brush("Accent");

        var panel = new StackPanel { MinWidth = minWidth };

        foreach (var item in items)
        {
            if (item.Header == "-")
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = ThemeManager.Brush("Border")
                });
                continue;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = item.Checked ? "\uE876" : item.Glyph ?? string.Empty,
                FontFamily = iconFont,
                FontSize = 15,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = item.Checked ? accentBrush : mutedBrush
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var header = new TextBlock
            {
                Text = item.Header,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = textBrush,
                Margin = new Thickness(2, 0, 0, 0)
            };
            Grid.SetColumn(header, 1);
            grid.Children.Add(header);

            var shortcut = new TextBlock
            {
                Text = item.Shortcut ?? string.Empty,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = mutedBrush,
                Margin = new Thickness(26, 0, 0, 0)
            };
            Grid.SetColumn(shortcut, 2);
            grid.Children.Add(shortcut);

            var row = new Button
            {
                Style = (Style)FindResource("MenuRow"),
                Content = grid,
                IsEnabled = item.Enabled
            };

            row.Click += (_, _) =>
            {
                _openMenu?.SetCurrentValue(Popup.IsOpenProperty, false);
                if (item.Enabled)
                {
                    item.Action();
                }
            };

            panel.Children.Add(row);
        }

        var surface = new Border
        {
            Background = ThemeManager.Brush("Surface"),
            BorderBrush = ThemeManager.Brush("BorderStrong"),
            BorderThickness = new Thickness(1),
            Child = panel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.26,
                Color = Colors.Black
            }
        };

        var menu = new Popup
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = surface,
            IsOpen = true
        };

        _openMenu = menu;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        surface.Opacity = 0;
        var transform = new TranslateTransform(0, -6);
        surface.RenderTransform = transform;
        surface.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void ShowDropOverlay()
    {
        _dragLeaveTimer.Stop();
        if (DropOverlay.Visibility == Visibility.Visible)
        {
            DropOverlay.BeginAnimation(OpacityProperty, null);
            DropOverlay.Opacity = 1;
            return;
        }

        DropOverlay.BeginAnimation(OpacityProperty, null);
        DropOverlay.Visibility = Visibility.Visible;
        DropOverlay.Opacity = 0;
        DropOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void HideDropOverlay()
    {
        if (DropOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = new DoubleAnimation(DropOverlay.Opacity, 0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            DropOverlay.Opacity = 1;
        };
        DropOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            _dragLeaveTimer.Stop();
            ShowDropOverlay();
        }
    }

    private void MainWindow_DragLeave(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        HideDropOverlay();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        e.Handled = true;

        if (!ConfirmDiscard())
        {
            return;
        }

        OpenFile(files[0], asPreview: true);
    }
}
