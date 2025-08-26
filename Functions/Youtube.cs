using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Sympho.Functions
{
    public class Youtube
    {
        private Sympho? _plugin;
        private readonly AudioHandler _audioHandler;
        private readonly Subtitles _subs;
        private readonly ILogger<Sympho> _logger;
        private string? _ytdlp;
        public static bool IsPlaying = false;

        public Youtube(AudioHandler audioHandler, Subtitles subs, ILogger<Sympho> logger)
        {
            _audioHandler = audioHandler;
            _subs = subs;
            _logger = logger;
        }

        public void Initialize(Sympho plugin)
        {
            _plugin = plugin;
            _ytdlp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        }

        public async Task<string?> ProceedYoutubeVideo(string url, int startSec = 0, int duration = 0)
        {
            var audiopath = await DownloadYoutubeVideo(url);
            if (audiopath == null) return null;

            var cues = await _subs.FetchAndParseSubtitlesAsync(url);

            var info = await GetYoutubeInfo(url);
            var durationFormat = TimeSpan.FromSeconds((double)(info.Duration ?? 0)).ToString(@"hh\:mm\:ss");
            var trimmedAudioPath = await EnsureTrimmedIfNeeded(audiopath, startSec, duration);
            
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

            return trimmedAudioPath;
        }

        public async Task<string?> DownloadYoutubeVideo(string url)
        {
            var dest = Path.Combine(_plugin!.ModuleDirectory, "tmp");
            Directory.CreateDirectory(dest);
            var videoId = GetYouTubeVideoId(url);
            if (string.IsNullOrEmpty(videoId)) { _logger.LogError("Could not extract video ID from URL: {0}", url); return null; }
            var targetPath = Path.Combine(dest, $"{videoId}.mp3");
            if (File.Exists(targetPath)) { _logger.LogInformation("Using cached audio file: {0}", targetPath); return targetPath; }
            var ytdl = new YoutubeDL { YoutubeDLPath = Path.Combine(_plugin!.ModuleDirectory, _ytdlp!) };
            var opts = new OptionSet { Output = Path.Combine(dest, "%(id)s.%(ext)s"), RestrictFilenames = true, ExtractAudio = true, AudioFormat = AudioConversionFormat.Mp3, NoMtime = true, IgnoreConfig = true, NoCheckCertificates = true };
            
            _logger.LogInformation("Downloading audio for {url}...", url);
            var response = await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3, overrideOptions: opts);
            if (!response.Success) { foreach (var e in response.ErrorOutput) _logger.LogError("yt-dlp: {msg}", e); return null; }
            
            var downloadedPath = response.Data;
            if (!string.Equals(Path.GetFullPath(downloadedPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(targetPath)) File.Delete(targetPath); File.Move(downloadedPath, targetPath); return targetPath; }
                catch (Exception ex) { _logger.LogError(ex, "Failed to rename downloaded file."); }
            }
            return downloadedPath;
        }

        private async Task<string> EnsureTrimmedIfNeeded(string sourcePath, int startSec, int duration)
        {
            if (startSec <= 0) return sourcePath;
            var destDir = Path.GetDirectoryName(sourcePath)!;
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var trimmedPath = Path.Combine(destDir, $"{fileName}_trimmed_{startSec}.mp3");
            if (File.Exists(trimmedPath)) return trimmedPath;
            await TrimAudioAsync(sourcePath, trimmedPath, startSec, duration);
            return File.Exists(trimmedPath) ? trimmedPath : sourcePath;
        }

        private async Task TrimAudioAsync(string src, string dest, int start, int duration)
        {
            try
            {
                var ffmpeg = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
                var args = new List<string> { "-y", "-i", src, "-vn", "-acodec", "copy" };
                if (start > 0) args.AddRange(new[] { "-ss", start.ToString() });
                if (duration > 0) args.AddRange(new[] { "-t", duration.ToString() });
                args.Add(dest);
                var startInfo = new ProcessStartInfo { FileName = ffmpeg, Arguments = string.Join(" ", args), UseShellExecute = false, CreateNoWindow = true };
                using var process = Process.Start(startInfo)!;
                await process.WaitForExitAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error trimming audio"); }
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