using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Sympho.Functions
{
    public class Youtube
    {
        private Sympho? _plugin;
        private AudioHandler _audioHandler;
        private string? _ytdlp;
        private readonly ILogger<Sympho> _logger;
        public static bool IsPlaying = false;
        
        public Youtube(Sympho plugin, AudioHandler audioHandler, ILogger<Sympho> logger)
        {
            _plugin = plugin;
            _audioHandler = audioHandler;
            _logger = logger;
        }

        public void Initialize()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _ytdlp = "yt-dlp";
            }

            else
            {
                _ytdlp = "yt-dlp.exe";
            }
        }

        public async Task ProceedYoutubeVideo(string url, int startSec = 0, int duration = 0)
        {
            var audiopath = await DownloadYoutubeVideo(url, startSec, duration);
            var audioData = await GetYoutubeInfo(url);

            var durationFormat = TimeSpan.FromSeconds((double)audioData.Duration!).ToString(@"hh\:mm\:ss");

            if (audiopath != null)
            {
                Server.NextFrame(() => {
                    IsPlaying = true;
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

            ytdl.YoutubeDLPath = "yt-dlp";
            _logger.LogInformation("Proceeding Downloading");

            ytdl.OutputFolder = dest;

            var response = await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3);
            var downloadFilePath = response.Data;

            if(!response.Success)
            {
                foreach(var errorlog in response.ErrorOutput)
                {
                    _logger.LogError("Error: {log}", errorlog);
                }

                _logger.LogError("Couldn't download the file!");
                return null;
            }

            var newFileName = $"{DateTime.Now.Hour}_{DateTime.Now.Minute}_{DateTime.Now.Second}.mp3";
            var newFilePath = Path.Combine(Path.GetDirectoryName(downloadFilePath)!, newFileName);

            try
            {
                File.Move(downloadFilePath, newFilePath);
                downloadFilePath = newFilePath;
            }
            catch (IOException ex)
            {
                _logger.LogError($"Error renaming file: {ex.Message}");
                // Handle the exception as needed
            }

            if(response.Success && startSec > 0)
            {
                var trimmedFilePath = Path.Combine(Path.GetDirectoryName(downloadFilePath)!, Path.GetFileNameWithoutExtension(downloadFilePath) + "_trimmed" + Path.GetExtension(downloadFilePath));

                await TrimAudioAsync(downloadFilePath, trimmedFilePath, startSec, duration);
                return trimmedFilePath;
            }

            return downloadFilePath;
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
                    FileName = "ffmpeg", 
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
