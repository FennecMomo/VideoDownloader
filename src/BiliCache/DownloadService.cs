using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public async Task<VideoInfo> AnalyzeAsync(string input, Settings settings, CancellationToken cancellationToken)
    {
        ValidateTools(settings);
        return (await ResolveBrowserAsync(NormalizeUrl(input), settings, cancellationToken)).Info;
    }

    public async Task<string> DownloadAsync(CacheJob job, Settings settings, IProgress<DownloadUpdate> progress, CancellationToken cancellationToken)
    {
        string root = ValidateTools(settings);
        if (!string.IsNullOrWhiteSpace(settings.CookieFile) && !File.Exists(settings.CookieFile))
            throw new FileNotFoundException("配置的 Cookie 文件不存在，请重新选择或清空。");
        progress.Report(new DownloadUpdate(job.Progress, "正在通过浏览器解析视频…"));
        ResolvedVideo source;
        try
        {
            source = await ResolveBrowserAsync(NormalizeUrl(job.Url), settings, cancellationToken);
        }
        finally { resolved.Clear(); }
        return await Task.Run(() => DownloadResolvedAsync(root, job, source, progress, cancellationToken), cancellationToken);
    }

    internal static async Task<string> DownloadResolvedAsync(string root, CacheJob job, ResolvedVideo source,
        IProgress<DownloadUpdate> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(job.Folder);
        string outputSuffix = $" [{source.ContentId}].mp4";
        var existing = Directory.EnumerateFiles(job.Folder).Where(path =>
            path.EndsWith(outputSuffix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (existing.Length == 1 && await ReuseOutputAsync(root, existing[0], source.Info.Duration, progress, cancellationToken))
            return Path.GetFullPath(existing[0]);

        var selection = await SelectTracksAsync(root, job, source, cancellationToken);
        string file = selection.File;
        if (File.Exists(file) && await ReuseOutputAsync(root, file, source.Info.Duration, progress, cancellationToken)) return file;

        string videoPath = await MediaTransfer.FetchAsync(job.Folder, source, selection.Video, (done, total) =>
            ReportTransfer(progress, "视频", done, total), cancellationToken);
        string audioPath = await MediaTransfer.FetchAsync(job.Folder, source, selection.Audio, (done, total) =>
            ReportTransfer(progress, "音频", done, total), cancellationToken);
        string mergePath = Path.Combine(job.Folder, ".merge-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            progress.Report(new DownloadUpdate(99, "正在合并音视频，请稍候…"));
            await RunAsync(Path.Combine(root, "ffmpeg.exe"),
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", videoPath, "-i", audioPath,
                    "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", mergePath],
                _ => { }, cancellationToken);
            progress.Report(new DownloadUpdate(99, "正在检查本地音视频文件…"));
            await VerifyMediaAsync(root, mergePath, cancellationToken, source.Info.Duration);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(mergePath, file);
        }
        catch (DownloadProcessException ex) when (IsInvalidMedia(ex))
        {
            await QuarantineInvalidTrackAsync(root, videoPath, "v:0", "video", cancellationToken);
            await QuarantineInvalidTrackAsync(root, audioPath, "a:0", "audio", cancellationToken);
            throw;
        }
        finally { TryDeleteTemporary(mergePath); }
        TryDeleteTemporary(videoPath);
        TryDeleteTemporary(videoPath + ".length");
        TryDeleteTemporary(audioPath);
        TryDeleteTemporary(audioPath + ".length");
        return file;
    }

    private static async Task QuarantineInvalidTrackAsync(string root, string file, string stream, string kind, CancellationToken token)
    {
        bool hasTrack = false;
        try
        {
            await RunAsync(Path.Combine(root, "ffprobe.exe"),
                ["-v", "error", "-select_streams", stream, "-show_entries", "stream=codec_type", "-of", "default=nw=1:nk=1", file],
                line => hasTrack |= line == kind, token);
        }
        catch (DownloadProcessException ex) when (IsInvalidMedia(ex)) { hasTrack = false; }
        catch (DownloadProcessException) { return; }

        token.ThrowIfCancellationRequested();
        if (hasTrack) return;
        File.Move(file, file + ".invalid-" + Guid.NewGuid().ToString("N"));
        TryDeleteTemporary(file + ".length");
    }

    private static async Task<bool> ReuseOutputAsync(string root, string file, double duration,
        IProgress<DownloadUpdate> progress, CancellationToken token)
    {
        try
        {
            progress.Report(new DownloadUpdate(99, "正在检查已有缓存文件…"));
            if (new FileInfo(file).Length == 0) throw new InvalidMediaException("缓存文件为空。");
            await VerifyMediaAsync(root, file, token, duration);
            return true;
        }
        catch (Exception ex) when (IsInvalidMedia(ex))
        {
            token.ThrowIfCancellationRequested();
            File.Move(file, file + ".invalid-" + Guid.NewGuid().ToString("N"));
            return false;
        }
    }

    private static async Task<(MediaTrack Video, MediaTrack Audio, string File)> SelectTracksAsync(
        string root, CacheJob job, ResolvedVideo source, CancellationToken token)
    {
        string selectionPath = Path.Combine(job.Folder, ".media-selection.json");
        var saved = await LoadSelectionAsync(selectionPath, token);
        string? videoId = null, audioId = null, file = null;
        string filter = job.Height > 0 ? $"[height<={job.Height}]" : "";
        string formats = $"bv{filter}+ba/b{filter}";
        if (saved != null)
        {
            var matchingVideo = source.Tracks.FirstOrDefault(track => MediaTransfer.TrackKey(track) == saved.Video);
            var matchingAudio = source.Tracks.FirstOrDefault(track => MediaTransfer.TrackKey(track) == saved.Audio);
            if (matchingVideo == null || matchingAudio == null)
                throw new DownloadInterruptedException("当前地址未返回此任务已选的音视频轨道，原有片段已保留。");
            formats = $"{matchingVideo.FormatId}+{matchingAudio.FormatId}";
        }

        string infoPath = Path.Combine(job.Folder, ".download-info-" + Guid.NewGuid().ToString("N") + ".json");
        var info = JsonNode.Parse(source.DownloadJson)!.AsObject();
        if (!string.IsNullOrWhiteSpace(job.Title)) info["title"] = job.Title;
        try
        {
            await File.WriteAllTextAsync(infoPath, info.ToJsonString(), token);
            await RunAsync(Path.Combine(root, "yt-dlp.exe"),
                ["--ignore-config", "--no-plugin-dirs", "--encoding", "utf-8", "--ffmpeg-location", root, "--load-info-json", infoPath,
                    "--windows-filenames", "--simulate", "--print", "TRACKS:%(requested_formats)j", "--print", "OUTPUT:%(filename)j",
                    "-f", formats, "-S", "vcodec:h264,acodec:aac", "--merge-output-format", "mp4",
                    "-o", Path.Combine(job.Folder, "%(title).120B [%(id)s].%(ext)s")],
                line =>
                {
                    if (line.StartsWith("OUTPUT:", StringComparison.Ordinal))
                        file = JsonSerializer.Deserialize<string>(line[7..]);
                    if (!line.StartsWith("TRACKS:[", StringComparison.Ordinal)) return;
                    using var tracks = JsonDocument.Parse(line[7..]);
                    foreach (var track in tracks.RootElement.EnumerateArray())
                    {
                        string id = Text(track, "format_id");
                        if (id.StartsWith("video-", StringComparison.Ordinal)) videoId = id;
                        else if (id.StartsWith("audio-", StringComparison.Ordinal)) audioId = id;
                    }
                }, token);
        }
        finally
        {
            TryDeleteTemporary(infoPath);
        }

        var video = source.Tracks.FirstOrDefault(track => track.FormatId == videoId);
        var audio = source.Tracks.FirstOrDefault(track => track.FormatId == audioId);
        if (video == null || audio == null || string.IsNullOrWhiteSpace(file))
            throw new IOException("下载组件未能选定完整的音视频轨道。");
        file = Path.GetFullPath(file);
        string allowed = Path.GetFullPath(job.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("下载组件返回的缓存路径不在任务目录内。");
        if (saved == null)
            await SaveSelectionAsync(selectionPath, new TrackSelection(MediaTransfer.TrackKey(video), MediaTransfer.TrackKey(audio)), token);
        return (video, audio, file);
    }

    private sealed record TrackSelection(string Video, string Audio);

    private static async Task<TrackSelection?> LoadSelectionAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var selection = JsonSerializer.Deserialize<TrackSelection>(await File.ReadAllTextAsync(file, token));
            if (selection?.Video is { Length: 24 } && selection.Audio is { Length: 24 }) return selection;
            throw new JsonException("轨道标识缺失。");
        }
        catch (JsonException ex) { throw new IOException("缓存任务的轨道记录损坏，原有片段已保留。", ex); }
    }

    private static async Task SaveSelectionAsync(string file, TrackSelection selection, CancellationToken token)
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(selection), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file);
        }
        finally { TryDeleteTemporary(temporary); }
    }

    private static void ReportTransfer(IProgress<DownloadUpdate> progress, string track, long done, long total) =>
        progress.Report(new DownloadUpdate(Math.Min(99, (double)done / total * 100),
            $"正在缓存{track} · {done / 1048576d:F1} / {total / 1048576d:F1} MB"));

    private sealed class InvalidMediaException(string message) : IOException(message);

    private static bool IsInvalidMedia(Exception error) => error is InvalidMediaException ||
        error is DownloadProcessException process &&
        (process.Diagnostic.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) ||
            process.Diagnostic.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase));

    internal static void TryDeleteTemporary(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task VerifyMediaAsync(string toolsRoot, string file, CancellationToken cancellationToken, double expectedDuration = 0)
    {
        var result = new StringBuilder();
        await RunAsync(Path.Combine(toolsRoot, "ffprobe.exe"), ["-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", file], line => result.AppendLine(line), cancellationToken);
        using var doc = JsonDocument.Parse(result.ToString());
        var types = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => Text(s, "codec_type")).ToList();
        if (!types.Contains("video") || !types.Contains("audio") || Number(doc.RootElement.GetProperty("format"), "duration") <= 0)
            throw new InvalidMediaException("缓存文件未通过检查：需要有效的视频、音频和时长。");
        if (expectedDuration > 0 && Math.Abs(Number(doc.RootElement.GetProperty("format"), "duration") - expectedDuration) > 3)
            throw new InvalidMediaException("缓存文件时长与原视频不一致，未标记完成，请重新缓存。");
    }

    internal static Task RunAsync(string exe, IEnumerable<string> arguments, Action<string> onLine, CancellationToken cancellationToken) =>
        ToolProcess.RunAsync(exe, arguments, onLine, cancellationToken);

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
