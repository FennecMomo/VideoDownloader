using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BiliCache;

public sealed record ResolvedVideo(VideoInfo Info, string DownloadJson)
{
    internal string ContentId { get; init; } = "";
    internal string UserAgent { get; init; } = "";
    internal IReadOnlyList<MediaTrack> Tracks { get; init; } = [];
}

/// <summary>Loads the real video page in the application's isolated browser session.</summary>
public sealed class BrowserResolver
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan BrowserOperationTimeout = TimeSpan.FromSeconds(30);
    public static string ProfilePath(string cookieFile, bool useAccountSession) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCache", !string.IsNullOrWhiteSpace(cookieFile) ? "AccountProfile" : useAccountSession ? "SignedInProfile" : "BrowserProfile");

    public async Task<ResolvedVideo> ResolveAsync(string input, string cookieFile, CancellationToken cancellationToken, bool useAccountSession = false)
    {
        string url = DownloadService.NormalizeUrl(input);
        await Gate.WaitAsync(cancellationToken);
        WebView2? browser = null;
        Window? host = null;
        bool closingBrowser = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            browser = new WebView2();
            host = new Window { Width = 1000, Height = 720, Content = browser, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None, Title = "BiliCache 页面解析" };
            host.Show();
            // Keep anonymous browsing separate from explicitly imported account cookies.
            string profile = ProfilePath(cookieFile, useAccountSession);
            var environment = await WaitForBrowserAsync(CoreWebView2Environment.CreateAsync(userDataFolder: profile), cancellationToken);
            await WaitForBrowserAsync(browser.EnsureCoreWebView2Async(environment), cancellationToken);
            if (!string.IsNullOrWhiteSpace(cookieFile)) ImportCookies(browser.CoreWebView2, cookieFile);
            else if (useAccountSession)
            {
                var session = new AccountSession();
                await session.RestoreAsync(browser.CoreWebView2, cancellationToken);
                var cookies = await AccountSession.CaptureAsync(browser.CoreWebView2, cancellationToken);
                AccountIdentity? account;
                try
                {
                    account = await AccountSession.VerifyAsync(cookies, browser.CoreWebView2.Settings.UserAgent, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new DownloadInterruptedException(ex.Message, ex);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError)
                {
                    throw new DownloadInterruptedException(ex.Message, ex);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (account == null) throw new LoginExpiredException();
                session.Save(new SavedSession(account, cookies));
            }
            // Request high quality using normal player preferences; the server still enforces account rights.
            foreach (var preference in new[] { ("CURRENT_QUALITY", "80"), ("CURRENT_FNVAL", "4048") })
            {
                var cookie = browser.CoreWebView2.CookieManager.CreateCookie(preference.Item1, preference.Item2, ".bilibili.com", "/");
                cookie.IsSecure = true;
                browser.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }
            browser.CoreWebView2.IsMuted = true;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            browser.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme != "https" ||
                    !(target.Host == "b23.tv" || target.Host == "bilibili.com" || target.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase))) e.Cancel = true;
            };
            string? networkPlayInfo = null;
            string lastPage = "尚未加载";
            string navigationStatus = "等待导航";
            bool? navigationSucceeded = null;
            int navigationHttpStatus = 0;
            browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                navigationSucceeded = e.IsSuccess;
                navigationHttpStatus = e.HttpStatusCode;
                navigationStatus = $"{e.IsSuccess}/{e.WebErrorStatus}";
            };
            browser.CoreWebView2.WebResourceResponseReceived += async (_, e) =>
            {
                if (closingBrowser || cancellationToken.IsCancellationRequested) return;
                try
                {
                    if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var endpoint) && endpoint.Host == "api.bilibili.com" && endpoint.AbsolutePath.EndsWith("/playurl") && e.Response.StatusCode == 200)
                    {
                        using var stream = await e.Response.GetContentAsync();
                        if (closingBrowser || stream == null) return;
                        using var reader = new StreamReader(stream);
                        networkPlayInfo = await reader.ReadToEndAsync(cancellationToken);
                    }
                }
                catch (Exception ex) when (closingBrowser || ex is System.Runtime.InteropServices.COMException or ObjectDisposedException or IOException) { }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            };
            browser.CoreWebView2.Navigate(url);
            var deadline = DateTime.UtcNow.AddSeconds(55);
            var reloadAt = DateTime.UtcNow.AddSeconds(25);
            bool reloaded = false;
            bool receivedMediaData = false;
            ResolvedVideo? best = null;
            DateTime? foundAt = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken);
                if (!reloaded && DateTime.UtcNow >= reloadAt)
                {
                    // One bounded page reload handles transient incomplete page loads.
                    networkPlayInfo = null;
                    navigationSucceeded = null;
                    navigationHttpStatus = 0;
                    browser.CoreWebView2.Reload();
                    reloaded = true;
                    continue;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                string snapshot;
                try
                {
                    snapshot = await WaitForBrowserAsync(browser.ExecuteScriptAsync("JSON.stringify({state:window.__INITIAL_STATE__||null,play:window.__playinfo__||null,url:location.href,userAgent:navigator.userAgent,title:document.title})"), cancellationToken, remaining);
                }
                catch (System.Runtime.InteropServices.COMException) { continue; }
                catch (TimeoutException ex) { throw new DownloadInterruptedException("等待视频页面响应超时，请检查网络或稍后重试。", ex); }
                string? content = JsonSerializer.Deserialize<string>(snapshot);
                if (string.IsNullOrEmpty(content)) continue;
                using var document = JsonDocument.Parse(content);
                var data = document.RootElement;
                lastPage = $"{data.GetProperty("title").GetString()}；state={data.GetProperty("state").ValueKind}；play={data.GetProperty("play").ValueKind}；network={networkPlayInfo != null}";
                if (data.GetProperty("state").ValueKind != JsonValueKind.Object) continue;
                JsonElement play = data.GetProperty("play");
                using var network = networkPlayInfo == null ? null : JsonDocument.Parse(networkPlayInfo);
                if (play.ValueKind != JsonValueKind.Object && network != null) play = network.RootElement;
                if (play.ValueKind != JsonValueKind.Object || !play.TryGetProperty("data", out var media) || media.ValueKind != JsonValueKind.Object) continue;
                var state = data.GetProperty("state");
                if (!state.TryGetProperty("videoData", out var video)) continue;
                receivedMediaData = true;
                var resolved = ConvertPage(url, data.GetProperty("url").GetString()!, video, media, data.GetProperty("userAgent").GetString()!);
                if (network != null && network.RootElement.TryGetProperty("data", out var networkMedia) && networkMedia.ValueKind == JsonValueKind.Object)
                {
                    var latest = ConvertPage(url, data.GetProperty("url").GetString()!, video, networkMedia, data.GetProperty("userAgent").GetString()!);
                    if (latest != null && (resolved == null || latest.Info.Heights.Max() > resolved.Info.Heights.Max())) resolved = latest;
                }
                if (resolved != null && (best == null || resolved.Info.Heights.Max() > best.Info.Heights.Max())) best = resolved;
                if (best != null)
                {
                    foundAt ??= DateTime.UtcNow;
                    // Logged-in players may replace the anonymous page snapshot with a higher-quality API response.
                    if ((!useAccountSession && string.IsNullOrWhiteSpace(cookieFile)) || best.Info.Heights.Max() >= 1080 || DateTime.UtcNow - foundAt.Value > TimeSpan.FromSeconds(8)) return best;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (navigationSucceeded != true)
            {
                if (navigationHttpStatus is >= 400 and < 500 and not 408)
                    throw new IOException($"B站视频页面拒绝了请求（HTTP {navigationHttpStatus}），尚未取得下载地址。");
                throw new DownloadInterruptedException($"B站视频页面连接失败或加载超时，尚未取得下载地址。请检查网络、DNS 或代理设置后重试。导航={navigationStatus}");
            }
            if (!receivedMediaData)
                throw new DownloadInterruptedException($"视频页面已加载，但播放器暂未返回资源数据。请检查网络或稍后重试。\n{lastPage}；导航={navigationStatus}");
            throw new InvalidOperationException($"视频页面已加载，但未取得可缓存资源。可能是页面资源加载不完整、需要登录或页面验证，或该视频暂时不可用。\n{lastPage}；导航={navigationStatus}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException("缺少 Microsoft Edge WebView2 Runtime。请从微软官网安装后重新打开 BiliCache：https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        finally
        {
            closingBrowser = true;
            try { browser?.Dispose(); }
            finally
            {
                try { host?.Close(); }
                finally { Gate.Release(); }
            }
        }
    }

    internal static async Task WaitForBrowserAsync(Task operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        try { await operation.WaitAsync(timeout ?? BrowserOperationTimeout, cancellationToken); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // WebView2 may complete after the caller has stopped waiting and disposed its view.
            _ = operation.ContinueWith(static faulted => { _ = faulted.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (ex is TimeoutException) throw new TimeoutException("等待浏览器响应超时，请稍后重试。", ex);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async Task<T> WaitForBrowserAsync<T>(Task<T> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await WaitForBrowserAsync((Task)operation, cancellationToken, timeout);
        return await operation;
    }

    public static ResolvedVideo? ConvertPage(string requestedUrl, string pageUrl, JsonElement video, JsonElement media, string userAgent)
    {
        string canonical = DownloadService.NormalizeUrl(pageUrl);
        string bvid = video.GetProperty("bvid").GetString()!;
        string title = video.GetProperty("title").GetString()!;
        string uploader = video.TryGetProperty("owner", out var owner) ? owner.GetProperty("name").GetString() ?? "" : "";
        int page = 1;
        var pageMatch = System.Text.RegularExpressions.Regex.Match(new Uri(requestedUrl).Query, @"(?:[?&])p=(\d+)");
        if (pageMatch.Success && !int.TryParse(pageMatch.Groups[1].Value, out page)) throw new ArgumentException("分P序号无效。");
        double duration = video.GetProperty("duration").GetDouble();
        if (video.TryGetProperty("pages", out var pages))
        {
            var parts = pages.EnumerateArray().ToList();
            if (page < 1 || page > parts.Count) throw new ArgumentException("该视频不存在指定的分P。");
            duration = parts[page - 1].GetProperty("duration").GetDouble();
            if (parts.Count > 1) title += $" P{page} " + parts[page - 1].GetProperty("part").GetString();
        }
        if (media.TryGetProperty("is_preview", out var preview) && preview.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("当前页面只提供试看片段，无法缓存完整视频。");
        if (media.TryGetProperty("timelength", out var length) && length.GetDouble() / 1000 < duration - 3)
            throw new InvalidOperationException("当前页面返回的媒体时长短于完整视频，可能需要登录。");
        var headers = new Dictionary<string, string> { ["Referer"] = canonical, ["User-Agent"] = userAgent };
        var formats = new List<Dictionary<string, object?>>();
        var mediaTracks = new List<MediaTrack>();
        var heights = new List<int>();
        string contentId = bvid + "_p" + page;
        if (media.TryGetProperty("dash", out var dash))
        {
            foreach (string kind in new[] { "video", "audio" })
            {
                if (!dash.TryGetProperty(kind, out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
                int index = 0;
                foreach (var track in tracks.EnumerateArray())
                {
                    string? source = track.TryGetProperty("baseUrl", out var camel) ? camel.GetString() : track.GetProperty("base_url").GetString();
                    if (!IsMediaUrl(source)) continue;
                    int height = kind == "video" ? track.GetProperty("height").GetInt32() : 0;
                    if (height > 0) heights.Add(height);
                    string codec = track.GetProperty("codecs").GetString()!;
                    string id = track.GetProperty("id").ToString();
                    string formatId = kind + "-" + id + "-" + index++;
                    string extension = kind == "video" ? "mp4" : "m4a";
                    var primary = new Uri(source!);
                    var urls = new List<Uri> { primary };
                    var backups = TrackProperty(track, "backupUrl", "backup_url");
                    if (backups.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var backup in backups.EnumerateArray())
                        {
                            if (backup.ValueKind == JsonValueKind.String && IsMediaUrl(backup.GetString()))
                                urls.Add(new Uri(backup.GetString()!));
                        }
                    }
                    var segment = TrackProperty(track, "SegmentBase", "segment_base");
                    if (segment.ValueKind == JsonValueKind.Undefined) segment = TrackProperty(track, "segmentBase");
                    string identity = JsonSerializer.Serialize(new[]
                    {
                        contentId, kind, id, codec, primary.AbsolutePath,
                        TrackProperty(track, "bandwidth").ToString(), TrackProperty(track, "width").ToString(),
                        TrackProperty(track, "height").ToString(), TrackProperty(track, "frameRate", "frame_rate").ToString(),
                        TrackProperty(track, "size").ToString(), TrackProperty(segment, "Initialization", "initialization").ToString(),
                        TrackProperty(segment, "indexRange", "index_range").ToString()
                    });
                    mediaTracks.Add(new MediaTrack(formatId, extension, identity, urls.Distinct().Take(3).ToArray()));
                    formats.Add(new Dictionary<string, object?>
                    {
                        ["format_id"] = formatId, ["url"] = source,
                        ["ext"] = extension, ["protocol"] = "https",
                        ["vcodec"] = kind == "video" ? codec : "none", ["acodec"] = kind == "audio" ? codec : "none",
                        ["height"] = height > 0 ? height : null,
                        ["width"] = kind == "video" ? track.GetProperty("width").GetInt32() : null,
                        ["tbr"] = track.GetProperty("bandwidth").GetDouble() / 1000,
                        ["http_headers"] = headers
                    });
                }
            }
        }
        if (formats.Count == 0) return null;
        if (!formats.Any(f => (string?)f["vcodec"] == "none") || !formats.Any(f => (string?)f["acodec"] == "none")) return null;
        string json = JsonSerializer.Serialize(new { id = contentId, title, uploader, duration, webpage_url = canonical, extractor = "BiliCacheBrowser", extractor_key = "BiliBili", formats, http_headers = headers });
        return new ResolvedVideo(new VideoInfo(canonical, title, uploader, duration, heights.Distinct().OrderDescending().ToList()), json)
        {
            ContentId = contentId,
            UserAgent = userAgent,
            Tracks = mediaTracks
        };
    }

    private static JsonElement TrackProperty(JsonElement track, string name, string? alias = null)
    {
        if (track.ValueKind != JsonValueKind.Object) return default;
        if (track.TryGetProperty(name, out var value)) return value;
        return alias != null && track.TryGetProperty(alias, out value) ? value : default;
    }

    internal static bool IsMediaUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilivideo.cn", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".biliapi.net", StringComparison.OrdinalIgnoreCase));

    private static void ImportCookies(CoreWebView2 core, string file)
    {
        int count = 0;
        foreach (string raw in File.ReadLines(file))
        {
            bool httpOnly = raw.StartsWith("#HttpOnly_");
            string line = httpOnly ? raw[10..] : raw;
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split('\t');
            if (fields.Length != 7) continue;
            string domain = fields[0].TrimStart('.');
            if (domain != "bilibili.com" && !domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!long.TryParse(fields[4], out long expiry) || expiry < 0) continue;
            if (expiry > 0 && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
            var cookie = core.CookieManager.CreateCookie(fields[5], fields[6], fields[0], fields[2]);
            cookie.IsSecure = fields[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            cookie.IsHttpOnly = httpOnly;
            if (expiry > 0) cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;
            core.CookieManager.AddOrUpdateCookie(cookie);
            count++;
        }
        if (count == 0) throw new ArgumentException("所选文件中没有有效的 Bilibili Cookie，请使用 Netscape 格式的未过期 Cookie 文件。");
    }
}
