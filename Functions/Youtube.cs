using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Sympho.Models;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Sympho.Functions
{
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

        private Sympho? _plugin;
        private readonly AudioHandler _audioHandler;
        private readonly Subtitles _subs;
        private readonly CacheManager _cache;
        private readonly ILogger<Sympho> _logger;
        private string? _ytdlp;
        public static bool IsPlaying = false;

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
            _ytdlp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        }

        public async Task<List<SubtitleInfo>?> ListAvailableSubtitlesAsync(string url)
        {
            if (_plugin == null || string.IsNullOrEmpty(_ytdlp)) return null;

            var ytdl = new YoutubeDL { YoutubeDLPath = Path.Combine(_plugin.ModuleDirectory, _ytdlp) };
            
            _logger.LogInformation("[Subtitles] Fetching video data to list subtitles for {url}", url);

            var res = await ytdl.RunVideoDataFetch(url);

            if (!res.Success)
            {
                _logger.LogError("[Subtitles] Failed to fetch video data. yt-dlp errors: {errors}", string.Join("\n", res.ErrorOutput));
                return null;
            }

            if (res.Data.Subtitles == null || res.Data.Subtitles.Count == 0)
            {
                _logger.LogInformation("[Subtitles] No subtitles found in video data.");
                return new List<SubtitleInfo>();
            }

            var subtitleList = new List<SubtitleInfo>();
            foreach (var sub in res.Data.Subtitles)
            {
                // FIX: The value (sub.Value) is an array of SubtitleData. 
                // We just need the name from the first element.
                if (sub.Value.Length > 0)
                {
                    subtitleList.Add(new SubtitleInfo { LangCode = sub.Key, LanguageName = sub.Value[0].Name });
                }
            }
            
            _logger.LogInformation("[Subtitles] Found {count} non-automatic subtitles.", subtitleList.Count);
            return subtitleList;
        }

        public async Task<YoutubeResult> ProceedYoutubeVideo(string url, int startSec, int duration, string? selectedLangCode)
        {
            var audiopath = await DownloadYoutubeVideo(url);
            if (audiopath == null) return YoutubeResult.Failed(YoutubeResult.Failure.DownloadFailed);

            var cues = await _subs.FetchAndParseSubtitlesAsync(url, selectedLangCode);

            var info = await GetYoutubeInfo(url);
            var durationFormat = TimeSpan.FromSeconds((double)(info.Duration ?? 0)).ToString(@"hh\:mm\:ss");
            
            var trimResult = await EnsureTrimmedIfNeeded(audiopath, startSec, duration);
            if (!trimResult.Success) return YoutubeResult.Failed(trimResult.FailureReason);
            var trimmedAudioPath = trimResult.FilePath!;
            
            Server.NextFrame(() =>
            {
                if (!IsPlaying) return;
                _audioHandler.PlayAudio(trimmedAudioPath);
                Server.PrintToChatAll($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["Youtube.Info", info.Title, durationFormat, info.Uploader]}");
                
                if (cues != null && cues.Count > 0)
                {
                    _subs.StartRealtimeSubtitles(cues);
                }
            });

            return YoutubeResult.Succeeded(trimmedAudioPath);
        }
        
        public async Task<string?> DownloadYoutubeVideo(string url)
        {
            if (_cache.TryGetValid(url, out var cachedPath))
            {
                _cache.Touch(cachedPath);
                return cachedPath;
            }
            
            var targetPath = _cache.GetPathForUrl(url);
            var destDir = Path.GetDirectoryName(targetPath)!;
            var ytdl = new YoutubeDL { YoutubeDLPath = Path.Combine(_plugin!.ModuleDirectory, _ytdlp!) };
            var opts = new OptionSet { Output = Path.Combine(destDir, "%(id)s.%(ext)s"), RestrictFilenames = true, ExtractAudio = true, AudioFormat = AudioConversionFormat.Mp3, NoMtime = true, IgnoreConfig = true, NoCheckCertificates = true };
            
            _logger.LogInformation("Downloading audio for {url}...", url);
            var response = await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3, overrideOptions: opts);
            if (!response.Success) 
            {
                foreach (var e in response.ErrorOutput) _logger.LogError("yt-dlp: {msg}", e); 
                return null; 
            }
            
            var downloadedPath = response.Data;
            if (!string.Equals(Path.GetFullPath(downloadedPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                try 
                { 
                    if (File.Exists(targetPath)) File.Delete(targetPath); 
                    File.Move(downloadedPath, targetPath);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to rename downloaded file."); }
            }

            _cache.Touch(targetPath);
            return targetPath;
        }

        private async Task<YoutubeResult> EnsureTrimmedIfNeeded(string sourcePath, int startSec, int duration)
        {
            if (startSec <= 0) return YoutubeResult.Succeeded(sourcePath);
            
            var destDir = Path.GetDirectoryName(sourcePath)!;
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var trimmedPath = Path.Combine(destDir, $"{fileName}_trimmed_{startSec}.mp3");
            
            if (File.Exists(trimmedPath)) return YoutubeResult.Succeeded(trimmedPath);
            
            bool success = await TrimAudioAsync(sourcePath, trimmedPath, startSec, duration);
            if (success && File.Exists(trimmedPath))
            {
                return YoutubeResult.Succeeded(trimmedPath);
            }
            
            return success ? YoutubeResult.Succeeded(sourcePath) : YoutubeResult.Failed(YoutubeResult.Failure.ProcessTimeout);
        }

        private async Task<bool> TrimAudioAsync(string src, string dest, int start, int duration)
        {
            try
            {
                var ffmpeg = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
                var args = new List<string> { "-y", "-i", src, "-vn", "-acodec", "copy" };
                if (start > 0) args.AddRange(new[] { "-ss", start.ToString() });
                if (duration > 0) args.AddRange(new[] { "-t", duration.ToString() });
                args.Add(dest);

                var startInfo = new ProcessStartInfo 
                { 
                    FileName = ffmpeg, 
                    Arguments = string.Join(" ", args), 
                    UseShellExecute = false, 
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo)!;
                
                var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var errorData = await process.StandardError.ReadToEndAsync();
                
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    _logger.LogError("[ffmpeg] Process timed out while trimming audio: {src}", src);
                    return false;
                }
                
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

        public async Task<VideoData> GetYoutubeInfo(string url)
        {
            var dl = new YoutubeDL { YoutubeDLPath = Path.Combine(_plugin!.ModuleDirectory, _ytdlp!) };
            try { return (await dl.RunVideoDataFetch(url)).Data; }
            catch { return new VideoData { Title = "Unknown", Uploader = "Unknown", Duration = 0 }; }
        }

        public static string? GetYouTubeVideoId(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var regex = new Regex(@"^(?:https?:\/\/)?(?:www\.)?(?:m\.)?(?:youtu\.be\/|youtube\.com\/(?:embed\/|v\/|watch\?v=|watch\?.+&v=))([\w-]{11})(?:\S+)?$");
            var match = regex.Match(url);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}