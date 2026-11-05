using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BiliCache;

public sealed class DownloadInterruptedException(string message, Exception? innerException = null) : IOException(message, innerException);

internal sealed record MediaTrack(string FormatId, string Extension, string Identity, IReadOnlyList<Uri> Urls);

internal static class MediaTransfer
{
    private const int OverlapBytes = 64 * 1024;
    private const int BufferBytes = 128 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = IdleTimeout };

    private sealed class ResumeException(string message) : IOException(message);

    internal static string TrackKey(MediaTrack track) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(track.Identity)))[..24];

    public static async Task<string> FetchAsync(string folder, ResolvedVideo source, MediaTrack track, Action<long, long> report, CancellationToken token)
    {
        string complete = Path.Combine(folder, $".media-{TrackKey(track)}.{track.Extension}");
        string lengthFile = complete + ".length";
        long? knownLength = await ReadKnownLengthAsync(lengthFile, token).ConfigureAwait(false);
        if (File.Exists(complete))
        {
            long actualLength = new FileInfo(complete).Length;
            if (actualLength > 0 && (knownLength == null || knownLength == actualLength)) return complete;
            throw new IOException("已缓存的媒体轨道长度不一致，原有文件已保留。");
        }

        string stablePartial = complete + ".part";
        string partial = stablePartial;
        if (!File.Exists(partial))
        {
            string suffix = $" [{source.ContentId}].f{track.FormatId}.{track.Extension}";
            var legacy = Directory.EnumerateFiles(folder).Where(path =>
                path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(suffix + ".part", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (legacy.Length == 1) partial = legacy[0];
        }

        while (true)
        {
            DownloadInterruptedException? interrupted = null;
            ResumeException? incompatible = null;
            bool receivedRange = false;
            foreach (var url in track.Urls)
            {
                try
                {
                    var result = await ReceiveAsync(url, partial, lengthFile, knownLength, source, report, token).ConfigureAwait(false);
                    knownLength = result.Length;
                    if (result.Complete)
                    {
                        token.ThrowIfCancellationRequested();
                        File.Move(partial, complete);
                        return complete;
                    }
                    receivedRange = true;
                    break;
                }
                catch (DownloadInterruptedException ex)
                {
                    interrupted = ex;
                    knownLength = await ReadKnownLengthAsync(lengthFile, token).ConfigureAwait(false);
                }
                catch (ResumeException ex) { incompatible = ex; }
            }
            if (receivedRange) continue;
            if (interrupted != null) throw interrupted;
            if (incompatible != null && partial != stablePartial && knownLength == null)
            {
                // An old index-based file may belong to another representation.
                partial = stablePartial;
                continue;
            }
            if (incompatible != null && File.Exists(partial) && new FileInfo(partial).Length > 0)
                throw new DownloadInterruptedException(incompatible.Message, incompatible);
            throw incompatible ?? new ResumeException("没有可用的媒体下载地址。");
        }
    }

    private static async Task<(bool Complete, long Length)> ReceiveAsync(Uri url, string partial, string lengthFile, long? knownLength,
        ResolvedVideo source, Action<long, long> report, CancellationToken token)
    {
        bool exists = File.Exists(partial);
        long offset = exists ? new FileInfo(partial).Length : 0;
        int overlap = (int)Math.Min(offset, OverlapBytes);
        long start = offset - overlap;
        using var response = await SendAsync(url, source, offset > 0 ? start : null, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new ResumeException("下载源返回了无法安全续传的编码，原有片段已保留。");
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)))
            throw new DownloadInterruptedException("下载地址返回了网页或接口响应，已有片段已保留。");

        long total;
        long end;
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange is { From: long first, To: long last, Length: long length } range &&
            range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) &&
            first == start && last >= first && last < length && length >= offset &&
            (last >= offset || last + 1 == offset && length == offset))
        {
            total = length;
            end = last;
        }
        else if (response.StatusCode == HttpStatusCode.OK && offset == 0 && response.Content.Headers.ContentLength is > 0)
        {
            total = response.Content.Headers.ContentLength.Value;
            end = total - 1;
        }
        else
        {
            throw new ResumeException(offset > 0
                ? "下载源未返回匹配的续传范围，原有片段已保留。可稍后重试 / 继续。"
                : "下载源未返回有效的媒体长度。");
        }

        if (response.Content.Headers.ContentLength is long declared && declared != end - start + 1)
            throw new ResumeException("下载源返回的长度与媒体范围不一致，原有片段已保留。");
        if (offset > 0 && knownLength is long savedLength && total != savedLength)
            throw new ResumeException("下载源的媒体总长度已变化，原有片段已保留。");

        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(partial, exists ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (output.Length != offset) throw new IOException("本地片段在读取期间发生变化，请重试。");
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        byte[] buffer = new byte[BufferBytes];
        if (overlap > 0)
        {
            byte[] existing = new byte[overlap];
            output.Position = start;
            await output.ReadExactlyAsync(existing, token).ConfigureAwait(false);
            int checkedBytes = 0;
            while (checkedBytes < overlap)
            {
                int count = await ReadAsync(input, buffer.AsMemory(0, overlap - checkedBytes), idle, token).ConfigureAwait(false);
                if (count == 0) throw new DownloadInterruptedException("验证续传位置时连接中断，原有片段已保留。");
                if (!buffer.AsSpan(0, count).SequenceEqual(existing.AsSpan(checkedBytes, count)))
                    throw new ResumeException("下载源的媒体内容与本地片段不一致，原有片段已保留。");
                checkedBytes += count;
            }
        }

        if (knownLength != total)
            await SaveKnownLengthAsync(lengthFile, total, token).ConfigureAwait(false);
        report(offset, total);
        var interval = Stopwatch.StartNew();
        while (offset <= end)
        {
            int maximum = (int)Math.Min(buffer.Length, end - offset + 1);
            int count = await ReadAsync(input, buffer.AsMemory(0, maximum), idle, token).ConfigureAwait(false);
            if (count == 0) throw new DownloadInterruptedException("下载连接在媒体数据传完前中断，已收到的片段已保留。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            offset += count;
            if (interval.ElapsedMilliseconds >= 250)
            {
                report(offset, total);
                interval.Restart();
            }
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        report(offset, total);
        return (offset == total, total);
    }

    private static async Task<long?> ReadKnownLengthAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file)) return null;
        string value = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long total) && total > 0)
            return total;
        throw new IOException("媒体长度记录损坏，原有片段已保留。");
    }

    private static async Task SaveKnownLengthAsync(string file, long total, CancellationToken token)
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, total.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, overwrite: true);
        }
        finally { DownloadService.TryDeleteTemporary(temporary); }
    }

    private static async Task<HttpResponseMessage> SendAsync(Uri url, ResolvedVideo source, long? start, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(source.Info.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (start is long from) request.Headers.Range = new RangeHeaderValue(from, null);

            HttpResponseMessage response;
            try { response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new DownloadInterruptedException("下载连接超时，已有片段已保留。", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadInterruptedException("下载连接失败，已有片段已保留。", ex);
            }

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent) return response;
            int code = (int)response.StatusCode;
            var location = response.Headers.Location;
            response.Dispose();
            if (code is 301 or 302 or 303 or 307 or 308 &&
                location != null && Uri.TryCreate(url, location, out var next) && BrowserResolver.IsMediaUrl(next.AbsoluteUri))
            {
                url = next;
                continue;
            }
            if (code is 401 or 403 or 404 or 408 or 410 or 429 || code >= 500)
                throw new DownloadInterruptedException($"下载源暂时不可用（HTTP {code}），已有片段已保留。");
            throw new ResumeException($"下载源拒绝当前请求（HTTP {code}），原有片段已保留。");
        }
        throw new DownloadInterruptedException("下载源重定向次数过多，已有片段已保留。");
    }

    private static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationTokenSource idle, CancellationToken token)
    {
        idle.CancelAfter(IdleTimeout);
        try { return await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new DownloadInterruptedException("下载长时间没有收到数据，已有片段已保留。", ex);
        }
        catch (Exception ex) when ((ex is IOException or HttpRequestException) && !token.IsCancellationRequested)
        {
            throw new DownloadInterruptedException("下载连接中断，已有片段已保留。", ex);
        }
        finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
    }
}
