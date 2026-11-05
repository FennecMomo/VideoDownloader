using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliCache;

internal sealed class SubtitleTrack
{
    internal const string FileDialogFilter = "字幕文件|*.srt;*.vtt;*.ass;*.ssa";

    private static readonly string[] Extensions = [".srt", ".vtt", ".ass", ".ssa"];
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".wmv", ".avi", ".m4v", ".webm", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".flv", ".asf", ".mxf", ".vob", ".3gp", ".ogv", ".rmvb", ".rm"
    };
    private static readonly Regex LanguageSuffix = new(
        @"^(?:[a-z]{2,3}(?:[-_][a-z]{2,8})*|简体中文|繁[體体]中文|简体|繁[體体]|简中|繁中|中文|中英|英文|英语)(?:[ ._-](?:[a-z]{2,3}|forced|default|简体|繁[體体]))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex BlockBreak = new(@"\n[ \t]*\n(?:[ \t]*\n)*");
    private static readonly Regex ArrowTiming = new(
        @"^\s*((?:[0-9]+:)?[0-9]{1,2}:[0-9]{2}[.,][0-9]{1,3})\s*-->\s*((?:[0-9]+:)?[0-9]{1,2}:[0-9]{2}[.,][0-9]{1,3})(?:[ \t].*)?$");
    private static readonly Regex TextMarkup = new(
        @"</?(?:b|i|u|s|font|c|v|lang|ruby|rt)(?:[. \t][^<>\n]*)?>|<[0-9]{2}(?::[0-9]{2}){1,2}\.[0-9]{3}>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AssOverride = new(@"\{[^}\n]*\}");
    private static readonly Regex AssDrawingMode = new(@"\\p([0-9]+)(?![0-9])");

    private readonly Cue[] cues;
    private readonly TimeSpan[] latestEnd;

    private SubtitleTrack(Cue[] cues)
    {
        this.cues = cues;
        latestEnd = new TimeSpan[cues.Length];
        TimeSpan end = TimeSpan.Zero;
        for (int i = 0; i < cues.Length; i++)
        {
            if (cues[i].End > end) end = cues[i].End;
            latestEnd[i] = end;
        }
    }

    internal static IEnumerable<string> FindForVideo(string videoPath)
    {
        string directory = Path.GetDirectoryName(videoPath)!;
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        string[] files = Directory.GetFiles(directory);
        var subtitles = files.Where(path => ExtensionOrder(path) >= 0)
            .OrderBy(ExtensionOrder)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var otherVideos = files.Where(path => VideoExtensions.Contains(Path.GetExtension(path)) &&
                !string.Equals(path, videoPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        var exact = subtitles.Where(path => SameStem(Path.GetFileNameWithoutExtension(path), stem)).ToArray();
        var variants = subtitles.Where(path =>
        {
            string subtitleStem = Path.GetFileNameWithoutExtension(path);
            return IsVariant(subtitleStem, stem) && !otherVideos.Any(other => other.Length > stem.Length &&
                (SameStem(subtitleStem, other) || IsVariant(subtitleStem, other)));
        }).ToArray();

        if (exact.Length == 0 && variants.Length == 0 && subtitles.Length == 1 && otherVideos.Length == 0)
            return subtitles;
        return exact.Concat(variants);
    }

    internal static async Task<SubtitleTrack> LoadAsync(string path, CancellationToken token)
    {
        int format = ExtensionOrder(path);
        if (format < 0) throw new InvalidDataException("字幕格式不受支持。");

        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        string content = Decode(bytes);
        var parsed = format >= 2 ? ParseAss(content) : ParseArrowCues(content);
        Cue[] cues = parsed.OrderBy(cue => cue.Start).ToArray();
        token.ThrowIfCancellationRequested();
        if (cues.Length == 0) throw new InvalidDataException("字幕文件没有有效的文本或时间轴。");
        return new SubtitleTrack(cues);
    }

    internal string TextAt(TimeSpan position)
    {
        int low = 0, high = cues.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (cues[middle].Start <= position) low = middle + 1;
            else high = middle;
        }

        List<string>? active = null;
        for (int i = low - 1; i >= 0 && latestEnd[i] > position; i--)
        {
            if (cues[i].End > position) (active ??= []).Add(cues[i].Text);
        }
        if (active == null) return "";
        if (active.Count == 1) return active[0];
        active.Reverse();
        return string.Join('\n', active);
    }

    private static int ExtensionOrder(string path)
    {
        string extension = Path.GetExtension(path);
        return Array.FindIndex(Extensions, candidate => SameStem(extension, candidate));
    }

    private static bool SameStem(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsVariant(string subtitleStem, string videoStem) =>
        subtitleStem.Length > videoStem.Length + 1 && subtitleStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) &&
        subtitleStem[videoStem.Length] is '.' or '_' or '-' or ' ' && LanguageSuffix.IsMatch(subtitleStem[(videoStem.Length + 1)..]);

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try { return reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (encoding == null) throw new InvalidDataException("无法识别字幕文件的文本编码。");
            try { return encoding.GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("无法识别字幕文件的文本编码。", ex); }
        }
    }

    private static IEnumerable<Cue> ParseArrowCues(string content)
    {
        foreach (string block in BlockBreak.Split(content.ReplaceLineEndings("\n")))
        {
            string[] lines = block.Split('\n');
            if (lines.Length < 2) continue;
            string header = lines[0].Trim();
            if (header is "WEBVTT" or "NOTE" or "STYLE" or "REGION" ||
                header.StartsWith("WEBVTT ", StringComparison.Ordinal) || header.StartsWith("NOTE ", StringComparison.Ordinal) ||
                header.StartsWith("WEBVTT\t", StringComparison.Ordinal) || header.StartsWith("NOTE\t", StringComparison.Ordinal)) continue;

            int index = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            var timing = ArrowTiming.Match(lines[index]);
            if (!timing.Success) continue;
            string text = string.Join('\n', lines.Skip(index + 1));
            text = WebUtility.HtmlDecode(TextMarkup.Replace(HtmlBreak.Replace(text, "\n"), "")).Trim();
            if (CreateCue(timing.Groups[1].Value, timing.Groups[2].Value, text) is { } cue) yield return cue;
        }
    }

    private static IEnumerable<Cue> ParseAss(string content)
    {
        using var reader = new StringReader(content);
        bool events = false;
        int startIndex = 1, endIndex = 2, textIndex = 9;
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            if (!events) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                string[] names = line[7..].Split(',', StringSplitOptions.TrimEntries);
                startIndex = Array.FindIndex(names, name => SameStem(name, "Start"));
                endIndex = Array.FindIndex(names, name => SameStem(name, "End"));
                textIndex = SameStem(names[^1], "Text") ? names.Length - 1 : -1;
            }
            if (startIndex < 0 || endIndex < 0 || textIndex < 0 || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            string[] fields = line[9..].Split(',', textIndex + 1);
            if (fields.Length <= textIndex) continue;
            string text = ReadAssText(fields[textIndex]);
            if (CreateCue(fields[startIndex], fields[endIndex], text) is { } cue) yield return cue;
        }
    }

    private static string ReadAssText(string text)
    {
        var result = new StringBuilder();
        int offset = 0;
        bool drawing = false;
        foreach (Match match in AssOverride.Matches(text))
        {
            if (!drawing) result.Append(text, offset, match.Index - offset);
            foreach (Match mode in AssDrawingMode.Matches(match.Value))
                drawing = mode.Groups[1].Value.Any(character => character != '0');
            offset = match.Index + match.Length;
        }
        if (!drawing) result.Append(text, offset, text.Length - offset);
        return result.ToString().Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", "\u00a0").Trim();
    }

    private static Cue? CreateCue(string startText, string endText, string text) =>
        text.Length > 0 && TryReadTimestamp(startText, out var start) && TryReadTimestamp(endText, out var end) && end > start
            ? new Cue(start, end, text)
            : null;

    private static bool TryReadTimestamp(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        int hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return false;
        if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes >= 60 ||
            !decimal.TryParse(parts[^1].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) || seconds >= 60)
            return false;

        decimal ticks = hours * (decimal)TimeSpan.TicksPerHour + minutes * (decimal)TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond;
        if (ticks > long.MaxValue) return false;
        time = TimeSpan.FromTicks((long)ticks);
        return true;
    }

    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);
}
