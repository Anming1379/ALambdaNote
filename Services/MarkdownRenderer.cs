using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdList = Markdig.Syntax.ListBlock;
using MdListItem = Markdig.Syntax.ListItemBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace ALambdaNote.Services;

/// <summary>预览区使用的颜色与字体。</summary>
public sealed class MarkdownStyle
{
    public required Brush Text { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Accent { get; init; }
    public required Brush AccentSoft { get; init; }
    public required Brush AccentLine { get; init; }
    public required Brush Border { get; init; }
    public required Brush CodeBg { get; init; }
    public required Brush CodeText { get; init; }
    public required FontFamily BodyFont { get; init; }
    public required FontFamily MonoFont { get; init; }
    public required FontFamily IconFont { get; init; }
    public double FontSize { get; init; } = 17;

    public static MarkdownStyle FromTheme(double fontSize)
    {
        static Brush Brush(string key, Brush fallback)
        {
            return Application.Current?.TryFindResource(key) as Brush ?? fallback;
        }

        static FontFamily Font(string key, FontFamily fallback)
        {
            return Application.Current?.TryFindResource(key) as FontFamily ?? fallback;
        }

        return new MarkdownStyle
        {
            Text = Brush("Text", Brushes.Black),
            Muted = Brush("TextMuted", Brushes.Gray),
            Accent = Brush("Accent", Brushes.SteelBlue),
            AccentSoft = Brush("AccentSoft", Brushes.LightSteelBlue),
            AccentLine = Brush("AccentLine", Brushes.Gainsboro),
            Border = Brush("Border", Brushes.Gainsboro),
            CodeBg = Brush("CodeBg", Brushes.WhiteSmoke),
            CodeText = Brush("CodeText", Brushes.SteelBlue),
            BodyFont = Font("BodyFont", new FontFamily("Segoe UI")),
            MonoFont = Font("MonoFont", new FontFamily("Consolas")),
            IconFont = Font("IconFont", new FontFamily("Segoe UI")),
            FontSize = fontSize
        };
    }
}

/// <summary>把 Markdig 的语法树翻译成 WPF 的 FlowDocument。</summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseAutoIdentifiers()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseYamlFrontMatter()
        .UseMathematics()
        .UseAlertBlocks()
        .Build();

    private static readonly Regex HtmlImageTagRegex = new(
        @"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex HtmlAttributeRegex = new(
        @"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:\x22(?<v1>[^\x22]*)\x22|'(?<v2>[^']*)'|(?<v3>[^\s>]+))",
        RegexOptions.Compiled);

    private static readonly Regex LooseImageLineRegex = new(
        @"^!\[(?<alt>[^\]\n]*)\]\((?<dest>[^()\n]*(?:\([^()\n]*\)[^()\n]*)*)\)$",
        RegexOptions.Compiled);

    private static readonly Regex LooseImageInlineRegex = new(
        @"!\[(?<alt>[^\]\n]*)\]\((?<dest>[^()\n]*(?:\([^()\n]*\)[^()\n]*)*)\)",
        RegexOptions.Compiled);

    private static readonly Regex TrailingTitleRegex = new(
        @"^(?<url>.*?)(?<title>\s+(?:\x22[^\x22]*\x22|'[^']*'))?$",
        RegexOptions.Compiled);

    private static readonly Regex LooseImageExtensionRegex = new(
        @"\.(png|jpe?g|jfif|gif|bmp|webp|svgz?|avif|ico|tiff?|heic|heif|wmp|jxl)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<Image> _images = new();
    private readonly List<Border> _placeholders = new();

    private ImageLoader _loader = new();
    private string? _baseDirectory;
    private double _maxWidth = 860;
    private int _generation;

    private sealed record Ctx(MarkdownStyle Style, string? BaseDirectory, bool Compact = false, int Depth = 0, Brush? Foreground = null);

    public FlowDocument Render(string markdown, MarkdownStyle style, string? baseDirectory, ImageLoader images)
    {
        _images.Clear();
        _placeholders.Clear();
        _baseDirectory = baseDirectory;
        _loader = images;
        _generation++;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = 100000,
            FontFamily = style.BodyFont,
            FontSize = style.FontSize,
            Foreground = style.Text,
            LineHeight = style.FontSize * 1.8,
            TextAlignment = TextAlignment.Left
        };

        var ctx = new Ctx(style, baseDirectory);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            document.Blocks.Add(new Paragraph(new Run("空文档"))
            {
                Foreground = style.Muted,
                FontSize = style.FontSize - 1
            });
            return document;
        }

        try
        {
            var ast = Markdown.Parse(markdown, Pipeline);
            AddBlocks(ast, document.Blocks, ctx);
        }
        catch (Exception ex)
        {
            document.Blocks.Add(new Paragraph(new Run("预览渲染失败：" + ex.Message))
            {
                Foreground = style.Muted
            });
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("空文档")) { Foreground = style.Muted });
        }

        return document;
    }

    /// <summary>窗口尺寸变化时同步图片宽度。</summary>
    public void SetMaxContentWidth(double width)
    {
        _maxWidth = Math.Clamp(width, 240, 1400);
        foreach (var image in _images)
        {
            image.MaxWidth = _maxWidth;
        }

        foreach (var placeholder in _placeholders)
        {
            placeholder.MaxWidth = _maxWidth;
        }
    }

    private void AddBlocks(ContainerBlock container, BlockCollection target, Ctx ctx)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case YamlFrontMatterBlock:
                    break;

                case HeadingBlock heading:
                    AddHeading(heading, target, ctx);
                    break;

                case ParagraphBlock paragraph:
                    AddParagraph(paragraph, target, ctx);
                    break;

                case AlertBlock alert:
                    AddQuote(alert, target, ctx, alert.Kind.ToString());
                    break;

                case QuoteBlock quote:
                    AddQuote(quote, target, ctx, null);
                    break;

                case MdList list:
                    AddList(list, target, ctx);
                    break;

                case FencedCodeBlock fenced:
                    AddCode(fenced, fenced.Info, target, ctx);
                    break;

                case CodeBlock code:
                    AddCode(code, null, target, ctx);
                    break;

                case MdTable table:
                    AddTable(table, target, ctx);
                    break;

                case ThematicBreakBlock:
                    AddRule(target, ctx);
                    break;

                case DefinitionTerm term:
                    AddParagraph(term, target, ctx, bold: true);
                    break;

                case DefinitionItem item:
                    AddDefinitionItem(item, target, ctx);
                    break;

                case FootnoteGroup footnotes:
                    AddFootnotes(footnotes, target, ctx);
                    break;

                case HtmlBlock html:
                    AddHtmlBlock(html.Lines.ToString(), target, ctx);
                    break;

                case ContainerBlock containerBlock:
                    AddBlocks(containerBlock, target, ctx);
                    break;
            }
        }
    }

    private void AddHeading(HeadingBlock heading, BlockCollection target, Ctx ctx)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var sizes = new[] { 2.35, 1.72, 1.4, 1.16, 1.02, 0.92 };
        var style = ctx.Style;
        var top = level switch { 1 => 46, 2 => 32, 3 => 25, 4 => 20, _ => 16 };
        var bottom = level switch { 1 => 30, 2 => 16, 3 => 12, 4 => 9, _ => 7 };
        if (ctx.Compact)
        {
            top = Math.Max(8, (int)Math.Round(top * 0.6));
            bottom = Math.Max(5, (int)Math.Round(bottom * 0.6));
        }

        var paragraph = new Paragraph
        {
            FontSize = style.FontSize * sizes[level - 1],
            FontWeight = level <= 2 ? FontWeights.Light : level >= 5 ? FontWeights.SemiBold : FontWeights.Bold,
            Foreground = level <= 2 ? style.Accent : style.Text,
            Margin = new Thickness(0, top, 0, bottom),
            LineHeight = style.FontSize * sizes[level - 1] * 1.25,
            TextAlignment = level == 1 ? TextAlignment.Center : TextAlignment.Left
        };

        RenderInlines(heading.Inline, paragraph.Inlines, ctx);
        target.Add(paragraph);
    }

    private void AddParagraph(ParagraphBlock paragraphBlock, BlockCollection target, Ctx ctx, bool bold = false)
    {
        if (paragraphBlock.Inline is { FirstChild: LinkInline { IsImage: true } image }
            && image.NextSibling is null)
        {
            var imageParagraph = new Paragraph
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, ctx.Style.FontSize * 1.2)
            };
            AddImage(image, imageParagraph.Inlines, ctx);
            target.Add(imageParagraph);
            return;
        }

        if (paragraphBlock.Inline is { } inline
            && !ContainsImage(inline)
            && TryParseLooseImage(inline) is { } loose)
        {
            // 有些本地路径带空格，Markdown 语法本身不认；这里兜底当成图片处理。
            var looseParagraph = new Paragraph
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, ctx.Style.FontSize * 1.2)
            };

            AddImageElement(loose.Url, loose.Alt, looseParagraph.Inlines, ctx);
            target.Add(looseParagraph);
            return;
        }

        if (paragraphBlock.Inline is { } inlineContent
            && !ContainsImage(inlineContent)
            && TryAddLooseInlineImages(inlineContent, target, ctx))
        {
            return;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, ctx.Compact ? 5 : ctx.Style.FontSize),
            Foreground = ctx.Foreground ?? ctx.Style.Text,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };

        RenderInlines(paragraphBlock.Inline, paragraph.Inlines, ctx);
        if (paragraph.Inlines.Count > 0)
        {
            target.Add(paragraph);
        }
    }

    private void AddParagraph(DefinitionTerm term, BlockCollection target, Ctx ctx, bool bold)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 2),
            FontWeight = FontWeights.SemiBold,
            Foreground = ctx.Style.Accent
        };
        RenderInlines(term.Inline, paragraph.Inlines, ctx);
        target.Add(paragraph);
    }

    private void AddQuote(QuoteBlock quote, BlockCollection target, Ctx ctx, string? kind)
    {
        var style = ctx.Style;
        var section = new Section
        {
            BorderBrush = style.Accent,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(16, 3, 0, 3),
            Margin = new Thickness(0, 3, 0, ctx.Compact ? 8 : 18),
            Foreground = style.Muted
        };

        if (!string.IsNullOrWhiteSpace(kind))
        {
            section.Blocks.Add(new Paragraph(new Run(kind.ToUpperInvariant()))
            {
                Foreground = style.Accent,
                FontWeight = FontWeights.Bold,
                FontSize = style.FontSize - 3,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        AddBlocks(quote, section.Blocks, ctx with { Compact = true, Foreground = style.Muted });
        if (section.Blocks.Count > 0)
        {
            target.Add(section);
        }
    }

    private void AddList(MdList listBlock, BlockCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = TextMarkerStyle.None,
            MarkerOffset = 0,
            Padding = new Thickness(ctx.Depth == 0 ? 34 : 20, 0, 0, 0),
            Foreground = style.Accent,
            Margin = new Thickness(0, 3, 0, ctx.Compact ? 6 : 14)
        };

        var startIndex = 1;
        if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out var parsed))
        {
            startIndex = parsed;
        }

        var itemIndex = 0;
        foreach (var child in listBlock)
        {
            if (child is not MdListItem item)
            {
                continue;
            }

            var isTask = IsTaskItem(item);
            var markerWidth = 22.0;
            var markerHeight = Math.Max(20, style.FontSize * 1.8);
            var listItem = new ListItem();

            AddBlocks(item, listItem.Blocks, ctx with { Compact = true, Depth = ctx.Depth + 1 });

            if (listItem.Blocks.FirstBlock is Paragraph firstParagraph)
            {
                firstParagraph.TextIndent = -markerWidth;

                if (isTask
                    && firstParagraph.Inlines.Count > 1
                    && firstParagraph.Inlines.ElementAt(1) is Run leadingRun
                    && leadingRun.Text.StartsWith(' '))
                {
                    leadingRun.Text = leadingRun.Text.TrimStart();
                }

                if (!isTask)
                {
                    var label = listBlock.IsOrdered
                        ? (startIndex + itemIndex).ToString() + "."
                        : "•";

                    UIElement markerContent;
                    if (listBlock.IsOrdered)
                    {
                        markerContent = new TextBlock
                        {
                            Text = label,
                            Width = markerWidth,
                            Height = markerHeight,
                            LineHeight = markerHeight,
                            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                            TextAlignment = TextAlignment.Center,
                            FontSize = style.FontSize,
                            Foreground = style.Accent,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    else
                    {
                        // 用几何圆点代替字体里的“•”，避免字体基线导致圆点上下偏移。
                        var dot = new Grid
                        {
                            Width = markerWidth,
                            Height = markerHeight,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var diameter = Math.Max(4, style.FontSize * 0.34);
                        dot.Children.Add(new System.Windows.Shapes.Ellipse
                        {
                            Width = diameter,
                            Height = diameter,
                            Fill = style.Accent,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        markerContent = dot;
                    }

                    var marker = new InlineUIContainer(markerContent)
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    };

                    if (firstParagraph.Inlines.FirstInline is { } firstInline)
                    {
                        firstParagraph.Inlines.InsertBefore(firstInline, marker);
                    }
                    else
                    {
                        firstParagraph.Inlines.Add(marker);
                    }
                }
            }

            if (listItem.Blocks.Count == 0)
            {
                listItem.Blocks.Add(new Paragraph());
            }

            list.ListItems.Add(listItem);
            itemIndex++;
        }

        if (list.ListItems.Count > 0)
        {
            target.Add(list);
        }
    }

    private static bool IsTaskItem(MdListItem item)
    {
        return item.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.FirstChild is TaskList;
    }

    private void AddDefinitionItem(DefinitionItem item, BlockCollection target, Ctx ctx)
    {
        var section = new Section
        {
            Padding = new Thickness(16, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8)
        };

        AddBlocks(item, section.Blocks, ctx with { Compact = true });
        if (section.Blocks.Count > 0)
        {
            target.Add(section);
        }
    }

    private void AddFootnotes(FootnoteGroup group, BlockCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var section = new Section
        {
            BorderBrush = style.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 12, 0, 8),
            Padding = new Thickness(0, 10, 0, 0),
            FontSize = style.FontSize - 1
        };

        foreach (var child in group)
        {
            if (child is not Footnote footnote)
            {
                continue;
            }

            section.Blocks.Add(new Paragraph(new Run($"[{footnote.Order}]"))
            {
                Foreground = style.Accent,
                FontWeight = FontWeights.SemiBold,
                FontSize = style.FontSize - 2,
                Margin = new Thickness(0, 4, 0, 1)
            });

            AddBlocks(footnote, section.Blocks, ctx with { Compact = true });
        }

        if (section.Blocks.Count > 0)
        {
            target.Add(section);
        }
    }

    private void AddCode(CodeBlock codeBlock, string? info, BlockCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var code = codeBlock.Lines.ToString()
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\t", "    ")
            .TrimEnd('\n');
        var fontSize = Math.Max(12, style.FontSize - 1);

        var section = new Section
        {
            Background = style.CodeBg,
            BorderBrush = style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 13, 18, 15),
            Margin = new Thickness(0, 3, 0, ctx.Compact ? 10 : 20),
            FontFamily = style.MonoFont,
            FontSize = fontSize,
            Foreground = style.Accent
        };

        if (!string.IsNullOrWhiteSpace(info))
        {
            section.Blocks.Add(new Paragraph(new Run(info.Trim()))
            {
                Foreground = style.Accent,
                FontFamily = style.BodyFont,
                FontSize = style.FontSize - 3,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9)
            });
        }

        var lines = code.Length == 0 ? new[] { " " } : code.Split('\n');
        foreach (var line in lines)
        {
            section.Blocks.Add(new Paragraph(new Run(line.Length == 0 ? " " : line))
            {
                Margin = new Thickness(0),
                LineHeight = fontSize * 1.7,
                TextAlignment = TextAlignment.Left
            });
        }

        target.Add(section);
    }

    private void AddRawBlock(string text, string label, BlockCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var section = new Section
        {
            Background = style.CodeBg,
            BorderBrush = style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 9, 14, 10),
            Margin = new Thickness(0, 2, 0, 14),
            FontFamily = style.MonoFont,
            FontSize = Math.Max(11.5, style.FontSize - 2),
            Foreground = style.Muted
        };

        section.Blocks.Add(new Paragraph(new Run(label))
        {
            FontFamily = style.BodyFont,
            FontSize = style.FontSize - 3,
            FontWeight = FontWeights.SemiBold,
            Foreground = style.Accent,
            Margin = new Thickness(0, 0, 0, 5)
        });

        section.Blocks.Add(new Paragraph(new Run(text.TrimEnd())) { Margin = new Thickness(0) });
        target.Add(section);
    }

    private void AddTable(MdTable tableBlock, BlockCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var columns = tableBlock.ColumnDefinitions.Count;
        var rowCount = 0;

        foreach (var child in tableBlock)
        {
            if (child is not MdTableRow row)
            {
                continue;
            }

            rowCount++;
            var needed = 0;
            foreach (var childCell in row)
            {
                if (childCell is not MdTableCell cell)
                {
                    continue;
                }

                needed = Math.Max(needed, cell.ColumnIndex + Math.Max(1, cell.ColumnSpan));
            }

            columns = Math.Max(columns, needed);
        }

        if (columns == 0 || rowCount == 0)
        {
            return;
        }

        var table = new WpfTable
        {
            CellSpacing = 0,
            BorderBrush = style.Border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 3, 0, ctx.Compact ? 10 : 20)
        };

        for (var i = 0; i < columns; i++)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var child in tableBlock)
        {
            if (child is not MdTableRow row)
            {
                continue;
            }

            var tableRow = new WpfTableRow
            {
                Background = row.IsHeader ? style.AccentSoft : Brushes.Transparent
            };

            foreach (var childCell in row)
            {
                if (childCell is not MdTableCell cell)
                {
                    continue;
                }

                var tableCell = new WpfTableCell
                {
                    Padding = new Thickness(11, 8, 11, 8),
                    BorderBrush = style.Border,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    ColumnSpan = Math.Max(1, cell.ColumnSpan),
                    TextAlignment = ResolveAlignment(tableBlock, cell)
                };

                if (row.IsHeader)
                {
                    tableCell.FontWeight = FontWeights.SemiBold;
                }

                AddBlocks(cell, tableCell.Blocks, ctx with { Compact = true, Foreground = row.IsHeader ? style.Accent : null });
                if (tableCell.Blocks.Count == 0)
                {
                    tableCell.Blocks.Add(new Paragraph());
                }

                tableRow.Cells.Add(tableCell);
            }

            group.Rows.Add(tableRow);
        }

        target.Add(table);
    }

    private static TextAlignment ResolveAlignment(MdTable table, MdTableCell cell)
    {
        if (cell.ColumnIndex < 0 || cell.ColumnIndex >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[cell.ColumnIndex].Alignment?.ToString() switch
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private void AddRule(BlockCollection target, Ctx ctx)
    {
        target.Add(new BlockUIContainer(new Border
        {
            Height = 1,
            Background = ctx.Style.AccentLine,
            Margin = new Thickness(0, 44, 0, 44)
        }));
    }

    private void RenderInlines(ContainerInline? container, InlineCollection target, Ctx ctx)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            RenderInline(inline, target, ctx);
        }
    }

    private void RenderInline(Markdig.Syntax.Inlines.Inline inline, InlineCollection target, Ctx ctx)
    {
        var style = ctx.Style;

        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
                AddEmphasis(emphasis, target, ctx);
                break;

            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = style.MonoFont,
                    Background = style.AccentSoft,
                    Foreground = style.Accent,
                    FontSize = Math.Max(11, style.FontSize - 0.5)
                });
                break;

            case MathInline math:
                target.Add(new Run("$" + math.Content.ToString() + "$")
                {
                    FontFamily = style.MonoFont,
                    FontStyle = FontStyles.Italic,
                    Foreground = style.Accent
                });
                break;

            case LinkInline link when link.IsImage:
                AddImage(link, target, ctx);
                break;

            case LinkInline link:
                AddHyperlink(link.Url ?? string.Empty, link, target, ctx);
                break;

            case AutolinkInline autolink:
                var autolinkUrl = autolink.IsEmail
                    && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? "mailto:" + autolink.Url
                        : autolink.Url;
                AddHyperlink(autolinkUrl, null, target, ctx, autolink.Url);
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case TaskList task:
                target.Add(new InlineUIContainer(new TextBlock
                {
                    Text = task.Checked ? "\uE834" : "\uE835",
                    FontFamily = style.IconFont,
                    FontSize = style.FontSize * 0.95,
                    Width = 22,
                    Height = Math.Max(20, style.FontSize * 1.8),
                    LineHeight = Math.Max(20, style.FontSize * 1.8),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = task.Checked ? style.Accent : style.Muted
                })
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
                break;

            case FootnoteLink footnoteLink:
                target.Add(new Run($"[{footnoteLink.Index}]")
                {
                    Foreground = style.Accent,
                    FontSize = style.FontSize - 3,
                    FontWeight = FontWeights.SemiBold,
                    BaselineAlignment = BaselineAlignment.Superscript
                });
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case HtmlInline html:
                AddHtmlInline(html.Tag, target, ctx);
                break;

            case ContainerInline container:
                RenderInlines(container, target, ctx);
                break;
        }
    }

    private void AddEmphasis(EmphasisInline emphasis, InlineCollection target, Ctx ctx)
    {
        var style = ctx.Style;
        var span = new Span();
        var count = emphasis.DelimiterCount;

        switch (emphasis.DelimiterChar)
        {
            case '*':
            case '_':
                if (count >= 2)
                {
                    span.FontWeight = FontWeights.Bold;
                }

                if (count % 2 == 1)
                {
                    span.FontStyle = FontStyles.Italic;
                }
                break;

            case '~':
                if (count >= 2)
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }
                else
                {
                    span.BaselineAlignment = BaselineAlignment.Subscript;
                    span.FontSize = style.FontSize * 0.82;
                }
                break;

            case '=':
                span.Background = style.AccentSoft;
                break;

            case '+':
                span.TextDecorations = TextDecorations.Underline;
                break;

            case '^':
                span.BaselineAlignment = BaselineAlignment.Superscript;
                span.FontSize = style.FontSize * 0.82;
                break;
        }

        RenderInlines(emphasis, span.Inlines, ctx);
        target.Add(span);
    }

    private void AddHyperlink(string url, ContainerInline? children, InlineCollection target, Ctx ctx, string? display = null)
    {
        var style = ctx.Style;
        var targetUrl = NormalizeUrl(url);

        var hyperlink = new Hyperlink
        {
            Foreground = style.Accent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = targetUrl,
            TextDecorations = null
        };

        hyperlink.Click += (_, args) =>
        {
            OpenLink(targetUrl);
            args.Handled = true;
        };

        hyperlink.MouseEnter += (_, _) => hyperlink.TextDecorations = TextDecorations.Underline;
        hyperlink.MouseLeave += (_, _) => hyperlink.TextDecorations = null;

        if (children is not null)
        {
            RenderInlines(children, hyperlink.Inlines, ctx);
        }

        if (hyperlink.Inlines.Count == 0)
        {
            hyperlink.Inlines.Add(new Run(display ?? targetUrl));
        }

        target.Add(hyperlink);
    }

    private static string NormalizeUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value;
    }

    private void OpenLink(string url)
    {
        url = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeMailto))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // 打不开就算了，不要打断阅读。
            }

            return;
        }

        OpenLocalPath(url);
    }

    private void OpenLocalPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return;
        }

        try
        {
            var path = url.Split('#')[0];
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                path = new Uri(path).LocalPath;
            }

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(_baseDirectory))
            {
                path = Path.Combine(_baseDirectory, path);
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略无效链接。
        }
    }

    private void AddImage(LinkInline link, InlineCollection target, Ctx ctx)
    {
        AddImageElement(link.Url ?? string.Empty, GetPlainText(link), target, ctx);
    }

    private static bool ContainsImage(ContainerInline container)
    {
        foreach (var child in container)
        {
            if (child is LinkInline { IsImage: true })
            {
                return true;
            }

            if (child is ContainerInline nested && ContainsImage(nested))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Url, string Alt)? TryParseLooseImage(ContainerInline container)
    {
        var text = GetPlainText(container).Trim();
        if (!text.StartsWith("![", StringComparison.Ordinal))
        {
            return null;
        }

        var match = LooseImageLineRegex.Match(text);
        if (!match.Success
            || !TryNormalizeLooseDestination(match.Groups["dest"].Value, out var url))
        {
            return null;
        }

        return (url, match.Groups["alt"].Value);
    }

    private bool TryAddLooseInlineImages(ContainerInline container, BlockCollection target, Ctx ctx)
    {
        var text = GetPlainText(container);
        if (text.IndexOf("![", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var matches = LooseImageInlineRegex.Matches(text);
        if (matches.Count == 0)
        {
            return false;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, ctx.Compact ? 6 : ctx.Style.FontSize * 1.15)
        };

        var cursor = 0;
        var added = false;
        var onlyWhitespaceAround = true;

        foreach (Match match in matches)
        {
            if (!TryNormalizeLooseDestination(match.Groups["dest"].Value, out var url))
            {
                continue;
            }

            var before = text.Substring(cursor, match.Index - cursor);
            if (before.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(before))
                {
                    onlyWhitespaceAround = false;
                }

                paragraph.Inlines.Add(new Run(before));
            }

            AddImageElement(url, match.Groups["alt"].Value, paragraph.Inlines, ctx);
            cursor = match.Index + match.Length;
            added = true;
        }

        if (!added)
        {
            return false;
        }

        var tail = text.Substring(cursor);
        if (tail.Length > 0)
        {
            if (!string.IsNullOrWhiteSpace(tail))
            {
                onlyWhitespaceAround = false;
            }

            paragraph.Inlines.Add(new Run(tail));
        }

        paragraph.TextAlignment = onlyWhitespaceAround ? TextAlignment.Center : TextAlignment.Left;
        target.Add(paragraph);
        return true;
    }

    private static bool TryNormalizeLooseDestination(string destination, out string url)
    {
        url = string.Empty;
        destination = destination.Trim();

        if (destination.Length == 0
            || destination.StartsWith('<')
            || destination.StartsWith('#'))
        {
            return false;
        }

        if (!destination.Contains(' ') && !destination.Contains('\t'))
        {
            return false;
        }

        var titleMatch = TrailingTitleRegex.Match(destination);
        if (titleMatch.Success && titleMatch.Groups["title"].Success)
        {
            destination = titleMatch.Groups["url"].Value.TrimEnd();
        }

        if (destination.Length == 0
            || destination.Contains('<')
            || destination.Contains('>'))
        {
            return false;
        }

        var looksLikePath = destination.Contains('/') || destination.Contains((char)92);
        if (!looksLikePath && !LooseImageExtensionRegex.IsMatch(destination))
        {
            return false;
        }

        url = destination;
        return true;
    }

    private void AddImageElement(string url, string alt, InlineCollection target, Ctx ctx)
    {
        var style = ctx.Style;

        var labelText = string.IsNullOrWhiteSpace(alt) ? "图片" : alt;
        var label = new TextBlock(new Run(labelText + "（加载中…）"))
        {
            Foreground = style.Muted,
            FontSize = Math.Max(11, style.FontSize - 3),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var placeholder = new Border
        {
            Background = style.CodeBg,
            BorderBrush = style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = _maxWidth,
            Child = label,
            ToolTip = url
        };

        var host = new InlineUIContainer(placeholder)
        {
            BaselineAlignment = BaselineAlignment.Center
        };

        target.Add(host);
        _placeholders.Add(placeholder);
        LoadImageAsync(host, placeholder, label, url, alt, _generation);
    }

    private void AddHtmlBlock(string html, BlockCollection target, Ctx ctx)
    {
        var imageMatches = HtmlImageTagRegex.Matches(html);
        if (imageMatches.Count == 0)
        {
            AddRawBlock(html, "html", target, ctx);
            return;
        }

        // 只接管“纯图片”HTML 块；混着正文的 HTML 保持原样，避免丢字。
        var textOnly = HtmlTagRegex.Replace(HtmlImageTagRegex.Replace(html, string.Empty), string.Empty);
        if (textOnly.Trim().Length > 0)
        {
            AddRawBlock(html, "html", target, ctx);
            return;
        }

        foreach (Match match in imageMatches)
        {
            var attributes = ParseHtmlAttributes(match.Value);
            if (!attributes.TryGetValue("src", out var source) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            attributes.TryGetValue("alt", out var alt);
            var paragraph = new Paragraph
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, ctx.Style.FontSize * 1.2)
            };

            AddImageElement(
                System.Net.WebUtility.HtmlDecode(source),
                System.Net.WebUtility.HtmlDecode(alt ?? string.Empty),
                paragraph.Inlines,
                ctx);

            target.Add(paragraph);
        }
    }

    private void AddHtmlInline(string tag, InlineCollection target, Ctx ctx)
    {
        if (tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
        {
            target.Add(new LineBreak());
            return;
        }

        var match = HtmlImageTagRegex.Match(tag);
        if (!match.Success)
        {
            return;
        }

        var attributes = ParseHtmlAttributes(match.Value);
        if (!attributes.TryGetValue("src", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        attributes.TryGetValue("alt", out var alt);
        AddImageElement(
            System.Net.WebUtility.HtmlDecode(source),
            System.Net.WebUtility.HtmlDecode(alt ?? string.Empty),
            target,
            ctx);
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HtmlAttributeRegex.Matches(tag))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["v1"].Success
                ? match.Groups["v1"].Value
                : match.Groups["v2"].Success
                    ? match.Groups["v2"].Value
                    : match.Groups["v3"].Value;

            result[name] = value;
        }

        return result;
    }

    private async void LoadImageAsync(
        InlineUIContainer host,
        Border placeholder,
        TextBlock label,
        string url,
        string alt,
        int generation)
    {
        var result = await _loader.LoadAsync(url, _baseDirectory).ConfigureAwait(false);
        if (generation != _generation)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (generation != _generation)
            {
                return;
            }

            if (result.Image is null)
            {
                var error = result.Error ?? "未知原因";
                label.Text = string.IsNullOrWhiteSpace(alt)
                    ? $"图片加载失败（{error}）"
                    : $"{alt}（加载失败：{error}）";
                placeholder.ToolTip = url + Environment.NewLine + error + Environment.NewLine + "单击打开";
                placeholder.Cursor = System.Windows.Input.Cursors.Hand;
                placeholder.MouseLeftButtonUp += (_, _) => OpenLink(url);
                return;
            }

            var image = new Image
            {
                Source = result.Image,
                MaxWidth = _maxWidth,
                MaxHeight = 2400,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = (string.IsNullOrWhiteSpace(alt) ? url : $"{alt}\n{url}") + Environment.NewLine + "单击打开"
            };

            image.MouseLeftButtonUp += (_, _) => OpenLink(url);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            host.Child = image;
            _images.Add(image);
            _placeholders.Remove(placeholder);
        });
    }

    private static string GetPlainText(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    builder.Append(GetPlainText(nested));
                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
