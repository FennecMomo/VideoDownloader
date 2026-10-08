using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliCache;

public sealed class DownloadService
{
    public static string CancellationDetail(string folder)
    {
        try
        {
            bool hasMedia = Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(path =>
                Path.GetExtension(path).ToLowerInvariant() is ".part" or ".mp4" or ".m4a" or ".webm" or ".mkv" && new FileInfo(path).Length > 0);
            return hasMedia ? "已取消。已有媒体文件或下载片段保留，可重试缓存。" : "已取消。未发现已下载的媒体数据，可重试缓存。";
        }
        catch (IOException) { return "已取消。无法检查下载目录，可重试缓存。"; }
        catch (UnauthorizedAccessException) { return "已取消。无法检查下载目录权限，可重试缓存。"; }
    }
    private readonly Dictionary<string, (DateTime At, ResolvedVideo Video)> resolved = new();
    public void ClearResolved() => resolved.Clear();
    private async Task<ResolvedVideo> ResolveBrowserAsync(string url, Settings settings, CancellationToken cancellationToken)
    {
        string key = url + "|" + settings.UseAccountSession + "|" + settings.CookieFile + "|" + (File.Exists(settings.CookieFile) ? File.GetLastWriteTimeUtc(settings.CookieFile).Ticks : 0);
        if (resolved.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Video;
        var result = await new BrowserResolver().ResolveAsync(url, settings.CookieFile, cancellationToken, settings.UseAccountSession);
        resolved.Clear();
        resolved[key] = (DateTime.UtcNow, result);
        return result;
    }
    public static string NormalizeUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("请粘贴完整的 HTTPS Bilibili 视频链接。");
        string host = uri.IdnHost.ToLowerInvariant();
        if (uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0)
            throw new ArgumentException("请使用不含用户名、端口或片段的标准视频链接。");
        if (host == "b23.tv" && Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9]+/?$"))
            return uri.GetLeftPart(UriPartial.Path);
        if (host is not ("www.bilibili.com" or "bilibili.com" or "m.bilibili.com") ||
            !Regex.IsMatch(uri.AbsolutePath, @"^/video/(BV[A-Za-z0-9]+|av[0-9]+)/?$", RegexOptions.IgnoreCase))
            throw new ArgumentException("第一版支持 B 站普通视频（BV / av）和 b23.tv 短链接，暂不支持直播、番剧和收藏夹。");
        var page = Regex.Match(uri.Query, @"(?:[?&])p=([1-9][0-9]*)(?:&|$)");
        return "https://www.bilibili.com" + uri.AbsolutePath + (page.Success ? "?p=" + page.Groups[1].Value : "");
    }

    public static string FindTools(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools");
            if (File.Exists(Path.Combine(candidate, "yt-dlp.exe"))) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "tools");
    }

    public static string ValidateTools(Settings settings)
    {
        string root = FindTools(settings.ToolsDirectory);
        foreach (string name in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe" })
            if (!File.Exists(Path.Combine(root, name))) throw new FileNotFoundException($"缺少 {name}。请运行 scripts\\Install-Tools.ps1，或在设置中选择完整的工具目录。");
        return root;
    }

    private static List<string> Common(Settings settings)
    {
        var args = new List<string> { "--ignore-config", "--no-plugin-dirs", "--no-playlist", "--no-colors", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "3", "--extractor-retries", "2" };
        if (!string.IsNullOrWhiteSpace(settings.CookieFile))
        {
            if (!File.Exists(settings.CookieFile)) throw new FileNotFoundException("配置的 Cookie 文件不存在，请重新选择或清空。");
            args.AddRange(["--cookies", settings.CookieFile]);
        }
        return args;
    }

    public async Task<VideoInfo> AnalyzeAsync(string input, Settings settings, CancellationToken cancellationToken)
    {
        ValidateTools(settings);
        return (await ResolveBrowserAsync(NormalizeUrl(input), settings, cancellationToken)).Info;
    }

    public async Task<string> DownloadAsync(CacheJob job, Settings settings, IProgress<DownloadUpdate> progress, CancellationToken cancellationToken)
    {
        string root = ValidateTools(settings);
        Directory.CreateDirectory(job.Folder);
        var args = Common(settings);
        string filter = job.Height > 0 ? $"[height<={job.Height}]" : "";
        progress.Report(new DownloadUpdate(0, "正在通过浏览器解析视频…"));
        var source = await ResolveBrowserAsync(NormalizeUrl(job.Url), settings, cancellationToken);
        string infoPath = Path.Combine(job.Folder, ".download-info.json");
        await File.WriteAllTextAsync(infoPath, source.DownloadJson, cancellationToken);
        args.AddRange(["--load-info-json", infoPath]);
        args.AddRange(["--ffmpeg-location", root, "--windows-filenames", "--continue", "--newline", "--progress", "--no-simulate",
            "--progress-template", "download:PROGRESS:%(progress)j", "--print", "after_move:FILE:%(filepath)j",
            "-f", $"bv{filter}+ba/b{filter}", "-S", "vcodec:h264,acodec:aac", "--merge-output-format", "mp4", "--remux-video", "mp4",
            "-o", Path.Combine(job.Folder, "%(title).120B [%(id)s].%(ext)s")]);
        string? file = null;
        try
        {
        await RunAsync(Path.Combine(root, "yt-dlp.exe"), args, line =>
        {
            if (line.StartsWith("FILE:")) file = JsonSerializer.Deserialize<string>(line[5..]);
            else if (line.StartsWith("PROGRESS:"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line[9..]);
                    var p = doc.RootElement;
                    double total = Number(p, "total_bytes");
                    if (total <= 0) total = Number(p, "total_bytes_estimate");
                    double done = Number(p, "downloaded_bytes");
                    double percent = total > 0 ? Math.Clamp(done / total * 100, 0, 100) : 0;
                    string detail = Text(p, "status") == "finished" ? "当前媒体流已下载，正在处理后续轨道或合并…" : $"{percent:F1}% · {Number(p, "speed") / 1048576:F2} MB/s · 剩余约 {Number(p, "eta"):F0} 秒";
                    progress.Report(new DownloadUpdate(Math.Min(percent, 99), detail));
                }
                catch (JsonException) { /* Ignore non-machine progress lines. */ }
            }
            else if (line.StartsWith("[Merger]") || line.StartsWith("[VideoRemuxer]"))
                progress.Report(new DownloadUpdate(99, "正在合并音视频，请稍候…"));
        }, cancellationToken);
        }
        finally
        {
            if (File.Exists(infoPath)) File.Delete(infoPath);
            resolved.Clear();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(file)) throw new IOException("下载进程结束，但没有返回最终文件路径。");
        file = Path.GetFullPath(file);
        string allowed = Path.GetFullPath(job.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(file) || new FileInfo(file).Length == 0)
            throw new IOException("没有找到完整的缓存文件，请重试。");
        progress.Report(new DownloadUpdate(99, "正在检查本地音视频文件…"));
        await VerifyMediaAsync(root, file, cancellationToken, source.Info.Duration);
        return file;
    }

    public static async Task VerifyMediaAsync(string toolsRoot, string file, CancellationToken cancellationToken, double expectedDuration = 0)
    {
        var result = new StringBuilder();
        await RunAsync(Path.Combine(toolsRoot, "ffprobe.exe"), ["-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", file], line => result.AppendLine(line), cancellationToken);
        using var doc = JsonDocument.Parse(result.ToString());
        var types = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => Text(s, "codec_type")).ToList();
        if (!types.Contains("video") || !types.Contains("audio") || Number(doc.RootElement.GetProperty("format"), "duration") <= 0)
            throw new IOException("缓存文件未通过检查：需要有效的视频、音频和时长。");
        if (expectedDuration > 0 && Math.Abs(Number(doc.RootElement.GetProperty("format"), "duration") - expectedDuration) > 3)
            throw new IOException("缓存文件时长与原视频不一致，未标记完成，请重新缓存。");
    }

    internal static async Task RunAsync(string exe, IEnumerable<string> arguments, Action<string> onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var errors = new Queue<string>();
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (errors.Count >= 12) errors.Dequeue();
                errors.Enqueue(line);
            }
        }
        async Task ReadOutput()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line) onLine(line);
        }
        try
        {
            await Task.WhenAll(ReadOutput(), ReadErrors(), process.WaitForExitAsync());
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(FriendlyError(string.Join(Environment.NewLine, errors)));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    public static string FriendlyError(string message)
    {
        string tip = message.Contains("412") || message.Contains("429") ? "B 站暂时限制了请求，请稍后重试。"
            : message.Contains("403") || message.Contains("login", StringComparison.OrdinalIgnoreCase) ? "访问被拒绝或需要登录，请在设置中配置自己的 Cookie 后重试。"
            : message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("Unable to download", StringComparison.OrdinalIgnoreCase) ? "网络连接失败，请检查网络后重试。"
            : "解析或缓存失败，可检查链接、登录状态和下载引擎版本后重试。";
        return tip + Environment.NewLine + message;
    }

    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Number(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
}
