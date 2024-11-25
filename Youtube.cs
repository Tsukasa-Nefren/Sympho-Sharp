using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using MediaToolkit;
using MediaToolkit.Model;
using VideoLibrary;

namespace Sympho
{
    public partial class Sympho
    {
        [CommandHelper(1, "css_yt <video-url>")]
        [ConsoleCommand("css_yt")]
        public void YoutubeCommand(CCSPlayerController client, CommandInfo info)
        {
            if (info.ArgCount > 1)
                return;

            var url = info.ArgString;
            Task.Run(() => ProceedYoutubeVideo(url));
        }

        public async Task ProceedYoutubeVideo(string url)
        {
            var valid = await IsVideoValid(url);

            string audiopath = string.Empty;

            if (valid)
            {
                audiopath = await DownloadYoutubeVideo(url);
            }

            if(audiopath != string.Empty)
            {
                Server.NextFrame(() => PlayAudio(audiopath));
            }
        }

        public async Task<string> DownloadYoutubeVideo(string url)
        {
            DateTime dateTime = DateTime.Now;
            var timeshort = dateTime.ToString("HHmmss");

            var dest = Path.Combine(ModuleDirectory, "temp");

            if(!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
            }

            var inputPath = Path.Combine(dest, $"video_{timeshort}.mp4");
            var outputPath = Path.Combine(dest, $"audio_{timeshort}.mp3");

            using (var service = Client.For(YouTube.Default))
            {
                var vid = await service.GetVideoAsync(url);
                await File.WriteAllBytesAsync(inputPath, await vid.GetBytesAsync());

                FFMpegCore.FFMpeg.ExtractAudio(inputPath, outputPath);

                File.Delete(inputPath);
            }

            return outputPath;
        }

        static async Task<bool> IsVideoValid(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync(url); 
                    string responseContent = await response.Content.ReadAsStringAsync();
                    bool isValid = response.IsSuccessStatusCode && !responseContent.Contains("Video unavailable");
                    return isValid; 
                } 
                catch 
                { 
                    // If an exception occurs, the URL is likely not valid or not accessible
                    return false; 
                } 
            } 
        }
    }
}
