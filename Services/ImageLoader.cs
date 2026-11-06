using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ALambdaNote.Services;

/// <summary>一次图片加载的结果，失败时带上简短原因。</summary>
public sealed record ImageLoadResult(ImageSource? Image, string? Error)
{
    public static ImageLoadResult Fail(string error) => new(null, error);
}

/// <summary>本地与网页图片的异步加载，带内存缓存、防盗链重试与失败原因。</summary>
public sealed class ImageLoader
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const long MaxImageBytes = 40L * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Dictionary<string, ImageLoadResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageLoadResult> _dataCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ImageLoadResult>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<ImageLoadResult> LoadAsync(string url, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(ImageLoadResult.Fail("空链接"));
        }

        url = url.Trim();

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(LoadDataUri(url));
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                url = new Uri(url).LocalPath;
            }
            catch
            {
                return Task.FromResult(ImageLoadResult.Fail("无效的文件链接"));
            }
        }

        var localPath = ResolveLocalPath(url, baseDirectory);
        if (localPath is not null)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(localPath, out var cached) && cached.Image is not null)
                {
                    return Task.FromResult(cached);
                }
            }

            var localResult = LoadLocal(localPath);
            if (localResult.Image is not null)
            {
                lock (_gate)
                {
                    _cache[localPath] = localResult;
                }
            }

            return Task.FromResult(localResult);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromResult(ImageLoadResult.Fail("不支持的链接"));
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(url, out var cached) && cached.Image is not null)
            {
                return Task.FromResult(cached);
            }

            if (_inflight.TryGetValue(url, out var running))
            {
                return running;
            }

            var task = DownloadAsync(uri);
            _inflight[url] = task;
            return CompleteAsync(url, task);
        }
    }

    private async Task<ImageLoadResult> CompleteAsync(string key, Task<ImageLoadResult> task)
    {
        var result = await task.ConfigureAwait(false);

        lock (_gate)
        {
            _inflight.Remove(key);

            if (result.Image is not null)
            {
                _cache[key] = result;
                if (_cache.Count > 64)
                {
                    _cache.Clear();
                    _cache[key] = result;
                }
            }
        }

        return result;
    }

    private static async Task<ImageLoadResult> DownloadAsync(Uri uri)
    {
        var lastError = "下载失败";

        // 第一次带来源页重试，很多图床要靠 Referer 才肯放行；
        // 被 403 拦住时再裸请求一次，兼容少数“有 Referer 反而拒绝”的站点。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "image/avif,image/webp,image/svg+xml,image/png,image/jpeg,image/gif,image/bmp,image/*;q=0.5,*/*;q=0.1");
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

                if (attempt == 0)
                {
                    try
                    {
                        request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
                    }
                    catch
                    {
                        // 有些 URL 取不到 origin，就不带 Referer。
                    }
                }

                using var response = await Http
                    .SendAsync(request)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var challenge = response.Headers.TryGetValues("cf-mitigated", out var mitigated)
                        && mitigated.Any(value => value.Contains("challenge", StringComparison.OrdinalIgnoreCase));
                    lastError = challenge
                        ? "HTTP " + status + "（网站要求浏览器验证）"
                        : status is 401 or 403 or 406
                            ? "HTTP " + status + "（防盗链或需要登录）"
                            : "HTTP " + status;

                    if (attempt == 0 && (status == 401 || status == 403 || status == 406))
                    {
                        continue;
                    }

                    return ImageLoadResult.Fail(lastError);
                }

                if (response.Content.Headers.ContentLength is > MaxImageBytes)
                {
                    return ImageLoadResult.Fail("图片过大");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length > MaxImageBytes)
                {
                    return ImageLoadResult.Fail("图片过大");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                return DecodeImage(bytes, mediaType, uri.AbsolutePath);
            }
            catch (TaskCanceledException)
            {
                lastError = "下载超时";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.StatusCode is { } status
                    ? "HTTP " + (int)status
                    : "网络不可用";
            }
            catch (Exception)
            {
                lastError = "下载失败";
            }
        }

        return ImageLoadResult.Fail(lastError);
    }

    private static ImageLoadResult LoadLocal(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ImageLoadResult.Fail("找不到文件");
            }

            var info = new FileInfo(path);
            if (info.Length > MaxImageBytes)
            {
                return ImageLoadResult.Fail("图片过大");
            }

            // 本地文件也走统一的 DecodeImage：这样没有 .svg 扩展名、
            // 或者只有 XML 注释/DOCTYPE 前缀的 SVG 也能被识别。
            return DecodeImage(File.ReadAllBytes(path), null, path);
        }
        catch (Exception)
        {
            return ImageLoadResult.Fail("读取失败");
        }
    }

    private ImageLoadResult LoadDataUri(string url)
    {
        if (url.Length > MaxImageBytes * 2)
        {
            return ImageLoadResult.Fail("图片过大");
        }

        lock (_gate)
        {
            if (_dataCache.TryGetValue(url, out var cached) && cached.Image is not null)
            {
                return cached;
            }
        }

        var result = DecodeDataUri(url);
        if (result.Image is not null)
        {
            lock (_gate)
            {
                _dataCache[url] = result;
                if (_dataCache.Count > 32)
                {
                    _dataCache.Clear();
                    _dataCache[url] = result;
                }
            }
        }

        return result;
    }

    private static ImageLoadResult DecodeDataUri(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0 || comma <= 5)
        {
            return ImageLoadResult.Fail("data URI 格式错误");
        }

        var header = url[5..comma];
        var payload = url[(comma + 1)..];
        var parts = header.Split(';');
        var mediaType = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        var isBase64 = parts.Any(part => part.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));

        byte[] bytes;
        try
        {
            bytes = isBase64
                ? Convert.FromBase64String(Uri.UnescapeDataString(payload))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch
        {
            return ImageLoadResult.Fail("data URI 解码失败");
        }

        return DecodeImage(bytes, mediaType, null);
    }

    private static ImageLoadResult DecodeImage(byte[] bytes, string? mediaType, string? sourceName)
    {
        if (bytes.Length == 0)
        {
            return ImageLoadResult.Fail("空图片");
        }

        if (bytes.Length > MaxImageBytes)
        {
            return ImageLoadResult.Fail("图片过大");
        }

        if (IsSvgSource(bytes, mediaType, sourceName))
        {
            var rendered = SvgImageRenderer.Render(bytes);
            return rendered.Image is null
                ? ImageLoadResult.Fail(rendered.Error ?? "SVG 解析失败")
                : new ImageLoadResult(rendered.Image, null);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = Decode(stream);
        return bitmap is null
            ? ImageLoadResult.Fail(UnsupportedFormat(mediaType, sourceName))
            : new ImageLoadResult(bitmap, null);
    }

    private static bool IsSvgSource(byte[] bytes, string? mediaType, string? sourceName)
    {
        var extension = Path.GetExtension(sourceName ?? string.Empty);
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(mediaType)
            && mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 明确的 HTML/JSON 不要当 SVG；Cloudflare 挑战页之类会返回 200 HTML。
        if (!string.IsNullOrEmpty(mediaType)
            && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 有些图床不带扩展名、Content-Type 也写成 text/plain 或 application/octet-stream，
        // 但内容其实是 SVG。扫描前 16 KB，兼容 XML 声明、注释、DOCTYPE 和命名空间前缀。
        var sampleLength = Math.Min(bytes.Length, 16384);
        var sample = Encoding.UTF8.GetString(bytes, 0, sampleLength)
            .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');

        if (sample.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SVGZ 可能也没有扩展名；解压一小段确认是否真的是 SVG。
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                var buffer = new byte[16384];
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    var gzipSample = Encoding.UTF8.GetString(buffer, 0, read)
                        .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                    return gzipSample.Contains("<svg", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string UnsupportedFormat(string? mediaType, string? sourceName)
    {
        var type = (mediaType ?? string.Empty).ToLowerInvariant();
        var extension = Path.GetExtension(sourceName ?? string.Empty).ToLowerInvariant();

        if (type.Contains("svg") || extension is ".svg" or ".svgz")
        {
            return "SVG 解析失败";
        }

        if (type.Contains("avif") || extension == ".avif")
        {
            return "AVIF 需要系统解码器";
        }

        if (type.Contains("webp") || extension == ".webp")
        {
            return "WebP 需要系统解码器";
        }

        if (type.Contains("heic") || type.Contains("heif") || extension is ".heic" or ".heif")
        {
            return "HEIC 需要系统解码器";
        }

        return "图片格式不支持";
    }

    private static BitmapImage? Decode(Stream stream)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocalPath(string url, string? baseDirectory)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("#", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var path = url;
            if (path.Contains('%'))
            {
                path = Uri.UnescapeDataString(path);
            }

            if (!Path.IsPathRooted(path))
            {
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return null;
                }

                path = Path.Combine(baseDirectory, path);
            }

            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }
}
