using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Sympho.Functions;

public sealed class SubtitleCue
{
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = "";
    public int LinePercent { get; set; } = 90;
}

public sealed class Subtitles : IDisposable
{
    private const double LingerDuration = 5.0;

    private readonly CacheManager _cache;
    private readonly ILogger<Sympho> _logger;
    private readonly Dictionary<string, string> _vttStyles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _predefinedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#FFFFFF",
        ["black"] = "#000000",
        ["red"] = "#FF0000",
        ["green"] = "#00FF00",
        ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00",
        ["cyan"] = "#00FFFF",
        ["magenta"] = "#FF00FF",
        ["silver"] = "#C0C0C0",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["maroon"] = "#800000",
        ["olive"] = "#808000",
        ["lime"] = "#00FF00",
        ["aqua"] = "#00FFFF",
        ["teal"] = "#008080",
        ["navy"] = "#000080",
        ["fuchsia"] = "#FF00FF",
        ["purple"] = "#800080",
        ["orange"] = "#FFA500",
        ["pink"] = "#FFC0CB",
        ["brown"] = "#A52A2A",
        ["gold"] = "#FFD700",
        ["violet"] = "#EE82EE",
        ["bg_transparent"] = "transparent",
        ["bg_black"] = "#000000",
        ["bg_blue"] = "#0000FF",
        ["bg_cyan"] = "#00FFFF",
        ["bg_green"] = "#00FF00",
        ["bg_magenta"] = "#FF00FF",
        ["bg_red"] = "#FF0000",
        ["bg_white"] = "#FFFFFF",
        ["bg_yellow"] = "#FFFF00"
    };

    private Sympho? _plugin;
    private string? _ytDlpPath;
    private Guid? _subtitleTimer;
    private List<SubtitleCue> _currentSubtitles = [];
    private List<SubtitleCue> _lastShownCues = [];

    public Subtitles(CacheManager cache, ILogger<Sympho> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public void Initialize(Sympho plugin)
    {
        _plugin = plugin;
        _ytDlpPath = Youtube.FindYtDlpPath(plugin.ModuleDirectory);

        if (_ytDlpPath is null)
        {
            _logger.LogWarning("[Subtitles] yt-dlp executable not found in plugin directory. Subtitle downloads will be skipped.");
        }
    }

    public async Task<List<SubtitleCue>> FetchAndParseSubtitlesAsync(string url, string? langCode, int startSec, int maxDuration)
    {
        if (string.IsNullOrWhiteSpace(langCode) || _plugin is null || _ytDlpPath is null)
        {
            return [];
        }

        try
        {
            var text = await FetchSubtitleTextAsync(url, langCode);
            List<SubtitleCue> cues = string.IsNullOrWhiteSpace(text) ? [] : ParseVttContent(text);
            return ShiftAndTrimCues(cues, startSec, maxDuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Subtitles] Failed to fetch subtitles for language {lang}.", langCode);
            return [];
        }
    }

    public void StartRealtimeSubtitles(List<SubtitleCue> cues)
    {
        if (_plugin is null || cues.Count == 0)
        {
            return;
        }

        StopRealtimeSubtitles();
        _currentSubtitles = cues;
        _lastShownCues = [];
        _subtitleTimer = _plugin.StartRepeatingTimer(UpdateSubtitles, 0.05);
        _logger.LogInformation("[Subtitles] Started realtime subtitles with {count} cues.", cues.Count);
    }

    public void StopRealtimeSubtitles()
    {
        if (_plugin is not null && _subtitleTimer is { } timer)
        {
            try { _plugin.StopTimer(timer); } catch { }
        }

        _subtitleTimer = null;
        _currentSubtitles.Clear();
        _lastShownCues.Clear();
        _vttStyles.Clear();

    }

    public void Dispose()
    {
        StopRealtimeSubtitles();
    }

    private async Task<string?> FetchSubtitleTextAsync(string url, string langCode)
    {
        var json = await FetchInfoJson(url);
        if (json is null)
        {
            return null;
        }

        var subtitleUrl = FindSubtitleUrl(json, langCode);
        if (subtitleUrl is null)
        {
            _logger.LogWarning("[Subtitles] Could not find VTT URL for language {lang}.", langCode);
            return null;
        }

        return await DownloadSubtitleTextAsync(subtitleUrl);
    }

    private async Task<string?> FetchInfoJson(string url)
    {
        if (_ytDlpPath is null || _plugin is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            WorkingDirectory = _plugin.ModuleDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--ignore-config");
        psi.ArgumentList.Add("--no-check-certificates");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--dump-single-json");
        Youtube.AddYtDlpJavaScriptRuntime(psi);
        psi.ArgumentList.Add(url);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            _logger.LogError("[Subtitles] yt-dlp timed out while fetching subtitle metadata.");
            return null;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogError("[Subtitles] yt-dlp failed fetching subtitle metadata: {error}", await errorTask);
            return null;
        }

        return await outputTask;
    }

    private static string? FindSubtitleUrl(string json, string langCode)
    {
        using var doc = JsonDocument.Parse(json);
        return FindSubtitleUrl(doc.RootElement, "subtitles", langCode) ??
               FindSubtitleUrl(doc.RootElement, "automatic_captions", langCode);
    }

    private static string? FindSubtitleUrl(JsonElement root, string bucket, string langCode)
    {
        if (!root.TryGetProperty(bucket, out var subtitles) ||
            subtitles.ValueKind != JsonValueKind.Object ||
            !subtitles.TryGetProperty(langCode, out var formats) ||
            formats.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var format in formats.EnumerateArray())
        {
            if (TryGetString(format, "ext")?.Equals("vtt", StringComparison.OrdinalIgnoreCase) == true)
            {
                return TryGetString(format, "url");
            }
        }

        return null;
    }

    private static async Task<string?> DownloadSubtitleTextAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var text = await client.GetStringAsync(url);
        if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var baseUri = new Uri(url);
        var builder = new StringBuilder();
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var segment = await client.GetStringAsync(new Uri(baseUri, trimmed));
            builder.AppendLine(segment);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private void UpdateSubtitles()
    {
        if (_plugin is null || _currentSubtitles.Count == 0 || !Youtube.IsPlaying)
        {
            return;
        }

        var elapsed = _plugin.GetAudioPlaybackTime();
        if (elapsed < 0)
        {
            return;
        }

        var activeCues = _currentSubtitles
            .Where(cue => cue.StartTime <= elapsed && elapsed < cue.EndTime)
            .ToList();

        List<SubtitleCue> finalCues;
        if (activeCues.Count > 0)
        {
            finalCues = activeCues
                .GroupBy(cue => cue.LinePercent)
                .Select(group => group.OrderByDescending(cue => cue.StartTime).First())
                .OrderBy(cue => cue.LinePercent)
                .ToList();
            _lastShownCues = finalCues;
        }
        else if (_lastShownCues.Count > 0 && elapsed <= _lastShownCues.Max(cue => cue.EndTime) + LingerDuration)
        {
            finalCues = _lastShownCues;
        }
        else
        {
            finalCues = [];
            _lastShownCues.Clear();
        }

        if (finalCues.Count == 0)
        {
            return;
        }

        var innerHtml = string.Join("<br>", finalCues.Select(cue => cue.Text));
        _plugin.PrintSubtitleCenterHtml($"<font size='5'>{innerHtml}</font>");
    }

    private async Task<List<SubtitleCue>> ParseVttFile(string path)
    {
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        return ParseVttContent(content);
    }

    private List<SubtitleCue> ParseVttContent(string content)
    {
        List<SubtitleCue> cues = [];
        if (string.IsNullOrWhiteSpace(content))
        {
            return cues;
        }

        ParseVttStyleBlock(content);
        content = Regex.Replace(content, @"NOTE\s+.*?(?=\r?\n\r?\n)", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (var block in Regex.Split(content, @"(?:\r?\n){2,}"))
        {
            try
            {
                var cue = ParseSingleCue(block.Trim());
                if (cue is not null)
                {
                    cues.Add(cue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Subtitles] Skipping malformed VTT cue.");
            }
        }

        return cues.OrderBy(cue => cue.StartTime).ToList();
    }

    private SubtitleCue? ParseSingleCue(string block)
    {
        if (string.IsNullOrWhiteSpace(block) ||
            block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
            block.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
            block.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase) ||
            block.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lines = block.Split(["\r\n", "\n"], StringSplitOptions.None);
        var timeLineIndex = Array.FindIndex(lines, line => Regex.IsMatch(line, @"[\d:.]+\s*-->\s*[\d:.]+"));
        if (timeLineIndex < 0)
        {
            return null;
        }

        var match = Regex.Match(lines[timeLineIndex].Trim(), @"^([\d:.,]+)\s*-->\s*([\d:.,]+)(.*)");
        if (!match.Success)
        {
            return null;
        }

        var start = ParseTimeToSeconds(match.Groups[1].Value);
        var end = ParseTimeToSeconds(match.Groups[2].Value);
        if (end <= start)
        {
            return null;
        }

        var linePercent = 90;
        var lineMatch = Regex.Match(match.Groups[3].Value, @"line:(-?\d+(?:\.\d+)?%?)");
        if (lineMatch.Success &&
            lineMatch.Groups[1].Value.EndsWith("%", StringComparison.Ordinal) &&
            int.TryParse(lineMatch.Groups[1].Value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPercent))
        {
            linePercent = Math.Clamp(parsedPercent, 0, 100);
        }

        var rawText = string.Join(" ", lines.Skip(timeLineIndex + 1).Where(line => !string.IsNullOrWhiteSpace(line))).Trim();
        if (rawText.Length == 0)
        {
            return null;
        }

        var html = ParseVttText(rawText);
        return html.Length == 0 ? null : new SubtitleCue { StartTime = start, EndTime = end, Text = html, LinePercent = linePercent };
    }

    private void ParseVttStyleBlock(string content)
    {
        _vttStyles.Clear();
        foreach (Match styleMatch in Regex.Matches(content, @"STYLE\s*([\s\S]*?)(?=\r?\n\r?\n|$)", RegexOptions.IgnoreCase))
        {
            ParseCssStyles(styleMatch.Groups[1].Value);
        }
    }

    private void ParseCssStyles(string styleBlock)
    {
        foreach (Match rule in Regex.Matches(styleBlock, @"([^{]+)\s*\{\s*([^}]+)\s*\}", RegexOptions.IgnoreCase))
        {
            var colorMatch = Regex.Match(rule.Groups[2].Value, @"color:\s*([^;]+)", RegexOptions.IgnoreCase);
            if (!colorMatch.Success)
            {
                continue;
            }

            var color = ParseColorValue(colorMatch.Groups[1].Value);
            if (color.Length == 0)
            {
                continue;
            }

            foreach (Match classMatch in Regex.Matches(rule.Groups[1].Value, @"\.([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase))
            {
                _vttStyles[classMatch.Groups[1].Value] = color;
                _vttStyles["c." + classMatch.Groups[1].Value] = color;
            }
        }
    }

    private string ParseColorValue(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (Regex.IsMatch(value, "^#[0-9a-f]{3}([0-9a-f]{3})?$", RegexOptions.IgnoreCase))
        {
            return value;
        }

        var rgb = Regex.Match(value, @"rgba?\s*\((\d+),\s*(\d+),\s*(\d+)");
        if (rgb.Success)
        {
            var r = Math.Clamp(int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture), 0, 255);
            var g = Math.Clamp(int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture), 0, 255);
            var b = Math.Clamp(int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        return _predefinedColors.TryGetValue(value, out var named) ? named : "";
    }

    private string ParseVttText(string text)
    {
        text = WebUtility.HtmlDecode(text.Replace("&nbsp;", " "));
        var result = new StringBuilder();
        var tagStack = new Stack<(string Name, string Close)>();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                var nextTag = text.IndexOf('<', i);
                if (nextTag < 0)
                {
                    nextTag = text.Length;
                }

                result.Append(WebUtility.HtmlEncode(text.Substring(i, nextTag - i)));
                i = nextTag;
                continue;
            }

            var tagEnd = text.IndexOf('>', i);
            if (tagEnd < 0)
            {
                result.Append("&lt;");
                i++;
                continue;
            }

            var tagContent = text.Substring(i + 1, tagEnd - i - 1).Trim();
            i = tagEnd + 1;

            if (Regex.IsMatch(tagContent, @"^[\d:.]+$"))
            {
                continue;
            }

            if (tagContent.StartsWith("/", StringComparison.Ordinal))
            {
                var closeName = tagContent[1..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?
                    .ToLowerInvariant();

                if (closeName is not null &&
                    tagStack.Count > 0 &&
                    IsMatchingClose(tagStack.Peek().Name, closeName))
                {
                    result.Append(tagStack.Pop().Close);
                }
                continue;
            }

            ProcessOpenTag(tagContent, result, tagStack);
        }

        while (tagStack.Count > 0)
        {
            result.Append(tagStack.Pop().Close);
        }

        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }

    private void ProcessOpenTag(string tagContent, StringBuilder result, Stack<(string Name, string Close)> tagStack)
    {
        var tagName = tagContent.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(tagName))
        {
            return;
        }

        switch (tagName)
        {
            case "b":
            case "i":
            case "u":
            case "s":
                result.Append('<').Append(tagName).Append('>');
                tagStack.Push((tagName, $"</{tagName}>"));
                return;
            case "v":
                var voice = tagContent.Length > 1 ? tagContent[1..].Trim() : "";
                if (voice.Length > 0)
                {
                    result.Append("<i>[")
                        .Append(WebUtility.HtmlEncode(voice))
                        .Append("]:</i> ");
                }
                return;
            case "rt":
                return;
        }

        var color = GetTagColor(tagName);
        if (color.Length == 0)
        {
            return;
        }

        result.Append("<font color='").Append(color).Append("'>");
        tagStack.Push((tagName, "</font>"));
    }

    private static bool IsMatchingClose(string openName, string closeName)
        => openName.Equals(closeName, StringComparison.OrdinalIgnoreCase) ||
           (closeName.Equals("c", StringComparison.OrdinalIgnoreCase) &&
            openName.StartsWith("c.", StringComparison.OrdinalIgnoreCase));

    private string GetTagColor(string tagName)
    {
        if (_vttStyles.TryGetValue(tagName, out var color) || _predefinedColors.TryGetValue(tagName, out color))
        {
            return color;
        }

        if (!tagName.StartsWith("c.", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        foreach (var part in tagName[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_vttStyles.TryGetValue(part, out color) || _predefinedColors.TryGetValue(part, out color))
            {
                return color;
            }
        }

        return "";
    }

    private static double ParseTimeToSeconds(string value)
    {
        value = value.Trim().Replace(',', '.');
        var parts = value.Split('.');
        var main = parts[0].Split(':');
        var ms = parts.Length > 1 && double.TryParse(parts[1].PadRight(3, '0')[..3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMs)
            ? parsedMs / 1000.0
            : 0;

        return main.Length switch
        {
            3 => int.Parse(main[0], CultureInfo.InvariantCulture) * 3600 +
                 int.Parse(main[1], CultureInfo.InvariantCulture) * 60 +
                 int.Parse(main[2], CultureInfo.InvariantCulture) + ms,
            2 => int.Parse(main[0], CultureInfo.InvariantCulture) * 60 +
                 int.Parse(main[1], CultureInfo.InvariantCulture) + ms,
            1 => double.Parse(main[0], CultureInfo.InvariantCulture) + ms,
            _ => 0
        };
    }

    private static List<SubtitleCue> ShiftAndTrimCues(IEnumerable<SubtitleCue> cues, int startSec, int maxDuration)
    {
        var endSec = maxDuration > 0 ? startSec + maxDuration : double.MaxValue;
        return cues
            .Where(cue => cue.EndTime > startSec && cue.StartTime < endSec)
            .Select(cue => new SubtitleCue
            {
                StartTime = Math.Max(0, cue.StartTime - startSec),
                EndTime = Math.Max(0, Math.Min(cue.EndTime, endSec) - startSec),
                Text = cue.Text,
                LinePercent = cue.LinePercent
            })
            .Where(cue => cue.EndTime > cue.StartTime)
            .ToList();
    }
}
