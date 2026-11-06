using System.Globalization;
using System.IO;
using System.Text;

namespace ALambdaNote.Services;

/// <summary>一个可显示的编码选项。</summary>
public sealed class EncodingOption
{
    public string Id { get; }

    public string Display { get; }

    public Encoding Encoding { get; }

    public bool EmitBom { get; }

    public EncodingOption(string id, string display, Encoding encoding, bool emitBom)
    {
        Id = id;
        Display = display;
        Encoding = encoding;
        EmitBom = emitBom;
    }
}

public sealed record TextFileData(string Text, EncodingOption Encoding, string Newline);

/// <summary>文本文件读取、编码探测与写回。</summary>
public static class TextFileService
{
    public static EncodingOption Ansi { get; } = new(
        "ansi",
        "ANSI",
        Encoding.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback),
        emitBom: false);

    public static EncodingOption Utf8 { get; } = new(
        "utf8",
        "UTF-8",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
        emitBom: false);

    public static EncodingOption Utf8Bom { get; } = new(
        "utf8bom",
        "UTF-8 BOM",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false),
        emitBom: true);

    public static EncodingOption Utf16Le { get; } = new(
        "utf16le",
        "UTF-16 LE",
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false),
        emitBom: true);

    public static EncodingOption Utf16Be { get; } = new(
        "utf16be",
        "UTF-16 BE",
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false),
        emitBom: true);

    public static IReadOnlyList<EncodingOption> All { get; } = new[]
    {
        Ansi, Utf8, Utf8Bom, Utf16Le, Utf16Be
    };

    public static TextFileData Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (option, bomLength) = Detect(bytes);

        if (bytes.Length == 0)
        {
            return new TextFileData(string.Empty, option, "\r\n");
        }

        if (LooksBinary(bytes, bomLength))
        {
            throw new InvalidDataException("这个文件看起来不是纯文本，打开它可能会损坏内容。");
        }

        string text;
        try
        {
            text = option.Encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.UTF8.GetString(bytes);
            option = Utf8;
        }

        // 去掉解码后残留的 BOM 字符。
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return new TextFileData(text, option, DetectNewline(text));
    }

    public static void Write(string path, string text, EncodingOption option, string newline)
    {
        var normalized = NormalizeNewlines(text, newline);
        var body = option.Encoding.GetBytes(normalized);

        if (option.EmitBom)
        {
            var preamble = option.Encoding.GetPreamble();
            if (preamble.Length > 0)
            {
                var withBom = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, withBom, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, withBom, preamble.Length, body.Length);
                body = withBom;
            }
        }

        File.WriteAllBytes(path, body);
    }

    public static string NormalizeNewlines(string text, string newline)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return newline == "\n" ? normalized : normalized.Replace("\n", newline);
    }

    public static string DetectNewline(string text)
    {
        var lf = text.IndexOf('\n');
        if (lf > 0 && text[lf - 1] == '\r')
        {
            return "\r\n";
        }

        if (lf >= 0)
        {
            return "\n";
        }

        return text.Contains('\r') ? "\r" : "\r\n";
    }

    private static (EncodingOption Option, int BomLength) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8Bom, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Utf16Le, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Utf16Be, 2);
        }

        try
        {
            // 严格 UTF-8 试解码，失败就当作 ANSI（中文系统为 GBK）。
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return (Utf8, 0);
        }
        catch (DecoderFallbackException)
        {
            return (Ansi, 0);
        }
    }

    private static bool LooksBinary(byte[] bytes, int offset)
    {
        var limit = Math.Min(bytes.Length, 8192);
        for (var i = offset; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
