using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ALambdaNote;
using ALambdaNote.Services;
using ALambdaNote.Views;

namespace UiHarness;

internal static class Program
{
    private static readonly List<string> Log = new();
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ALambdaNote",
            "settings.json");
        var backup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;

        var work = Path.Combine(Path.GetTempPath(), "ALambdaNoteTests");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(work);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ALambdaNote;component/Themes/Styles.xaml", UriKind.Absolute)
        });

        try
        {
            TestThemeAndResources(settingsPath);
            var settings = new AppSettings
            {
                ThemeMode = AppThemeMode.Light,
                RandomAccent = false,
                AccentIndex = 0,
                WindowWidth = 1000,
                WindowHeight = 700,
                EditorFontSize = 17
            };

            ThemeManager.Apply(settings);
            TestEncoding(work);
            TestWebImage(settings);
            TestHtmlImage(settings, work);
            TestLooseImage(settings, work);
            TestSvgImages(settings, work);
            TestNewWindow(settings);
            TestOpenMarkdown(settings, work);
            TestEmptyFileOpensEditor(settings, work);
            TestSaveRoundtrip(settings, work);
            TestRename(settings, work);
            TestWheel(settings, work);
            TestModeScrollSync(settings, work);
            TestDragDrop(settings, work);
            TestDefaultSize();
            TestSettingsAndShots(settings, work);
        }
        catch (Exception ex)
        {
            Fail("harness", ex.ToString());
        }
        finally
        {
            try
            {
                if (backup is not null)
                {
                    File.WriteAllBytes(settingsPath, backup);
                }
                else if (File.Exists(settingsPath))
                {
                    File.Delete(settingsPath);
                }
            }
            catch
            {
                // 忽略恢复失败。
            }

            Directory.Delete(work, true);
        }

        Console.WriteLine();
        foreach (var line in Log)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL CHECKS PASSED" : $"{_failed} CHECK(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestThemeAndResources(string settingsPath)
    {
        Check("styles loaded: BodyFont", Application.Current.TryFindResource("BodyFont") is FontFamily);
        Check("styles loaded: IconFont", Application.Current.TryFindResource("IconFont") is FontFamily);
        Check("default editor font size 17", Math.Abs(new AppSettings().EditorFontSize - 17) < 0.01, new AppSettings().EditorFontSize.ToString());

        File.WriteAllText(settingsPath, @"{""EditorFontSize"":15}", new UTF8Encoding(false));
        var migrated = SettingsStore.Load();
        Check("legacy 15 migrates to 17", Math.Abs(migrated.EditorFontSize - 17) < 0.01, migrated.EditorFontSize.ToString());
        Check("legacy settings stamped version", migrated.SettingsVersion == 3, migrated.SettingsVersion?.ToString() ?? "null");

        File.WriteAllText(settingsPath, @"{""EditorFontSize"":16,""AccentIndex"":5}", new UTF8Encoding(false));
        var migratedV2 = SettingsStore.Load();
        Check("v2 font migrates to 17", Math.Abs(migratedV2.EditorFontSize - 17) < 0.01, migratedV2.EditorFontSize.ToString());
        Check("removed yellow shifts accent index", migratedV2.AccentIndex == 4, migratedV2.AccentIndex.ToString());
        File.Delete(settingsPath);

        ThemeManager.Apply(new AppSettings { ThemeMode = AppThemeMode.Light, RandomAccent = false, AccentIndex = 4 });
        var lightBg = ColorOf("Bg");
        Check("light bg color", lightBg == ThemeManager.Hex("#FCFBF8"), lightBg.ToString());
        Check("accent preset 黛蓝", ThemeManager.CurrentAccent.Name == "黛蓝", ThemeManager.CurrentAccent.Name);

        ThemeManager.Apply(new AppSettings { ThemeMode = AppThemeMode.Dark, RandomAccent = false, AccentIndex = 4 });
        var darkBg = ColorOf("Bg");
        Check("dark bg color", darkBg == ThemeManager.Hex("#1A1C1E"), darkBg.ToString());
        Check("dark accent variant", ThemeManager.Accent == ThemeManager.Hex("#7FA0FF"), ThemeManager.Accent.ToString());
        Check("yellow accent removed", ThemeManager.Accents.All(a => a.Name != "芥黄"), ThemeManager.Accents.Length.ToString());

        ThemeManager.Apply(new AppSettings { ThemeMode = AppThemeMode.Light, RandomAccent = true });
        var firstIndex = ThemeManager.CurrentAccentIndex;
        ThemeManager.Apply(new AppSettings { ThemeMode = AppThemeMode.Light, RandomAccent = true });
        Check("random accent stable within run", firstIndex == ThemeManager.CurrentAccentIndex);

        var probe = new AppSettings
        {
            ThemeMode = AppThemeMode.Dark,
            RandomAccent = false,
            AccentIndex = 3,
            WindowWidth = 701,
            WindowHeight = 899,
            EditorFontSize = 17,
            DefaultSaveFolder = Path.GetTempPath()
        };
        SettingsStore.Save(probe);
        var loaded = SettingsStore.Load();
        Check("settings roundtrip", loaded.ThemeMode == AppThemeMode.Dark && loaded.AccentIndex == 3 && Math.Abs(loaded.WindowWidth - 701) < 0.1 && Math.Abs(loaded.EditorFontSize - 17) < 0.1, loaded.ThemeMode + "/" + loaded.AccentIndex);
    }

    private static void TestEncoding(string work)
    {
        var ansiPath = Path.Combine(work, "ansi.txt");
        TextFileService.Write(ansiPath, "中文测试\r\n第二行", TextFileService.Ansi, "\r\n");
        var ansi = TextFileService.Read(ansiPath);
        Check("ANSI roundtrip text", ansi.Text.Contains("中文测试") && ansi.Text.Contains("第二行"), ansi.Text);
        Check("ANSI roundtrip encoding", ansi.Encoding.Id == "ansi", ansi.Encoding.Id);
        Check("ANSI newline", ansi.Newline == "\r\n", ansi.Newline.Replace("\r", "\\r").Replace("\n", "\\n"));

        var utf8BomPath = Path.Combine(work, "bom.txt");
        TextFileService.Write(utf8BomPath, "hello", TextFileService.Utf8Bom, "\n");
        var bomBytes = File.ReadAllBytes(utf8BomPath);
        Check("UTF-8 BOM written", bomBytes.Length >= 3 && bomBytes[0] == 0xEF && bomBytes[1] == 0xBB && bomBytes[2] == 0xBF);
        var bomRead = TextFileService.Read(utf8BomPath);
        Check("UTF-8 BOM detected", bomRead.Encoding.Id == "utf8bom" && bomRead.Text == "hello", bomRead.Encoding.Id + "/" + bomRead.Text);

        var utf8Path = Path.Combine(work, "plain.txt");
        var lf = ((char)10).ToString();
        TextFileService.Write(utf8Path, "你好" + lf + "世界", TextFileService.Utf8, lf);
        var plain = TextFileService.Read(utf8Path);
        Check("UTF-8 detected", plain.Encoding.Id == "utf8", plain.Encoding.Id);
        Check("LF kept", plain.Newline == lf, plain.Newline.Length.ToString());

        var utf16Path = Path.Combine(work, "utf16.txt");
        TextFileService.Write(utf16Path, "宽字符", TextFileService.Utf16Le, "\r\n");
        var utf16 = TextFileService.Read(utf16Path);
        Check("UTF-16 LE roundtrip", utf16.Encoding.Id == "utf16le" && utf16.Text == "宽字符", utf16.Encoding.Id + "/" + utf16.Text);

        var binaryPath = Path.Combine(work, "bin.dat");
        File.WriteAllBytes(binaryPath, new byte[] { 1, 2, 0, 4, 5 });
        var threw = false;
        try
        {
            TextFileService.Read(binaryPath);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        Check("binary file rejected", threw);
    }

    private static void TestWebImage(AppSettings settings)
    {
        const string url = "https://www.baidu.com/img/PCtm_d9c8750bed0b3c7d089fa7d55720d6cf.png";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
            Check("web image download", bytes.Length > 1000, bytes.Length.ToString());

            try
            {
                var bmp = Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }).GetAwaiter().GetResult();
                Check("web image decode on worker thread", bmp.PixelWidth > 0, bmp.PixelWidth.ToString());
            }
            catch (Exception ex)
            {
                Fail("web image decode on worker thread", ex.GetType().Name + ": " + ex.Message);
            }

            var loader = new ImageLoader();
            var loaded = loader.LoadAsync(url, null).GetAwaiter().GetResult();
            Check("ImageLoader web image", HasImage(loaded.Image), DescribeImage(loaded.Image) ?? loaded.Error ?? "null");

            var markdownPath = Path.Combine(Path.GetTempPath(), "ALambdaNoteWebImage.md");
            File.WriteAllText(markdownPath, "# web" + ((char)10).ToString() + ((char)10).ToString() + "![web](" + url + ")", new UTF8Encoding(false));
            var window = new MainWindow(settings, markdownPath, openedExternally: true);
            window.Show();
            Pump(3500);
            var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
            var document = viewer.Document!;
            var loadedInPreview = FindImageHosts(document).Any(h => h.Child is Image);
            Check("preview web image loaded", loadedInPreview, AllParagraphText(document).FirstOrDefault(t => t.Contains("加载失败", StringComparison.Ordinal)) ?? "ok");
            window.Close();
            Pump(120);
            File.Delete(markdownPath);
        }
        catch (Exception ex)
        {
            Fail("web image download", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void TestHtmlImage(AppSettings settings, string work)
    {
        var imagePath = Path.Combine(work, "html-image.png");
        CreateSampleImage(imagePath);

        var markdownPath = Path.Combine(work, "html.md");
        File.WriteAllText(markdownPath, "<img src='html-image.png' alt='html image'>", new UTF8Encoding(false));

        var window = new MainWindow(settings, markdownPath, openedExternally: true);
        window.Show();
        Pump(900);

        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var hosts = FindImageHosts(viewer.Document!).ToList();

        Check("html image rendered", hosts.Count >= 1, hosts.Count.ToString());
        Check("html image loaded", hosts.Any(h => h.Child is Image), hosts.Count(h => h.Child is Image).ToString());

        window.Close();
        Pump(100);
    }

    private static void TestLooseImage(AppSettings settings, string work)
    {
        var imagePath = Path.Combine(work, "loose image.png");
        CreateSampleImage(imagePath);

        var markdownPath = Path.Combine(work, "loose.md");
        var nl = ((char)10).ToString();
        var quote = ((char)34).ToString();
        var text = "![space](loose image.png)" + nl + nl +
                   "![title](loose image.png " + quote + "标题" + quote + ")" + nl + nl +
                   "前文 ![inline](loose image.png) 后文";
        File.WriteAllText(markdownPath, text, new UTF8Encoding(false));

        var window = new MainWindow(settings, markdownPath, openedExternally: true);
        window.Show();
        Pump(900);

        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var hosts = FindImageHosts(viewer.Document!).ToList();
        var loaded = hosts.Count(h => h.Child is Image);

        var plain = new TextRange(viewer.Document!.ContentStart, viewer.Document.ContentEnd).Text;
        Check("loose image rendered", hosts.Count >= 3, hosts.Count.ToString());
        Check("loose image loaded", loaded >= 3, loaded.ToString());
        Check("loose inline text kept", plain.Contains("前文") && plain.Contains("后文"), plain.Replace((char)10, ' '));

        window.Close();
        Pump(100);
    }

    private static void TestSvgImages(AppSettings settings, string work)
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="240" height="120" viewBox="0 0 240 120">
              <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#155EEF"/><stop offset="1" stop-color="#6FA8FF"/></linearGradient></defs>
              <rect width="240" height="120" rx="18" fill="url(#g)"/>
              <circle cx="54" cy="60" r="26" fill="#FFFFFF" opacity="0.9"/>
              <text x="96" y="69" font-family="Segoe UI" font-size="24" fill="#FFFFFF">SVG</text>
            </svg>
            """;

        var svgPath = Path.Combine(work, "vector.svg");
        File.WriteAllText(svgPath, svg, new UTF8Encoding(false));

        var loader = new ImageLoader();
        var localSvg = loader.LoadAsync(svgPath, work).GetAwaiter().GetResult();
        Check("local svg image", HasImage(localSvg.Image), DescribeImage(localSvg.Image) ?? localSvg.Error ?? "null");

        var svgzPath = Path.Combine(work, "vector.svgz");
        using (var output = File.Create(svgzPath))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            var payload = Encoding.UTF8.GetBytes(svg);
            gzip.Write(payload, 0, payload.Length);
        }

        var svgz = loader.LoadAsync(svgzPath, work).GetAwaiter().GetResult();
        Check("local svgz image", HasImage(svgz.Image), DescribeImage(svgz.Image) ?? svgz.Error ?? "null");

        var viewBoxPath = Path.Combine(work, "viewbox-only.svg");
        File.WriteAllText(
            viewBoxPath,
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><circle cx="24" cy="24" r="20" fill="#155EEF"/></svg>""",
            new UTF8Encoding(false));
        var viewBoxOnly = loader.LoadAsync(viewBoxPath, work).GetAwaiter().GetResult();
        Check("viewbox-only svg image", HasImage(viewBoxOnly.Image), DescribeImage(viewBoxOnly.Image) ?? viewBoxOnly.Error ?? "null");

        var paddedPath = Path.Combine(work, "padded.svg");
        File.WriteAllText(
            paddedPath,
            """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80"><circle cx="60" cy="40" r="20" fill="#155EEF"/></svg>""",
            new UTF8Encoding(false));
        var padded = loader.LoadAsync(paddedPath, work).GetAwaiter().GetResult();
        Check(
            "svg keeps viewport padding",
            padded.Image is not null && padded.Image.Width >= 120 && padded.Image.Height >= 80,
            DescribeImage(padded.Image) ?? padded.Error ?? "null");

        try
        {
            var remoteSvg = loader
                .LoadAsync("https://www.w3.org/Icons/w3c_home.svg", null)
                .GetAwaiter()
                .GetResult();
            Check("remote svg image", HasImage(remoteSvg.Image), DescribeImage(remoteSvg.Image) ?? remoteSvg.Error ?? "null");
        }
        catch (Exception ex)
        {
            Fail("remote svg image", ex.GetType().Name + ": " + ex.Message);
        }

        var dataSvg = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        var fromData = loader.LoadAsync(dataSvg, null).GetAwaiter().GetResult();
        Check("data uri svg image", HasImage(fromData.Image), DescribeImage(fromData.Image) ?? fromData.Error ?? "null");

        // 宣传图常见：CSS 动画 + foreignObject 里的 HTML 文字。
        const string headerSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="300" height="80" viewBox="0 0 300 80">
              <style>@keyframes fade { from { opacity: 0; } to { opacity: 1; } } .fade { animation: fade 1s; }</style>
              <rect width="300" height="80" fill="#0EA5E9"/>
              <foreignObject x="20" y="20" width="260" height="40">
                <div xmlns="http://www.w3.org/1999/xhtml" style="font: 20px sans-serif; color: white;">Anming</div>
              </foreignObject>
            </svg>
            """;
        var headerPath = Path.Combine(work, "header-foreign.svg");
        File.WriteAllText(headerPath, headerSvg, new UTF8Encoding(false));
        var header = loader.LoadAsync(headerPath, work).GetAwaiter().GetResult();
        Check(
            "css and foreignObject svg falls back to text",
            HasImage(header.Image),
            DescribeImage(header.Image) ?? header.Error ?? "null");

        // 没有扩展名、前面还有注释的 SVG 也要能识别出来。
        var commentSvgPath = Path.Combine(work, "no-extension-image");
        File.WriteAllText(
            commentSvgPath,
            "<!-- badge --><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"48\" height=\"24\"><rect width=\"48\" height=\"24\" fill=\"#155EEF\"/></svg>",
            new UTF8Encoding(false));
        var commentSvg = loader.LoadAsync(commentSvgPath, work).GetAwaiter().GetResult();
        Check("comment svg detected without extension", HasImage(commentSvg.Image), DescribeImage(commentSvg.Image) ?? commentSvg.Error ?? "null");

        // 动画型 typing SVG：两条重叠 textPath 在静态预览里应纵向展开，不能只留第一行。
        const string typingSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="600" height="50" viewBox="0 0 600 50">
              <path id="p"><animate attributeName="d" values="m0,25 h0 ; m0,25 h600" /></path>
              <text x="50%" text-anchor="middle" font-size="20" fill="#22C55E"><textPath href="#p">First line</textPath></text>
              <text x="50%" text-anchor="middle" font-size="20" fill="#22C55E"><textPath href="#p">Second line</textPath></text>
            </svg>
            """;
        var typingPath = Path.Combine(work, "typing-two-lines.svg");
        File.WriteAllText(typingPath, typingSvg, new UTF8Encoding(false));
        var typing = loader.LoadAsync(typingPath, work).GetAwaiter().GetResult();
        Check(
            "typing svg keeps both static lines",
            typing.Image is not null && typing.Image.Width >= 600 && typing.Image.Height > 50,
            DescribeImage(typing.Image) ?? typing.Error ?? "null");

        const string animatedSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="600" height="50" viewBox="0 0 600 50">
              <path id="p"><animate attributeName="d" values="m0,25 h0 ; m0,25 h600" /></path>
              <text x="50%" text-anchor="middle" font-size="20" fill="#22C55E"><textPath href="#p">Hello typing</textPath></text>
            </svg>
            """;
        var animatedPath = Path.Combine(work, "animated.svg");
        File.WriteAllText(animatedPath, animatedSvg, new UTF8Encoding(false));
        var animated = loader.LoadAsync(animatedPath, work).GetAwaiter().GetResult();
        Check("animated svg falls back to static text", animated.Image is not null && animated.Image.Width >= 600, DescribeImage(animated.Image) ?? animated.Error ?? "null");

        var brokenSvg = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("not an svg"));
        var broken = loader.LoadAsync(brokenSvg, null).GetAwaiter().GetResult();
        Check("broken svg reports reason", broken.Image is null && !string.IsNullOrWhiteSpace(broken.Error) && broken.Error.Contains("SVG", StringComparison.OrdinalIgnoreCase), broken.Error ?? "null");

        var markdownPath = Path.Combine(work, "svg-preview.md");
        File.WriteAllText(markdownPath, "![vector](vector.svg)", new UTF8Encoding(false));
        var window = new MainWindow(settings, markdownPath, openedExternally: true);
        window.Show();
        Pump(900);
        SaveShot((FrameworkElement)window.Content, "svg-preview.png");
        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var hosts = FindImageHosts(viewer.Document!).ToList();
        Check("preview svg image loaded", hosts.Any(h => h.Child is Image), hosts.Count(h => h.Child is Image).ToString());
        window.Close();
        Pump(100);
    }

    private static void TestNewWindow(AppSettings settings)
    {
        var window = new MainWindow(settings, null, openedExternally: false);
        window.Show();
        Pump(250);

        var editor = Field<ICSharpCode.AvalonEdit.TextEditor>(window, "Editor");
        var editorHost = Field<Border>(window, "EditorHost");
        var previewHost = Field<Border>(window, "PreviewHost");
        var fileName = Field<TextBlock>(window, "FileNameText");
        var encoding = Field<TextBlock>(window, "EncodingText");
        var pathText = Field<TextBlock>(window, "PathText");

        Check("new window editor visible", editorHost.Visibility == Visibility.Visible);
        Check("editor selection has no border", editor.TextArea.SelectionBorder is null, editor.TextArea.SelectionBorder?.ToString() ?? "null");
        Check("editor selection has square corners", editor.TextArea.SelectionCornerRadius == 0, editor.TextArea.SelectionCornerRadius.ToString());
        Check("new window preview hidden", previewHost.Visibility == Visibility.Collapsed);
        Check("new window filename", fileName.Text == "未命名", fileName.Text);
        Check("new window encoding ANSI", encoding.Text == "ANSI", encoding.Text);
        Check("new window path text", pathText.Text == "尚未保存", pathText.Text);
        Check("new window clean title", window.Title == "未命名 - ALambda Note", window.Title);
        Check("new window empty text", editor.Text.Length == 0, editor.Text);
        Check("new window not dirty", !IsDirty(window));

        editor.Text = "# 临时修改";
        Pump(200);
        Check("typing marks dirty", IsDirty(window));
        Check("dirty title marker", window.Title.StartsWith("* ", StringComparison.Ordinal), window.Title);
        Check("dirty lamp accent", ColorOfElement(Field<System.Windows.Shapes.Ellipse>(window, "DirtyLight")) == ThemeManager.Accent);

        editor.Text = string.Empty;
        Pump(200);
        Check("undo back to empty is clean", !IsDirty(window));
        Check("clean title restored", window.Title == "未命名 - ALambda Note", window.Title);
        Check("window icon present", WindowIcon(window) != IntPtr.Zero, WindowIcon(window).ToString());
        Check("light theme state", !ThemeManager.IsDark);
        Check("light native title bar", DarkTitleBar(window) == 0, DarkTitleBar(window).ToString());
        NativeTheme.Apply(window);
        Pump(120);
        Check("light native title bar after reapply", DarkTitleBar(window) == 0, DarkTitleBar(window).ToString());

        var metrics = RenderWindow(window);
        Check("window renders non blank", metrics.DistinctColors > 6, metrics.DistinctColors.ToString());
        Check("toolbar has icon ink", metrics.ToolbarInk > 30, metrics.ToolbarInk.ToString());
        Check("status bar has ink", metrics.StatusInk > 20, metrics.StatusInk.ToString());
        Check("toolbar background is surface", metrics.ToolbarBg == ColorOf("Surface"), metrics.ToolbarBg + " vs " + ColorOf("Surface"));
        Check("status background is surface", metrics.StatusBg == ColorOf("Surface"), metrics.StatusBg + " vs " + ColorOf("Surface"));

        window.Close();
        Pump(120);
    }

    private static void TestOpenMarkdown(AppSettings settings, string work)
    {
        CreateSampleImage(Path.Combine(work, "local.png"));

        var markdownPath = Path.Combine(work, "sample.md");
        File.WriteAllText(markdownPath, SampleMarkdown, new UTF8Encoding(false));

        var window = new MainWindow(settings, markdownPath, openedExternally: true);
        window.Show();
        Pump(900);

        var editorHost = Field<Border>(window, "EditorHost");
        var previewHost = Field<Border>(window, "PreviewHost");
        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var fileName = Field<TextBlock>(window, "FileNameText");
        var encoding = Field<TextBlock>(window, "EncodingText");

        Check("external open starts in preview", previewHost.Visibility == Visibility.Visible && editorHost.Visibility == Visibility.Collapsed);
        Check("external file name", fileName.Text == "sample.md", fileName.Text);
        Check("sample encoding UTF-8", encoding.Text == "UTF-8", encoding.Text);
        Check("sample clean", !IsDirty(window));
        Check("preview document exists", viewer.Document is not null);
        Check("viewer width set", viewer.Width > 200, viewer.Width.ToString("F0"));
        Check("preview text max width", viewer.Width <= 720.5, viewer.Width.ToString("F0"));

        var document = viewer.Document!;
        var failureText = AllParagraphText(document).FirstOrDefault(t => t.Contains("预览渲染失败", StringComparison.Ordinal));
        Check("no render failure", failureText is null, failureText ?? "");

        Check("preview has table", ContainsBlock<Table>(document));
        Check("preview has code section", ContainsRun(document, "var x = 1;"));
        Check("preview has heading", ContainsRun(document, "标题"));
        Check("preview has task icon", ContainsIconRun(document));
        Check("preview has image host", FindImageHosts(document).Any());
        Check("preview has rule", ContainsBlock<BlockUIContainer>(document));
        Check("preview has link", ContainsHyperlink(document));

        var headingParagraph = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .FirstOrDefault(paragraph => GetParagraphText(paragraph) == "标题");
        Check("heading spacing enlarged", headingParagraph is not null && headingParagraph.Margin.Top >= 44, headingParagraph?.Margin.Top.ToString("F0") ?? "not found");
        Check("h1 uses light accent", headingParagraph is not null
            && headingParagraph.FontWeight == FontWeights.Light
            && BrushColor(headingParagraph.Foreground) == ThemeManager.Accent,
            headingParagraph is null ? "not found" : headingParagraph.FontWeight + "/" + BrushColor(headingParagraph.Foreground));
        Check("h1 size enlarged", headingParagraph is not null && headingParagraph.FontSize >= 38, headingParagraph?.FontSize.ToString("F1") ?? "not found");

        var ruleHost = WalkBlocks(document.Blocks)
            .OfType<BlockUIContainer>()
            .FirstOrDefault(host => host.Child is Border);
        Check("rule spacing enlarged", ruleHost?.Child is Border ruleBorder && ruleBorder.Margin.Top >= 40, ruleHost?.Child is Border b2 ? b2.Margin.Top.ToString("F0") : "not found");

        var tableTexts = AllParagraphText(document).ToList();
        var tableText = tableTexts.Any(t => t.Contains("名称", StringComparison.Ordinal)) && tableTexts.Any(t => t.Contains("甲", StringComparison.Ordinal));
        Check("table content present", tableText);
        var tableHeader = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .FirstOrDefault(paragraph => GetParagraphText(paragraph) == "名称");
        Check("table header follows accent", tableHeader is not null && BrushColor(tableHeader.Foreground) == ThemeManager.Accent,
            tableHeader is null ? "not found" : BrushColor(tableHeader.Foreground).ToString());
        Check("definition list rendered", tableTexts.Any(t => t.Contains("定义内容", StringComparison.Ordinal)));
        var definitionTerm = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .FirstOrDefault(paragraph => GetParagraphText(paragraph) == "术语");
        Check("definition term follows accent", definitionTerm is not null && BrushColor(definitionTerm.Foreground) == ThemeManager.Accent,
            definitionTerm is null ? "not found" : BrushColor(definitionTerm.Foreground).ToString());
        Check("footnote rendered", tableTexts.Any(t => t.Contains("脚注内容", StringComparison.Ordinal)));
        Check("alert block rendered", tableTexts.Any(t => t.Contains("提示块", StringComparison.Ordinal)) && tableTexts.All(t => !t.Contains("[!NOTE]", StringComparison.Ordinal)));

        var listDot = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .OfType<InlineUIContainer>()
            .Select(host => host.Child)
            .OfType<Grid>()
            .SelectMany(grid => grid.Children.OfType<System.Windows.Shapes.Ellipse>())
            .FirstOrDefault();
        Check("unordered list dot uses geometry", listDot is not null, listDot is null ? "not found" : "ellipse");
        Check("unordered list dot follows accent", listDot is not null && BrushColor(listDot.Fill) == ThemeManager.Accent,
            listDot is null ? "not found" : BrushColor(listDot.Fill).ToString());

        Check("preview link accent", FindHyperlinks(document).All(h => BrushColor(h.Foreground) == ThemeManager.Accent));
        var inlineCodeRun = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .OfType<Run>()
            .FirstOrDefault(run => run.Text == "行内代码");
        Check("inline code follows accent", inlineCodeRun is not null && BrushColor(inlineCodeRun.Foreground) == ThemeManager.Accent, inlineCodeRun is null ? "not found" : BrushColor(inlineCodeRun.Foreground).ToString());
        var fencedCodeRun = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .OfType<Run>()
            .FirstOrDefault(run => run.Text == "var x = 1;");
        Check("fenced code follows accent", fencedCodeRun is not null && BrushColor(fencedCodeRun.Foreground) == ThemeManager.Accent,
            fencedCodeRun is null ? "not found" : BrushColor(fencedCodeRun.Foreground).ToString());
        Check("local image loaded", FindImageHosts(document).Any(h => h.Child is Image));

        try
        {
            viewer.Selection.Select(document.ContentStart, document.ContentEnd);
            ApplicationCommands.Copy.Execute(null, viewer);
            Pump(120);
            var clip = Clipboard.GetText();
            Check("preview copy works", clip.Contains("var x = 1;", StringComparison.Ordinal), clip.Length.ToString());
        }
        catch (Exception ex)
        {
            Fail("preview copy works", ex.Message);
        }

        // 主题切换后预览应重建，文档前景换色
        settings.ThemeMode = AppThemeMode.Dark;
        ThemeManager.Apply(settings);
        Pump(400);
        var darkText = ThemeManager.Hex("#E7E5E1");
        Check("dark theme applied", ThemeManager.IsDark && ColorOf("Bg") == ThemeManager.Hex("#1A1C1E"));
        Check("dark native title bar", DarkTitleBar(window) != 0, DarkTitleBar(window).ToString());
        Check("preview rebuilt with dark foreground", BrushColor(viewer.Document?.Foreground) == darkText, BrushColor(viewer.Document?.Foreground).ToString());
        Check("preview still renders after theme switch", viewer.Document is not null && ContainsBlock<Table>(viewer.Document));

        settings.ThemeMode = AppThemeMode.Light;
        ThemeManager.Apply(settings);
        Pump(300);
        Check("light native title bar restored", DarkTitleBar(window) == 0, DarkTitleBar(window).ToString());

        window.Close();
        Pump(120);
    }

    private static void TestEmptyFileOpensEditor(AppSettings settings, string work)
    {
        var variants = new (string Name, byte[] Bytes)[]
        {
            ("empty.md", Array.Empty<byte>()),
            ("empty-bom.md", new byte[] { 0xEF, 0xBB, 0xBF }),
            ("empty-lines.md", Encoding.UTF8.GetBytes("\r\n\r\n"))
        };

        foreach (var variant in variants)
        {
            var path = Path.Combine(work, variant.Name);
            File.WriteAllBytes(path, variant.Bytes);

            var window = new MainWindow(settings, path, openedExternally: true);
            window.Show();
            Pump(350);

            var editorHost = Field<Border>(window, "EditorHost");
            var previewHost = Field<Border>(window, "PreviewHost");
            Check(
                "empty external opens editor: " + variant.Name,
                editorHost.Visibility == Visibility.Visible && previewHost.Visibility == Visibility.Collapsed,
                editorHost.Visibility + "/" + previewHost.Visibility);

            window.Close();
            Pump(120);
        }
    }

    private static void TestSaveRoundtrip(AppSettings settings, string work)
    {
        var path = Path.Combine(work, "save.md");
        File.WriteAllText(path, "old", new UTF8Encoding(false));

        var window = new MainWindow(settings, path, openedExternally: true);
        window.Show();
        Pump(250);

        var editor = Field<ICSharpCode.AvalonEdit.TextEditor>(window, "Editor");
        var lf = ((char)10).ToString();
        editor.Text = "new content" + lf + "second line";
        Pump(180);

        var method = typeof(MainWindow).GetMethod("SaveDocument", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var saved = (bool)method.Invoke(window, new object[] { false })!;
        Pump(150);

        var data = TextFileService.Read(path);
        Check("save returns true", saved);
        Check("save wrote new text", data.Text.Contains("new content", StringComparison.Ordinal) && data.Text.Contains("second line", StringComparison.Ordinal), data.Text);
        Check("save keeps source encoding", data.Encoding.Id == "utf8", data.Encoding.Id);
        Check("save clears dirty", !IsDirty(window));

        window.Close();
        Pump(100);
    }

    private static void TestRename(AppSettings settings, string work)
    {
        var oldPath = Path.Combine(work, "rename-me.md");
        File.WriteAllText(oldPath, "rename", new UTF8Encoding(false));

        var window = new MainWindow(settings, oldPath, openedExternally: true);
        window.Show();
        Pump(300);

        var editor = Field<ICSharpCode.AvalonEdit.TextEditor>(window, "Editor");
        Check("editor font is not monospace",
            !editor.FontFamily.Source.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            && !editor.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            && !editor.FontFamily.Source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase),
            editor.FontFamily.Source);
        var fileNameButton = Field<Button>(window, "FileNameButton");
        fileNameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(60);

        var box = Field<TextBox>(window, "FileNameBox");
        Check("single click file name starts rename", box.Visibility == Visibility.Visible, box.Visibility.ToString());
        Check("rename box visible", box.Visibility == Visibility.Visible, box.Visibility.ToString());
        Check("rename box filled", box.Text == "rename-me.md", box.Text);

        box.Text = "renamed.md";
        var commit = typeof(MainWindow).GetMethod("CommitRename", BindingFlags.Instance | BindingFlags.NonPublic)!;
        commit.Invoke(window, null);
        Pump(120);

        var newPath = Path.Combine(work, "renamed.md");
        Check("rename moved file", File.Exists(newPath) && !File.Exists(oldPath), newPath);
        Check("rename updates title", window.Title.Contains("renamed.md", StringComparison.Ordinal), window.Title);
        Check("rename updates path", Field<TextBlock>(window, "PathText").Text == newPath, Field<TextBlock>(window, "PathText").Text);

        var fresh = new MainWindow(settings, null, openedExternally: false);
        fresh.Show();
        Pump(250);
        var freshButton = Field<Button>(fresh, "FileNameButton");
        freshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(60);
        var freshBox = Field<TextBox>(fresh, "FileNameBox");
        Check("untitled rename edits directly", freshBox.Visibility == Visibility.Visible && freshBox.Text == "未命名", freshBox.Text);
        freshBox.Text = "draft-note.md";
        var commitFresh = typeof(MainWindow).GetMethod("CommitRename", BindingFlags.Instance | BindingFlags.NonPublic)!;
        commitFresh.Invoke(fresh, null);
        Pump(80);
        Check("untitled rename updates title", fresh.Title == "draft-note.md - ALambda Note", fresh.Title);
        Check("untitled rename stays unsaved", Field<TextBlock>(fresh, "PathText").Text == "尚未保存", Field<TextBlock>(fresh, "PathText").Text);
        fresh.Close();
        Pump(100);

        window.Close();
        Pump(100);
    }

    private static void TestWheel(AppSettings settings, string work)
    {
        var path = Path.Combine(work, "wheel.md");
        File.WriteAllLines(path, Enumerable.Range(0, 300).Select(i => "line " + i), new UTF8Encoding(false));

        var window = new MainWindow(settings, path, openedExternally: true);
        window.Show();
        Pump(300);

        var setMode = typeof(MainWindow).GetMethod("SetMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setMode.Invoke(window, new object[] { false });
        Pump(150);

        var editor = Field<ICSharpCode.AvalonEdit.TextEditor>(window, "Editor");
        editor.ScrollToVerticalOffset(0);
        Pump(80);
        var before = editor.VerticalOffset;

        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        editor.RaiseEvent(args);
        Pump(100);

        var after = editor.VerticalOffset;
        Check("wheel scrolls editor", after > before, before.ToString("0.0") + " -> " + after.ToString("0.0"));
        Check("wheel step is generous", after - before > editor.TextArea.TextView.DefaultLineHeight, (after - before).ToString("0.0"));

        var textView = editor.TextArea.TextView;
        Check("editor line height relaxed", textView.DefaultLineHeight >= editor.FontSize * 1.95, (textView.DefaultLineHeight / editor.FontSize).ToString("F2") + "x");
        Check("scrolled visual lines keep relaxed height",
            textView.VisualLinesValid && textView.VisualLines.All(v => Math.Abs(v.Height - textView.DefaultLineHeight) < 0.1),
            textView.VisualLinesValid ? string.Join(",", textView.VisualLines.Select(v => v.Height.ToString("F1")).Distinct().Take(4)) : "invalid");
        SaveShot((FrameworkElement)window.Content, "editor-scrolled.png");

        setMode.Invoke(window, new object[] { true });
        Pump(250);
        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var scroll = FindDescendant<ScrollViewer>(viewer);
        Check("preview has scroll viewer", scroll is not null, "null");
        if (scroll is not null)
        {
            scroll.ScrollToVerticalOffset(0);
            Pump(80);
            var previewBefore = scroll.VerticalOffset;
            var previewArgs = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            };
            viewer.RaiseEvent(previewArgs);
            Pump(100);
            Check("preview wheel scrolls", scroll.VerticalOffset > previewBefore, previewBefore.ToString("0.0") + " -> " + scroll.VerticalOffset.ToString("0.0"));
        }

        window.Close();
        Pump(100);
    }

    private static void TestModeScrollSync(AppSettings settings, string work)
    {
        var path = Path.Combine(work, "sync.md");
        File.WriteAllLines(path, Enumerable.Range(0, 300).Select(i => "line " + i), new UTF8Encoding(false));

        var window = new MainWindow(settings, path, openedExternally: true);
        window.Show();
        Pump(300);
        var setMode = typeof(MainWindow).GetMethod("SetMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setMode.Invoke(window, new object[] { false });
        Pump(200);

        var editor = Field<ICSharpCode.AvalonEdit.TextEditor>(window, "Editor");
        var editorMax = Math.Max(0, editor.ExtentHeight - editor.ViewportHeight);
        editor.ScrollToVerticalOffset(editorMax);
        Pump(120);

        setMode.Invoke(window, new object[] { true });
        Pump(450);
        var viewer = Field<FlowDocumentScrollViewer>(window, "PreviewViewer");
        var scroll = FindDescendant<ScrollViewer>(viewer);
        Check("edit to preview keeps bottom", scroll is not null && scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 3, scroll is null ? "no scroll" : scroll.VerticalOffset + "/" + scroll.ScrollableHeight);

        setMode.Invoke(window, new object[] { false });
        Pump(450);
        var editorMaxAfter = Math.Max(0, editor.ExtentHeight - editor.ViewportHeight);
        Check("preview to edit keeps bottom", editorMaxAfter > 0 && Math.Abs(editor.VerticalOffset - editorMaxAfter) < 3, editor.VerticalOffset + "/" + editorMaxAfter);

        window.Close();
        Pump(100);
    }

    private static void TestDragDrop(AppSettings settings, string work)
    {
        var path = Path.Combine(work, "dropped.md");
        File.WriteAllText(path, "# dropped", new UTF8Encoding(false));

        var window = new MainWindow(settings, null, openedExternally: false);
        window.Show();
        Pump(200);

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        var ctor = typeof(DragEventArgs)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke(new object[]
        {
            data,
            DragDropKeyStates.None,
            DragDropEffects.Copy,
            window,
            new Point(40, 40)
        });
        args.RoutedEvent = DragDrop.PreviewDropEvent;
        window.RaiseEvent(args);
        Pump(450);

        var fileName = Field<TextBlock>(window, "FileNameText");
        var previewHost = Field<Border>(window, "PreviewHost");

        Check("drop opens file", fileName.Text == "dropped.md", fileName.Text);
        Check("drop opens preview", previewHost.Visibility == Visibility.Visible);

        window.Close();
        Pump(100);
    }

    private static void TestDefaultSize()
    {
        var window = new MainWindow(new AppSettings(), null, openedExternally: false);
        window.Show();
        Pump(200);

        Check("default window is portrait", window.Height > window.Width, window.Width + "x" + window.Height);
        Check("default window narrower", window.Width <= 700, window.Width.ToString("F0"));
        Check("default window fits screen", window.Height <= SystemParameters.WorkArea.Height, window.Height + "/" + SystemParameters.WorkArea.Height);

        window.Close();
        Pump(100);
    }

    private static void TestSettingsAndShots(AppSettings settings, string work)
    {
        var window = new MainWindow(settings, Path.Combine(work, "sample.md"), openedExternally: true);
        window.Width = 640;
        window.Height = 840;
        window.Show();
        Pump(900);

        SaveShot((FrameworkElement)window.Content, "main-preview-light.png");

        var setMode = typeof(MainWindow).GetMethod("SetMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setMode.Invoke(window, new object[] { false });
        Pump(250);
        SaveShot((FrameworkElement)window.Content, "main-editor-light.png");

        settings.ThemeMode = AppThemeMode.Dark;
        ThemeManager.Apply(settings);
        Pump(350);
        setMode.Invoke(window, new object[] { true });
        Pump(500);
        SaveShot((FrameworkElement)window.Content, "main-preview-dark.png");
        setMode.Invoke(window, new object[] { false });
        Pump(250);
        SaveShot((FrameworkElement)window.Content, "main-editor-dark.png");

        var darkSettingsWindow = new SettingsWindow(settings);
        darkSettingsWindow.Show();
        Pump(300);
        SaveShot((FrameworkElement)darkSettingsWindow.Content, "settings-dark.png");
        darkSettingsWindow.Close();
        Pump(150);

        settings.ThemeMode = AppThemeMode.Light;
        ThemeManager.Apply(settings);
        Pump(350);

        var settingsWindow = new SettingsWindow(settings);
        settingsWindow.Show();
        Pump(300);

        var accentPanel = Field<WrapPanel>(settingsWindow, "AccentPanel");
        Check("settings accent controls", accentPanel.Children.Count == ThemeManager.Accents.Length + 1, accentPanel.Children.Count.ToString());
        SaveShot((FrameworkElement)settingsWindow.Content, "settings-light.png");

        settingsWindow.Close();
        Pump(150);
        window.Close();
        Pump(100);
    }

    private static void SaveShot(FrameworkElement root, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ALambdaNoteShots");
        Directory.CreateDirectory(dir);
        root.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)(root.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)(root.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(dir, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("SHOT " + path);
    }

    private const string SampleMarkdown = """
# 标题

一段 **粗体** 和 *斜体*，`行内代码`，以及 [链接](https://example.com)。

- [x] 已完成
- [ ] 未完成
  - 嵌套项

> 引用内容

> [!NOTE]
> 提示块

术语
: 定义内容

脚注示例[^1]

[^1]: 脚注内容

~~删除~~ ==高亮==

| 名称 | 数值 |
|:---|---:|
| 甲 | 1 |

```csharp
var x = 1;
```

![本地图片](local.png)

---

结语。
""";

    private static void CreateSampleImage(string path)
    {
        var visual = new Border
        {
            Width = 80,
            Height = 48,
            Background = new SolidColorBrush(ThemeManager.Hex("#6E8062"))
        };

        visual.Measure(new Size(80, 48));
        visual.Arrange(new Rect(0, 0, 80, 48));

        var bitmap = new RenderTargetBitmap(80, 48, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private static IntPtr WindowIcon(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var small = SendMessage(handle, 0x7F, IntPtr.Zero, IntPtr.Zero);
        return small != IntPtr.Zero ? small : SendMessage(handle, 0x7F, (IntPtr)1, IntPtr.Zero);
    }

    private static int DarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return -1;
        }

        return DwmGetWindowAttribute(handle, 20, out var value, sizeof(int)) == 0 ? value : -2;
    }

    private static bool IsDirty(MainWindow window)
    {
        return (bool)typeof(MainWindow)
            .GetProperty("IsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("field not found: " + name);
        return (T)field.GetValue(target)!;
    }

    private static Color ColorOf(string key)
    {
        return ((SolidColorBrush)Application.Current.Resources[key]).Color;
    }

    private static Color ColorOfElement(System.Windows.Shapes.Shape shape)
    {
        return shape.Fill is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    }

    private static Color BrushColor(Brush? brush)
    {
        return brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
    }

    private static IEnumerable<string> AllParagraphText(FlowDocument document)
    {
        foreach (var text in WalkBlocks(document.Blocks).OfType<Paragraph>().Select(GetParagraphText))
        {
            yield return text;
        }
    }

    private static IEnumerable<Block> WalkBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;

            switch (block)
            {
                case Section section:
                    foreach (var child in WalkBlocks(section.Blocks))
                    {
                        yield return child;
                    }

                    break;

                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var child in WalkBlocks(item.Blocks))
                        {
                            yield return child;
                        }
                    }

                    break;

                case Table table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var child in WalkBlocks(cell.Blocks))
                                {
                                    yield return child;
                                }
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static string GetParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        foreach (var inline in paragraph.Inlines)
        {
            AppendInline(inline, builder);
        }

        return builder.ToString();
    }

    private static void AppendInline(Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case Run run:
                builder.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines)
                {
                    AppendInline(child, builder);
                }

                break;
        }
    }

    private static bool ContainsBlock<T>(FlowDocument document) where T : Block
    {
        return WalkBlocks(document.Blocks).OfType<T>().Any();
    }

    private static bool ContainsRun(FlowDocument document, string text)
    {
        return WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .Any(paragraph => GetParagraphText(paragraph).Contains(text, StringComparison.Ordinal));
    }

    private static bool ContainsIconRun(FlowDocument document)
    {
        var inlines = WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .ToList();

        return inlines.OfType<Run>()
                   .Any(run => run.FontFamily.Source.Contains("MaterialSymbolsSharp", StringComparison.OrdinalIgnoreCase))
            || inlines.OfType<InlineUIContainer>()
                      .Any(host => host.Child is TextBlock text && text.FontFamily.Source.Contains("MaterialSymbolsSharp", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Inline> EnumerateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;

            if (inline is Span span)
            {
                foreach (var child in EnumerateInlines(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool HasImage(ImageSource? image)
    {
        return image is { Width: > 0, Height: > 0 };
    }

    private static string? DescribeImage(ImageSource? image)
    {
        if (image is null)
        {
            return null;
        }

        return image is BitmapSource bitmap
            ? bitmap.PixelWidth + "x" + bitmap.PixelHeight + " px"
            : image.Width.ToString("F0") + "x" + image.Height.ToString("F0") + " dip";
    }

    private static IEnumerable<InlineUIContainer> FindImageHosts(FlowDocument document)
    {
        return WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .OfType<InlineUIContainer>();
    }

    private static IEnumerable<Hyperlink> FindHyperlinks(FlowDocument document)
    {
        return WalkBlocks(document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => EnumerateInlines(paragraph.Inlines))
            .OfType<Hyperlink>();
    }

    private static bool ContainsHyperlink(FlowDocument document)
    {
        return FindHyperlinks(document).Any();
    }

    private sealed record RenderMetrics(
        int DistinctColors,
        int ToolbarInk,
        int StatusInk,
        Color ToolbarBg,
        Color StatusBg);

    private static RenderMetrics RenderWindow(Window window)
    {
        window.UpdateLayout();

        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();

        var width = (int)Math.Round(root.ActualWidth);
        var height = (int)Math.Round(root.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)(width * dpi.DpiScaleX)),
            Math.Max(1, (int)(height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        Color Pixel(int x, int y)
        {
            x = Math.Clamp(x, 0, bitmap.PixelWidth - 1);
            y = Math.Clamp(y, 0, bitmap.PixelHeight - 1);
            var offset = y * stride + x * 4;
            return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }

        var colors = new HashSet<Color>();
        var toolbarInk = 0;
        var statusInk = 0;

        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var color = Pixel(x, y);
                colors.Add(color);

                if (y < 46 * dpi.DpiScaleY && x < 340 * dpi.DpiScaleX)
                {
                    if (color != ColorOf("Surface"))
                    {
                        toolbarInk++;
                    }
                }

                if (y > (height - 30) * dpi.DpiScaleY)
                {
                    if (color != ColorOf("Surface"))
                    {
                        statusInk++;
                    }
                }
            }
        }

        return new RenderMetrics(
            colors.Count,
            toolbarInk,
            statusInk,
            Pixel(bitmap.PixelWidth - 4, 8),
            Pixel(bitmap.PixelWidth - 4, bitmap.PixelHeight - 4));
    }

    private static void Pump(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            Log.Add("PASS  " + name + (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
            return;
        }

        _failed++;
        Log.Add("FAIL  " + name + (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Log.Add("FAIL  " + name + "  [" + detail + "]");
    }
}
