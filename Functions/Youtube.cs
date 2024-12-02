using System.Drawing;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using Serilog.Core;
using Sympho.Models;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Sympho.Functions
{
    public class Youtube
    {
        private Sympho? _plugin;
        private AudioHandler _audioHandler;
        private string? _ffmpeg;
        private string? _ytdlp;
        private Settings? _settings;
        
        public Youtube(AudioHandler audioHandler)
        {
            _audioHandler = audioHandler;
        }

        public void Initialize(Sympho plugin)
        {
            _plugin = plugin;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _ffmpeg = "ffmpeg";
                _ytdlp = "yt-dlp";
            }

            else
            {
                _ffmpeg = "ffmpeg.exe";
                _ytdlp = "yt-dlp.exe";
            }
        }

        public void InitialConfigs(Settings settings)
        {
            _settings = settings;
        }

        public async Task ProceedYoutubeVideo(string url, int startSec = 0, int duration = 0)
        {
            var audiopath = await DownloadYoutubeVideo(url, startSec, duration);
            var audioData = await GetYoutubeInfo(url);

            var durationFormat = TimeSpan.FromSeconds((double)audioData.Duration!).ToString(@"hh\:mm\:ss");

            if (audiopath != null)
            {
                Server.NextFrame(() => {
                    _audioHandler.PlayAudio(audiopath);
                    // Server.PrintToChatAll($" {ChatColors.Default}[{ChatColors.Lime}Sympho{ChatColors.Default}] Youtube Title: {audioData.Title} | Duration: {durationFormat} | Author: {audioData.Uploader}");

                    Server.PrintToChatAll($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["Youtube.Info", audioData.Title, durationFormat, audioData.Uploader]}");
                });
            }
        }

        public async Task<string?> DownloadYoutubeVideo(string url, int startSec = 0, int duration = 0)
        {
            var dest = Path.Combine(_plugin!.ModuleDirectory, "temp");

            if (!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
            }

            var ytdl = new YoutubeDL();

            ytdl.YoutubeDLPath = Path.Combine(_plugin!.ModuleDirectory, _ytdlp!);
            ytdl.OutputFolder = dest;

            var response = await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3);
            var downloadFilePath = response.Data;

            if(response.Success && startSec > 0)
            {
                var trimmedFilePath = Path.Combine(Path.GetDirectoryName(downloadFilePath)!, Path.GetFileNameWithoutExtension(downloadFilePath) + "_trimmed" + Path.GetExtension(downloadFilePath));

                await TrimAudioAsync(downloadFilePath, trimmedFilePath, startSec, duration);
                return trimmedFilePath;
            }

            return response.Data;
        }

        public async Task<VideoData> GetYoutubeInfo(string url)
        {
            var ytdl = new YoutubeDL();

            ytdl.YoutubeDLPath = Path.Combine(_plugin!.ModuleDirectory, _ytdlp!);
            var response = await ytdl.RunVideoDataFetch(url);
            return response.Data;
        }

        public async Task TrimAudioAsync(string inputFilePath, string outputFilePath, int startSec, int duration = 0) 
        { 
            string startTime = TimeSpan.FromSeconds(startSec).ToString(@"hh\:mm\:ss"); 
            string durationTime = TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss");

            string ffmpegCommand; 
                
            if(duration > 0)
                ffmpegCommand = $"-i \"{inputFilePath}\" -ss {startTime} -t {durationTime} -c copy \"{outputFilePath}\""; 

            else
                ffmpegCommand = $"-i \"{inputFilePath}\" -ss {startTime} -c copy \"{outputFilePath}\"";

            var process = new System.Diagnostics.Process 
            { 
                StartInfo = new System.Diagnostics.ProcessStartInfo 
                { 
                    FileName = Path.Combine(_plugin!.ModuleDirectory, _ffmpeg!), 
                    Arguments = ffmpegCommand, 
                    RedirectStandardOutput = true, 
                    UseShellExecute = false, 
                    CreateNoWindow = true, 
                } 
            }; 
            process.Start(); 
            await process.WaitForExitAsync(); 
        }
    }
}
