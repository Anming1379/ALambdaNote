using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace ALambdaNote.Services;

internal sealed record SvgRenderResult(ImageSource? Image, string? Error);

/// <summary>
/// 用 SharpVectors 把 SVG / SVGZ 解析成 WPF 矢量图。
/// 保留 DrawingImage 而不是栅格化，预览缩放时不会发糊。
/// WPF 绘图对象需要 STA 线程，所以解析放在独立工作线程里。
/// </summary>
internal static class SvgImageRenderer
{
    public static SvgRenderResult Render(byte[] svgBytes)
    {
        byte[] source;
        try
        {
            source = IsGZip(svgBytes) ? Decompress(svgBytes) : svgBytes;
        }
        catch
        {
            return new SvgRenderResult(null, "SVGZ 解压失败");
        }

        SvgRenderResult result = new(null, "SVG 解析失败");
        var thread = new Thread(() =>
        {
            try
            {
                result = RenderOnSta(source);
            }
            catch
            {
                result = new SvgRenderResult(null, "SVG 解析失败");
            }
        })
        {
            IsBackground = true,
            Name = "ALambdaNote.SvgRenderer"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            return new SvgRenderResult(null, "SVG 渲染超时");
        }

        return result;
    }

    private static SvgRenderResult RenderOnSta(byte[] svgBytes)
    {
        // 少数 SVG 对 text / CSS / viewBox 的处理比较敏感，先按最常用的方式解析；
        // 失败时再换更保守的文本和 viewBox 设置重试。
        var attempts = new[]
        {
            new WpfDrawingSettings
            {
                TextAsGeometry = false,
                IncludeRuntime = true,
                OptimizePath = true,
                EnsureViewboxPosition = false,
                ViewBoxMode = ViewBoxMode.Strict
            },
            new WpfDrawingSettings
            {
                TextAsGeometry = false,
                IncludeRuntime = true,
                OptimizePath = false,
                EnsureViewboxPosition = false,
                ViewBoxMode = ViewBoxMode.ComputedUnion
            },
            new WpfDrawingSettings
            {
                TextAsGeometry = true,
                IncludeRuntime = false,
                OptimizePath = true,
                EnsureViewboxPosition = false,
                ViewBoxMode = ViewBoxMode.Strict
            },
            new WpfDrawingSettings
            {
                TextAsGeometry = true,
                IncludeRuntime = false,
                OptimizePath = true,
                EnsureViewboxPosition = false,
                ViewBoxMode = ViewBoxMode.ComputedUnion
            }
        };

        // 动画 / textPath / @font-face 型 SVG 在静态预览里直接使用静态化版本。
        // 否则 SharpVectors 可能“解析成功但只显示动画的初始空白帧”，或只显示第一行。
        byte[]? sanitized = null;
        if (NeedsStaticSanitize(svgBytes))
        {
            sanitized = SanitizeSvg(svgBytes);
            if (sanitized is not null && TryReadAll(sanitized, attempts) is { } staticImage)
            {
                return new SvgRenderResult(staticImage, null);
            }
        }

        foreach (var settings in attempts)
        {
            try
            {
                var image = ReadVector(svgBytes, settings);
                if (image is not null)
                {
                    return new SvgRenderResult(image, null);
                }
            }
            catch
            {
                // 换下一组设置继续尝试。
            }
        }

        // 最后再尝试一次“静态化”版本：去掉动画、内嵌字体和 textPath，
        // 把可静态显示的文字保留下来。只在前面的正常解析失败时使用。
        try
        {
            sanitized ??= SanitizeSvg(svgBytes);
            if (sanitized is not null && TryReadAll(sanitized, attempts) is { } fallbackImage)
            {
                return new SvgRenderResult(fallbackImage, null);
            }
        }
        catch
        {
            // 静态化失败就保留原来的失败结果。
        }

        return new SvgRenderResult(null, "SVG 解析失败");
    }

    private static ImageSource? TryReadAll(byte[] svgBytes, WpfDrawingSettings[] attempts)
    {
        foreach (var settings in attempts)
        {
            try
            {
                var image = ReadVector(svgBytes, settings);
                if (image is not null)
                {
                    return image;
                }
            }
            catch
            {
                // 继续尝试。
            }
        }

        return null;
    }

    private static bool NeedsStaticSanitize(byte[] svgBytes)
    {
        var sampleLength = Math.Min(svgBytes.Length, 65536);
        var sample = Encoding.UTF8.GetString(svgBytes, 0, sampleLength);
        return sample.Contains("<animate", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("<set", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("textPath", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("@font-face", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("@keyframes", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("foreignObject", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("feGaussianBlur", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? ReadVector(byte[] svgBytes, WpfDrawingSettings settings)
    {
        using var reader = new FileSvgReader(settings, false);
        using var stream = new MemoryStream(svgBytes, writable: false);
        var drawing = reader.Read(stream);
        if (drawing is null || drawing.Bounds.IsEmpty)
        {
            return null;
        }

        var bounds = drawing.Bounds;
        if (bounds.Width < 0.01 || bounds.Height < 0.01)
        {
            return null;
        }

        // SharpVectors 的 drawing.Bounds 是内容包围盒，不是 SVG 根画布。
        // 读取 root width / height / viewBox，给矢量图补上原始画布，避免裁掉留白。
        var viewport = ReadViewport(svgBytes);
        var useViewport = viewport is { Width: > 0.01, Height: > 0.01 };
        var width = useViewport ? viewport!.Value.Width : bounds.Width;
        var height = useViewport ? viewport!.Value.Height : bounds.Height;

        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            // 有根画布时 SharpVectors 已经把 viewBox 原点归到 (0,0)；
            // 没有根画布时按内容包围盒平移。
            if (!useViewport)
            {
                context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            }

            // 透明底板负责把自然尺寸固定为 SVG 画布尺寸。
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            context.DrawDrawing(drawing);

            if (!useViewport)
            {
                context.Pop();
            }
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static byte[]? SanitizeSvg(byte[] svgBytes)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(svgBytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = false
            });
            document = XDocument.Load(reader);
        }
        catch
        {
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        document.DocumentType?.Remove();

        // 徽章类 SVG 常把同样的文字复制一份加 GaussianBlur 当阴影。
        // 小尺寸预览里这层阴影会让文字看起来发糊；移除 aria-hidden 的阴影文字组，
        // 保留上面的清晰文字层。
        var blurFilterIds = document.Descendants()
            .Where(element => element.Name.LocalName == "filter"
                && element.Descendants().Any(child => child.Name.LocalName == "feGaussianBlur"))
            .Select(element => GetAttribute(element, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        if (blurFilterIds.Count > 0)
        {
            foreach (var hiddenGroup in document.Descendants()
                         .Where(element => element.Name.LocalName == "g"
                             && string.Equals(GetAttribute(element, "aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)
                             && element.Descendants().Any(child =>
                                 child.Name.LocalName == "text"
                                 && ReferencesFilter(child, blurFilterIds)))
                         .ToList())
            {
                hiddenGroup.Remove();
            }
        }

        // 内嵌 @font-face 字体数据会让部分解析器直接失败；清掉字体声明，保留其他 CSS。
        foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "style").ToList())
        {
            var css = style.Value;
            var cleaned = Regex.Replace(css, @"@font-face\s*\{[^}]*\}", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // 静态预览不执行 CSS 动画；否则淡入/位移类动画可能停在初始不可见帧。
            cleaned = Regex.Replace(cleaned, @"@keyframes\s+[^{]+\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleaned = Regex.Replace(cleaned, @"(?:-webkit-)?animation(?:-[a-z-]+)?\s*:[^;}]*;?", string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"(?:-webkit-)?transition(?:-[a-z-]+)?\s*:[^;}]*;?", string.Empty, RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                style.Remove();
            }
            else
            {
                style.Value = cleaned;
            }
        }

        // 动画对静态预览没有意义，先移除；带 d 动画的 path 取一个最完整的静态值。
        foreach (var animation in document.Descendants()
                     .Where(element => element.Name.LocalName.StartsWith("animate", StringComparison.OrdinalIgnoreCase)
                         || element.Name.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var parent = animation.Parent;
            var attributeName = GetAttribute(animation, "attributeName");
            if (parent is not null
                && parent.Name.LocalName == "path"
                && string.Equals(attributeName, "d", StringComparison.OrdinalIgnoreCase)
                && parent.Attribute("d") is null)
            {
                var values = GetAttribute(animation, "values");
                var staticD = values?
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .OrderByDescending(value => value.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(staticD))
                {
                    parent.SetAttributeValue("d", staticD);
                }
            }

            animation.Remove();
        }

        // textPath 是文本路径，静态兜底时把文字拉回普通 text，保留可见内容。
        var flattenedTexts = new List<XElement>();
        foreach (var text in document.Descendants().Where(element => element.Name.LocalName == "text").ToList())
        {
            var textPath = text.Descendants().FirstOrDefault(element => element.Name.LocalName == "textPath");
            if (textPath is null)
            {
                continue;
            }

            var content = textPath.Value.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var x = GetAttribute(text, "x") ?? "50%";
            var y = GetAttribute(text, "y");
            if (y is null)
            {
                y = FindTextPathY(document, GetAttribute(textPath, "href"))
                    ?? GetAttribute(textPath, "xlink:href")
                    ?? "50%";
            }

            text.SetAttributeValue("x", x);
            text.SetAttributeValue("y", y);
            if (GetAttribute(text, "text-anchor") is null)
            {
                text.SetAttributeValue("text-anchor", "middle");
            }

            text.ReplaceNodes(new XText(content));
            flattenedTexts.Add(text);
        }

        // 动画型的 typing SVG 往往有两条重叠的 textPath，用动画轮流显示。
        // 静态预览里不再删掉第二条，而是把同位置的多条文字纵向叠开，避免内容丢失。
        var textGroups = flattenedTexts
            .GroupBy(text => (GetAttribute(text, "x") ?? string.Empty)
                + "|" + (GetAttribute(text, "y") ?? string.Empty))
            .ToList();
        foreach (var group in textGroups)
        {
            var texts = group.ToList();
            if (texts.Count <= 1)
            {
                continue;
            }

            if (texts.Select(text => text.Value.Trim()).Distinct(StringComparer.Ordinal).Count() <= 1)
            {
                foreach (var duplicate in texts.Skip(1))
                {
                    duplicate.Remove();
                }

                continue;
            }

            var baseY = ParseLength(GetAttribute(texts[0], "y"));
            if (baseY is null)
            {
                foreach (var duplicate in texts.Skip(1))
                {
                    duplicate.Remove();
                }

                continue;
            }

            var fontSize = ParseLength(GetAttribute(texts[0], "font-size")) ?? 16;
            var gap = Math.Max(12, fontSize * 1.25);
            for (var i = 1; i < texts.Count; i++)
            {
                texts[i].SetAttributeValue(
                    "y",
                    (baseY.Value + gap * i).ToString("0.###", CultureInfo.InvariantCulture));
            }

            ExpandViewportHeight(document, gap * (texts.Count - 1));
        }

        // 少数宣传图用 foreignObject 放 HTML 文字；SharpVectors 不支持时，
        // 静态化兜底把其中的纯文本提成一个普通 text，至少保留可见内容。
        foreach (var foreign in document.Descendants()
                     .Where(element => element.Name.LocalName == "foreignObject")
                     .ToList())
        {
            var textValue = Regex.Replace(foreign.Value, @"\s+", " ").Trim();
            if (textValue.Length == 0)
            {
                foreign.Remove();
                continue;
            }

            var x = ParseLength(GetAttribute(foreign, "x")) ?? 0;
            var y = ParseLength(GetAttribute(foreign, "y")) ?? 0;
            var width = ParseLength(GetAttribute(foreign, "width")) ?? 0;
            var height = ParseLength(GetAttribute(foreign, "height")) ?? 0;
            var fontSize = Math.Clamp(height * 0.35, 12, 64);
            var text = new XElement(
                foreign.Name.Namespace + "text",
                new XAttribute("x", (x + width / 2).ToString("0.###", CultureInfo.InvariantCulture)),
                new XAttribute("y", (y + height / 2 + fontSize * 0.35).ToString("0.###", CultureInfo.InvariantCulture)),
                new XAttribute("font-size", fontSize.ToString("0.###", CultureInfo.InvariantCulture)),
                new XAttribute("text-anchor", "middle"),
                new XAttribute("fill", GetAttribute(document.Root!, "fill") ?? "#000000"),
                new XText(textValue));
            foreign.ReplaceWith(text);
        }

        // 没有实际 paint 的 path 容易让解析器判定为空图；加透明描边只是为了让几何保留。
        foreach (var path in document.Descendants().Where(element => element.Name.LocalName == "path"))
        {
            var fill = GetAttribute(path, "fill");
            var stroke = GetAttribute(path, "stroke");
            var hasStyle = GetAttribute(path, "style") is not null || GetAttribute(path, "class") is not null;
            if (!hasStyle
                && string.IsNullOrWhiteSpace(stroke)
                && (string.IsNullOrWhiteSpace(fill) || fill.Equals("none", StringComparison.OrdinalIgnoreCase)))
            {
                path.SetAttributeValue("stroke", "#00000000");
            }
        }

        using var output = new MemoryStream();
        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false
        };
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    private static void ExpandViewportHeight(XDocument document, double extra)
    {
        if (extra <= 0)
        {
            return;
        }

        var root = document.Root;
        if (root is null)
        {
            return;
        }

        var height = ParseLength(GetAttribute(root, "height"));
        if (height is > 0)
        {
            root.SetAttributeValue(
                "height",
                (height.Value + extra).ToString("0.###", CultureInfo.InvariantCulture));
        }

        var viewBox = ParseViewBox(GetAttribute(root, "viewBox"));
        if (viewBox is { Height: > 0 })
        {
            root.SetAttributeValue(
                "viewBox",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{viewBox.Value.X:0.###} {viewBox.Value.Y:0.###} {viewBox.Value.Width:0.###} {viewBox.Value.Height + extra:0.###}"));
        }
    }

    private static string? FindTextPathY(XDocument document, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || !href.StartsWith('#'))
        {
            return null;
        }

        var id = href[1..];
        var path = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "path"
            && string.Equals(GetAttribute(element, "id"), id, StringComparison.Ordinal));
        if (path is null)
        {
            return null;
        }

        var data = GetAttribute(path, "d");
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        var match = Regex.Match(data, @"[mM]\s*([+-]?\d+(?:\.\d+)?)[\s,]+([+-]?\d+(?:\.\d+)?)");
        return match.Success ? match.Groups[2].Value : null;
    }

    private static Rect? ReadViewport(byte[] svgBytes)
    {
        try
        {
            using var stream = new MemoryStream(svgBytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true
            });
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var width = ParseLength(GetAttribute(root, "width"));
            var height = ParseLength(GetAttribute(root, "height"));
            var viewBox = ParseViewBox(GetAttribute(root, "viewBox"));

            if (width is > 0 && height is > 0)
            {
                return new Rect(0, 0, width.Value, height.Value);
            }

            if (viewBox is { Width: > 0, Height: > 0 })
            {
                return viewBox;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ReferencesFilter(XElement element, HashSet<string> filterIds)
    {
        var filter = GetAttribute(element, "filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        return filterIds.Any(id => filter.Contains("#" + id, StringComparison.Ordinal));
    }

    private static string? GetAttribute(XElement element, string name)
    {
        return element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(
            value,
            @"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([a-zA-Z%]*)\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToLowerInvariant();
        var result = unit switch
        {
            "" or "px" => number,
            "pt" => number * 96 / 72,
            "pc" => number * 16,
            "in" => number * 96,
            "cm" => number * 96 / 2.54,
            "mm" => number * 96 / 25.4,
            "em" or "rem" => number * 16,
            "ex" => number * 8,
            _ => double.NaN
        };

        return double.IsFinite(result) && result > 0 ? result : null;
    }

    private static Rect? ParseViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers[2] > 0 && numbers[3] > 0
            ? new Rect(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
    }

    private static bool IsGZip(byte[] bytes)
    {
        return bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > 40L * 1024 * 1024)
            {
                throw new InvalidDataException("SVGZ 过大");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
