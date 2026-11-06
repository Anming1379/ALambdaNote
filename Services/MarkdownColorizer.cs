using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ALambdaNote.Services;

/// <summary>编辑区 Markdown 高亮用到的颜色。</summary>
public sealed class EditorPalette
{
    public required Brush Text { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Accent { get; init; }
    public required Brush Code { get; init; }
    public required Brush CodeBackground { get; init; }
    public required Brush Mark { get; init; }
    public required FontFamily Family { get; init; }

    public static EditorPalette FromTheme(FontFamily family)
    {
        static Brush Brush(string key, Brush fallback)
        {
            return Application.Current?.TryFindResource(key) as Brush ?? fallback;
        }

        return new EditorPalette
        {
            Text = Brush("Text", Brushes.Black),
            Muted = Brush("TextMuted", Brushes.Gray),
            Accent = Brush("Accent", Brushes.SteelBlue),
            Code = Brush("CodeText", Brushes.SteelBlue),
            CodeBackground = Brush("CodeBg", Brushes.WhiteSmoke),
            Mark = Brush("AccentSoft", Brushes.LightSteelBlue),
            Family = family
        };
    }
}

/// <summary>编辑区的 Markdown 语法高亮，主题感知，随编辑内容刷新。</summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private static readonly Regex FenceRegex = new(@"^\s{0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^\s{0,3}(#{1,6})(\s+.*)?$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"^\s{0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex BlockquoteRegex = new(@"^(\s{0,3}(?:>\s?)+)", RegexOptions.Compiled);
    private static readonly Regex ListRegex = new(@"^(\s*)([-*+]|\d{1,9}[.)])(\s+)", RegexOptions.Compiled);
    private static readonly Regex TaskRegex = new(@"^(\s*(?:[-*+]|\d{1,9}[.)])\s+)(\[[ xX]\])", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex CodeSpanRegex = new(@"(?<!`)`{1,3}[^`\n]+?`{1,3}(?!`)", RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(@"!\[([^\]\n]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[([^\]\n]+)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex AutoLinkRegex = new(@"https?://[^\s<>()\]]+", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    private static readonly Regex StrikeRegex = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
    private static readonly Regex MarkRegex = new(@"==(?=\S)(.+?)(?<=\S)==", RegexOptions.Compiled);
    private static readonly Regex ItalicStarRegex = new(@"(?<![\*\w])\*(?=\S)(.+?)(?<=\S)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ItalicUnderRegex = new(@"(?<![\w])_(?=\S)(.+?)(?<=\S)_(?![\w])", RegexOptions.Compiled);
    private static readonly Regex HtmlRegex = new(@"</?[A-Za-z][^>\n]*>", RegexOptions.Compiled);
    private static readonly Regex EntityRegex = new(@"&(?:[A-Za-z]+|#\d+);", RegexOptions.Compiled);

    private readonly HashSet<int> _fenceLines = new();

    private EditorPalette _palette;
    private Typeface _normal;
    private Typeface _bold;
    private Typeface _italic;
    private Typeface _boldItalic;
    private bool _structureDirty = true;

    public MarkdownColorizer(EditorPalette palette)
    {
        _palette = palette;
        _normal = new Typeface(palette.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(palette.Family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italic = new Typeface(palette.Family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalic = new Typeface(palette.Family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    }

    public void SetPalette(EditorPalette palette)
    {
        _palette = palette;
        _normal = new Typeface(palette.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(palette.Family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italic = new Typeface(palette.Family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalic = new Typeface(palette.Family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    }

    public void InvalidateStructure()
    {
        _structureDirty = true;
    }

    /// <summary>扫描围栏代码块，记录需要整行按代码着色的行号。</summary>
    public void UpdateStructure(TextDocument document)
    {
        _fenceLines.Clear();
        _structureDirty = false;

        if (document.LineCount > 30000)
        {
            return;
        }

        var inFence = false;
        foreach (var line in document.Lines)
        {
            var text = document.GetText(line);
            if (FenceRegex.IsMatch(text))
            {
                _fenceLines.Add(line.LineNumber);
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                _fenceLines.Add(line.LineNumber);
            }
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_structureDirty)
        {
            UpdateStructure(CurrentContext.Document);
        }

        var text = CurrentContext.Document.GetText(line);
        if (text.Length == 0)
        {
            return;
        }

        var offset = line.Offset;
        var length = text.Length;

        if (FenceRegex.IsMatch(text))
        {
            Paint(offset, 0, length, _palette.Muted, length);
            return;
        }

        if (_fenceLines.Contains(line.LineNumber))
        {
            Paint(offset, 0, length, _palette.Accent, length);
            return;
        }

        if (TableSeparatorRegex.IsMatch(text))
        {
            Paint(offset, 0, length, _palette.Accent, length);
            return;
        }

        var heading = HeadingRegex.Match(text);
        if (heading.Success)
        {
            Paint(offset, 0, length, _palette.Accent, length, bold: true);
            return;
        }

        if (HorizontalRuleRegex.IsMatch(text))
        {
            Paint(offset, 0, length, _palette.Accent, length);
            return;
        }

        var blockquote = BlockquoteRegex.Match(text);
        if (blockquote.Success)
        {
            var marker = blockquote.Groups[1];
            Paint(offset, 0, marker.Length, _palette.Accent, length, bold: true);
            Paint(offset, marker.Length, length, _palette.Muted, length);
        }

        var task = TaskRegex.Match(text);
        if (task.Success)
        {
            var box = task.Groups[2];
            Paint(offset, box.Index, box.Index + box.Length, _palette.Accent, length, bold: true);
        }
        else
        {
            var list = ListRegex.Match(text);
            if (list.Success)
            {
                var marker = list.Groups[2];
                Paint(offset, marker.Index, marker.Index + marker.Length, _palette.Accent, length, bold: true);
            }
        }

        var claimed = new bool[length];

        bool Claim(Match match)
        {
            var start = Math.Max(0, match.Index);
            var end = Math.Min(length, match.Index + match.Length);
            if (end <= start)
            {
                return false;
            }

            for (var i = start; i < end; i++)
            {
                if (claimed[i])
                {
                    return false;
                }
            }

            for (var i = start; i < end; i++)
            {
                claimed[i] = true;
            }

            return true;
        }

        void PaintGroup(Group group, Brush brush, bool bold = false, bool italic = false, bool strike = false, Brush? background = null)
        {
            Paint(offset, group.Index, group.Index + group.Length, brush, length, bold, italic, strike, background);
        }

        foreach (Match match in CodeSpanRegex.Matches(text))
        {
            if (Claim(match))
            {
                Paint(offset, match.Index, match.Index + match.Length, _palette.Accent, length, background: _palette.Mark);
            }
        }

        foreach (Match match in ImageRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Accent);
                PaintGroup(match.Groups[2], _palette.Muted);
            }
        }

        foreach (Match match in LinkRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Accent);
                PaintGroup(match.Groups[2], _palette.Muted);
            }
        }

        foreach (Match match in AutoLinkRegex.Matches(text))
        {
            if (Claim(match))
            {
                Paint(offset, match.Index, match.Index + match.Length, _palette.Accent, length);
            }
        }

        foreach (Match match in BoldRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[2], _palette.Text, bold: true);
            }
        }

        foreach (Match match in StrikeRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Muted, strike: true);
            }
        }

        foreach (Match match in MarkRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Text, background: _palette.Mark);
            }
        }

        foreach (Match match in ItalicStarRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Text, italic: true);
            }
        }

        foreach (Match match in ItalicUnderRegex.Matches(text))
        {
            if (Claim(match))
            {
                PaintGroup(match.Groups[1], _palette.Text, italic: true);
            }
        }

        foreach (Match match in HtmlRegex.Matches(text))
        {
            if (Claim(match))
            {
                Paint(offset, match.Index, match.Index + match.Length, _palette.Muted, length);
            }
        }

        foreach (Match match in EntityRegex.Matches(text))
        {
            if (Claim(match))
            {
                Paint(offset, match.Index, match.Index + match.Length, _palette.Muted, length);
            }
        }

        // 表格里的小竖线淡一点，看起来更像表格。
        if (text.Contains('|'))
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '|' && !claimed[i])
                {
                    Paint(offset, i, i + 1, _palette.Muted, length);
                }
            }
        }
    }

    private void Paint(
        int lineOffset,
        int start,
        int end,
        Brush foreground,
        int lineLength,
        bool bold = false,
        bool italic = false,
        bool strike = false,
        Brush? background = null)
    {
        start = Math.Max(0, start);
        end = Math.Min(lineLength, end);
        if (end <= start)
        {
            return;
        }

        ChangeLinePart(lineOffset + start, lineOffset + end, element =>
        {
            var properties = element.TextRunProperties;
            properties.SetForegroundBrush(foreground);

            if (background is not null)
            {
                properties.SetBackgroundBrush(background);
            }

            if (bold || italic)
            {
                properties.SetTypeface(bold
                    ? (italic ? _boldItalic : _bold)
                    : _italic);
            }

            if (strike)
            {
                properties.SetTextDecorations(TextDecorations.Strikethrough);
            }
        });
    }
}
