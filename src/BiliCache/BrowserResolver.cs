using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BiliCache;

public sealed record ResolvedVideo(VideoInfo Info, string DownloadJson);

/// <summary>Loads the real video page in the application's isolated browser session.</summary>
public sealed class BrowserResolver
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string ProfilePath(string cookieFile, bool useAccountSession) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCache", !string.IsNullOrWhiteSpace(cookieFile) ? "AccountProfile" : useAccountSession ? "SignedInProfile" : "BrowserProfile");

    public async Task<ResolvedVideo> ResolveAsync(string input, string cookieFile, CancellationToken cancellationToken, bool useAccountSession = false)
    {
        string url = DownloadService.NormalizeUrl(input);
        await Gate.WaitAsync(cancellationToken);
        WebView2? browser = null;
        Window? host = null;
        try
        {
            browser = new WebView2();
            host = new Window { Width = 1000, Height = 720, Content = browser, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None, Title = "BiliCache 页面解析" };
            host.Show();
            // Keep anonymous browsing separate from explicitly imported account cookies.
            string profile = ProfilePath(cookieFile, useAccountSession);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await browser.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(cookieFile)) ImportCookies(browser.CoreWebView2, cookieFile);
            else if (useAccountSession)
            {
                var session = new AccountSession();
                await session.RestoreAsync(browser.CoreWebView2);
                var cookies = await AccountSession.CaptureAsync(browser.CoreWebView2);
                var account = await AccountSession.VerifyAsync(cookies, browser.CoreWebView2.Settings.UserAgent, cancellationToken);
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
            browser.CoreWebView2.NavigationCompleted += (_, e) => { navigationSucceeded = e.IsSuccess; navigationStatus = $"{e.IsSuccess}/{e.WebErrorStatus}"; };
            browser.CoreWebView2.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var endpoint) && endpoint.Host == "api.bilibili.com" && endpoint.AbsolutePath.EndsWith("/playurl") && e.Response.StatusCode == 200)
                    {
                        using var stream = await e.Response.GetContentAsync();
                        using var reader = new StreamReader(stream);
                        networkPlayInfo = await reader.ReadToEndAsync();
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ObjectDisposedException or IOException) { }
            };
            browser.CoreWebView2.Navigate(url);
            var deadline = DateTime.UtcNow.AddSeconds(55);
            var reloadAt = DateTime.UtcNow.AddSeconds(25);
            bool reloaded = false;
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
                    browser.CoreWebView2.Reload();
                    reloaded = true;
                    continue;
                }
                string snapshot;
                try
                {
                    snapshot = await browser.ExecuteScriptAsync("JSON.stringify({state:window.__INITIAL_STATE__||null,play:window.__playinfo__||null,url:location.href,userAgent:navigator.userAgent,title:document.title})");
                }
                catch (System.Runtime.InteropServices.COMException) { continue; }
                string? content = JsonSerializer.Deserialize<string>(snapshot);
                if (string.IsNullOrEmpty(content)) continue;
                using var document = JsonDocument.Parse(content);
                var data = document.RootElement;
                lastPage = $"{data.GetProperty("title").GetString()}；state={data.GetProperty("state").ValueKind}；play={data.GetProperty("play").ValueKind}；network={networkPlayInfo != null}";
                if (data.GetProperty("state").ValueKind != JsonValueKind.Object) continue;
                JsonElement play = data.GetProperty("play");
                using var network = networkPlayInfo == null ? null : JsonDocument.Parse(networkPlayInfo);
                if (play.ValueKind != JsonValueKind.Object && network != null) play = network.RootElement;
                if (play.ValueKind != JsonValueKind.Object || !play.TryGetProperty("data", out var media)) continue;
                var state = data.GetProperty("state");
                if (!state.TryGetProperty("videoData", out var video)) continue;
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
            if (navigationSucceeded != true)
                throw new IOException($"B站视频页面连接失败或加载超时，尚未取得下载地址。请检查网络、DNS 或代理设置后重试。导航={navigationStatus}");
            throw new InvalidOperationException($"视频页面已加载，但未取得可缓存资源。可能是页面资源加载不完整、需要登录或页面验证，或该视频暂时不可用。\n{lastPage}；导航={navigationStatus}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException("缺少 Microsoft Edge WebView2 Runtime。请从微软官网安装后重新打开 BiliCache：https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        finally { browser?.Dispose(); host?.Close(); Gate.Release(); }
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
        var heights = new List<int>();
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
                    formats.Add(new Dictionary<string, object?>
                    {
                        ["format_id"] = kind + "-" + track.GetProperty("id") + "-" + index++, ["url"] = source,
                        ["ext"] = kind == "video" ? "mp4" : "m4a", ["protocol"] = "https",
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
        string json = JsonSerializer.Serialize(new { id = bvid + "_p" + page, title, uploader, duration, webpage_url = canonical, extractor = "BiliCacheBrowser", extractor_key = "BiliBili", formats, http_headers = headers });
        return new ResolvedVideo(new VideoInfo(canonical, title, uploader, duration, heights.Distinct().OrderDescending().ToList()), json);
    }

    private static bool IsMediaUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
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
