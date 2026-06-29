using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sympho.Models;

namespace Sympho.Functions;

public class Youtube
{
    public class YoutubeResult
    {
        public bool Success { get; set; }
        public string? FilePath { get; set; }
        public Failure FailureReason { get; set; }

        public enum Failure
        {
            None,
            DownloadFailed,
            ProcessTimeout,
            Unknown
        }

        public static YoutubeResult Succeeded(string path) => new() { Success = true, FilePath = path };
        public static YoutubeResult Failed(Failure reason) => new() { Success = false, FailureReason = reason };
    }

    public sealed class YoutubeInfo
    {
        public string Title { get; init; } = "Unknown";
        public string Uploader { get; init; } = "Unknown";
        public double? Duration { get; init; }
    }

    private sealed record YtDlpResult(int ExitCode, string Output, string Error);

    private readonly AudioHandler _audioHandler;
    private readonly Subtitles _subs;
    private readonly CacheManager _cache;
    private readonly ILogger<Sympho> _logger;
    private Sympho? _plugin;
    private string? _ytDlpPath;

    public static bool IsPlaying;

    public Youtube(AudioHandler audioHandler, Subtitles subs, CacheManager cache, ILogger<Sympho> logger)
    {
        _audioHandler = audioHandler;
        _subs = subs;
        _cache = cache;
        _logger = logger;
    }

    public void Initialize(Sympho plugin)
    {
        _plugin = plugin;
        _ytDlpPath = FindYtDlpPath(plugin.ModuleDirectory);
    }

    public static string? FindYtDlpPath(string moduleDirectory)
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "yt-dlp.exe" }
            : new[] { "yt-dlp", "yt-dlp.exe" };

        return candidates
            .Select(name => Path.Combine(moduleDirectory, name))
            .FirstOrDefault(File.Exists);
    }

    public static void AddYtDlpJavaScriptRuntime(ProcessStartInfo startInfo)
    {
        var nodePath = FindNodeRuntimePath();
        if (nodePath is null)
        {
            return;
        }

        startInfo.ArgumentList.Add("--js-runtimes");
        startInfo.ArgumentList.Add($"node:{nodePath}");
    }

    public async Task<List<SubtitleInfo>?> ListAvailableSubtitlesAsync(string url)
    {
        try
        {
            var json = await FetchInfoJson(url);
            return json is null ? null : ParseSubtitleList(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Subtitles] Failed to list subtitles.");
            return null;
        }
    }

    public async Task<YoutubeResult> ProceedYoutubeVideo(string url, int startSec, int maxDuration, string? selectedLangCode)
    {
        var audioPath = await DownloadYoutubeVideo(url);
        if (audioPath == null) return YoutubeResult.Failed(YoutubeResult.Failure.DownloadFailed);

        var cues = await _subs.FetchAndParseSubtitlesAsync(url, selectedLangCode, startSec, maxDuration);
        var info = await GetYoutubeInfo(url);
        var durationFormat = TimeSpan.FromSeconds(info.Duration ?? 0).ToString(@"hh\:mm\:ss");

        var trimResult = await EnsureTrimmedIfNeeded(audioPath, startSec, maxDuration);
        if (!trimResult.Success) return YoutubeResult.Failed(trimResult.FailureReason);

        var trimmedAudioPath = trimResult.FilePath!;
        _plugin?.RunOnGameThread(() =>
        {
            if (!IsPlaying) return;
            _plugin.PrepareSubtitles(cues);
            if (!_audioHandler.PlayAudio(trimmedAudioPath))
            {
                _plugin.PrepareSubtitles([]);
                IsPlaying = false;
                return;
            }

            _plugin.PrintYoutubeNowPlaying(info.Title, durationFormat, info.Uploader);
        });

        return YoutubeResult.Succeeded(trimmedAudioPath);
    }

    public async Task<string?> DownloadYoutubeVideo(string url)
    {
        if (_plugin == null || string.IsNullOrEmpty(_ytDlpPath)) return null;

        if (_cache.TryGetValid(url, out var cachedPath))
        {
            _cache.Touch(cachedPath);
            return cachedPath;
        }

        var targetPath = _cache.GetPathForUrl(url);
        var destDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(destDir);

        var outputTemplate = Path.Combine(destDir, Path.GetFileNameWithoutExtension(targetPath) + ".%(ext)s");
        var args = new[]
        {
            "--no-warnings",
            "--ignore-config",
            "--no-check-certificates",
            "--restrict-filenames",
            "--no-mtime",
            "--extract-audio",
            "--audio-format",
            "mp3",
            "--ffmpeg-location",
            ResolveFfmpegDirectory(),
            "--output",
            outputTemplate,
            url
        };

        _logger.LogInformation("Downloading audio for {url}...", url);

        try
        {
            var result = await RunYtDlp(args, timeout: TimeSpan.FromMinutes(10));
            if (result is null || result.ExitCode != 0)
            {
                _logger.LogError("yt-dlp failed downloading audio: {error}", result?.Error);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("yt-dlp timed out while downloading audio.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp failed to start or crashed.");
            return null;
        }

        var downloadedPath = ResolveDownloadedAudioPath(targetPath, destDir);
        if (downloadedPath is null)
        {
            _logger.LogError("yt-dlp finished but no mp3 output was found.");
            return null;
        }

        if (!string.Equals(Path.GetFullPath(downloadedPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(downloadedPath, targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move downloaded file.");
                return downloadedPath;
            }
        }

        _cache.Touch(targetPath);
        return targetPath;
    }

    private async Task<YoutubeResult> EnsureTrimmedIfNeeded(string sourcePath, int startSec, int maxDuration)
    {
        if (startSec <= 0 && maxDuration <= 0) return YoutubeResult.Succeeded(sourcePath);

        var destDir = Path.GetDirectoryName(sourcePath)!;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var trimmedPath = Path.Combine(destDir, $"{fileName}_trimmed_{startSec}_{maxDuration}.mp3");

        if (File.Exists(trimmedPath)) return YoutubeResult.Succeeded(trimmedPath);

        var success = await TrimAudioAsync(sourcePath, trimmedPath, startSec, maxDuration);
        if (success && File.Exists(trimmedPath))
        {
            return YoutubeResult.Succeeded(trimmedPath);
        }

        return success ? YoutubeResult.Succeeded(sourcePath) : YoutubeResult.Failed(YoutubeResult.Failure.ProcessTimeout);
    }

    private async Task<bool> TrimAudioAsync(string src, string dest, int start, int maxDuration)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-y");
            if (start > 0)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(start.ToString(CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(src);
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-acodec");
            startInfo.ArgumentList.Add("copy");
            if (maxDuration > 0)
            {
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(maxDuration.ToString(CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add(dest);

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                _logger.LogError("[ffmpeg] Process timed out while trimming audio: {src}", src);
                return false;
            }

            var errorData = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger.LogError("[ffmpeg] Error trimming audio. Exit Code: {code}. Error: {error}", process.ExitCode, errorData);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while trimming audio");
            return false;
        }
    }

    private string ResolveFfmpegDirectory()
        => Path.GetDirectoryName(ResolveFfmpegPath()) ?? (_plugin?.ModuleDirectory ?? AppContext.BaseDirectory);

    private string ResolveFfmpegPath()
    {
        var ffmpegName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        if (_plugin is null)
        {
            return ffmpegName;
        }

        var moduleDir = _plugin.ModuleDirectory;
        var candidates = new[]
        {
            Path.Combine(moduleDir, "..", "Audio", ffmpegName),
            Path.Combine(moduleDir, ffmpegName),
            ffmpegName
        };

        return candidates.FirstOrDefault(File.Exists) ?? ffmpegName;
    }

    public async Task<YoutubeInfo> GetYoutubeInfo(string url)
    {
        try
        {
            var json = await FetchInfoJson(url);
            return json is null ? new YoutubeInfo() : ParseYoutubeInfo(json);
        }
        catch
        {
            return new YoutubeInfo();
        }
    }

    private async Task<string?> FetchInfoJson(string url)
    {
        if (_plugin == null || string.IsNullOrEmpty(_ytDlpPath)) return null;

        var result = await RunYtDlp(
            [
                "--no-warnings",
                "--ignore-config",
                "--no-check-certificates",
                "--skip-download",
                "--dump-single-json",
                url
            ],
            timeout: TimeSpan.FromMinutes(2));

        if (result is null || result.ExitCode != 0)
        {
            _logger.LogError("yt-dlp failed fetching video data: {error}", result?.Error);
            return null;
        }

        return result.Output;
    }

    private async Task<YtDlpResult?> RunYtDlp(IEnumerable<string> args, TimeSpan timeout)
    {
        if (_plugin == null || string.IsNullOrEmpty(_ytDlpPath)) return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            WorkingDirectory = _plugin.ModuleDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        AddYtDlpJavaScriptRuntime(startInfo);

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process == null) return null;

        using var cts = new CancellationTokenSource(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        return new YtDlpResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string? ResolveDownloadedAudioPath(string targetPath, string destDir)
    {
        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        var targetStem = Path.GetFileNameWithoutExtension(targetPath);
        return Directory.EnumerateFiles(destDir, $"{targetStem}.*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindNodeRuntimePath()
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"'))
            .Where(path => path.Length > 0)
            .Select(path => Path.Combine(path, executableName));

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? paths.Concat(
            [
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe"
            ])
            : paths.Concat(
            [
                "/usr/local/bin/node",
                "/usr/bin/node"
            ]);

        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<SubtitleInfo> ParseSubtitleList(string json)
    {
        using var doc = JsonDocument.Parse(json);

        List<SubtitleInfo> result = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSubtitleBucket(doc.RootElement, "subtitles", result, seen, normalCodes);
        AddSubtitleBucket(doc.RootElement, "automatic_captions", result, seen, normalCodes);

        return result
            .OrderBy(sub => sub.LanguageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddSubtitleBucket(
        JsonElement root,
        string bucketName,
        List<SubtitleInfo> result,
        HashSet<string> seen,
        HashSet<string> normalCodes)
    {
        if (!root.TryGetProperty(bucketName, out var subtitles) || subtitles.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var isAutomaticBucket = bucketName.Equals("automatic_captions", StringComparison.OrdinalIgnoreCase);
        var automaticSourceCodes = isAutomaticBucket
            ? GetAutomaticSourceLanguageCodes(subtitles)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subtitle in subtitles.EnumerateObject())
        {
            if (subtitle.Name.Equals("live_chat", StringComparison.OrdinalIgnoreCase) ||
                subtitle.Value.ValueKind != JsonValueKind.Array ||
                subtitle.Value.GetArrayLength() == 0)
            {
                continue;
            }

            if (!TryGetSubtitleFormat(subtitle.Value, "vtt", out var vttFormat) ||
                seen.Contains(subtitle.Name) ||
                (isAutomaticBucket && (normalCodes.Contains(subtitle.Name) ||
                                       automaticSourceCodes.Contains(GetPrimaryLanguageCode(subtitle.Name)) ||
                                       !IsNativeTimedTextFormat(vttFormat))))
            {
                continue;
            }

            var first = subtitle.Value[0];
            var name = TryGetString(vttFormat, "name") ?? TryGetString(first, "name");
            result.Add(new SubtitleInfo
            {
                LangCode = subtitle.Name,
                LanguageName = string.IsNullOrWhiteSpace(name) ? GetLanguageName(subtitle.Name) : name
            });
            seen.Add(subtitle.Name);
            if (!isAutomaticBucket)
            {
                normalCodes.Add(subtitle.Name);
            }
        }
    }

    private static bool TryGetSubtitleFormat(JsonElement formats, string extension, out JsonElement subtitleFormat)
    {
        subtitleFormat = default;
        foreach (var format in formats.EnumerateArray())
        {
            if (TryGetString(format, "ext")?.Equals(extension, StringComparison.OrdinalIgnoreCase) == true)
            {
                subtitleFormat = format;
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeTimedTextFormat(JsonElement format)
    {
        return TryGetString(format, "protocol")?.Equals("m3u8_native", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static HashSet<string> GetAutomaticSourceLanguageCodes(JsonElement subtitles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtitle in subtitles.EnumerateObject())
        {
            if (subtitle.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var format in subtitle.Value.EnumerateArray())
            {
                var url = TryGetString(format, "url");
                if (url is null ||
                    !url.Contains("kind=asr", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("tlang=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = ExtractTimedTextLanguage(url);
                if (source is not null)
                {
                    result.Add(GetPrimaryLanguageCode(source));
                }
            }
        }

        return result;
    }

    private static string? ExtractTimedTextLanguage(string url)
    {
        var match = Regex.Match(url, @"[?&]lang=([^&]+)", RegexOptions.IgnoreCase);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static string GetPrimaryLanguageCode(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 ? code[..dash] : code;
    }

    private static string GetLanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch
        {
            var dash = code.IndexOf('-');
            if (dash > 0)
            {
                try
                {
                    return CultureInfo.GetCultureInfo(code[..dash]).EnglishName;
                }
                catch
                {
                }
            }

            return code;
        }
    }

    private static YoutubeInfo ParseYoutubeInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new YoutubeInfo
        {
            Title = TryGetString(root, "title") ?? "Unknown",
            Uploader = TryGetString(root, "uploader") ?? TryGetString(root, "channel") ?? "Unknown",
            Duration = TryGetDouble(root, "duration")
        };
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? TryGetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public static string? GetYouTubeVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var regex = new Regex(@"^(?:https?:\/\/)?(?:www\.)?(?:m\.)?(?:youtu\.be\/|youtube\.com\/(?:embed\/|v\/|watch\?v=|watch\?.+&v=))([\w-]{11})(?:\S+)?$");
        var match = regex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }
}
