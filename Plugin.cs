using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Sympho.Functions;
using Sympho.Models;

namespace Sympho
{
    public partial class Sympho : BasePlugin
    {
        public override string ModuleName => "Sympho Audio Player";
        public override string ModuleVersion => "1.0";
        public override string ModuleAuthor => "Oylsister";

        private ILogger<Sympho> _logger;
        private AudioHandler _handler;
        private Youtube _youtube;
        public AudioService? AudioService { get; private set; }

        public Sympho(ILogger<Sympho> logger, AudioHandler handler, Youtube youtube)
        {
            _logger = logger;
            _handler = handler;
            _youtube = youtube;
        }

        public override void Load(bool hotReload)
        {
            AudioService = new AudioService();
            AudioService.PluginDirectory = ModuleDirectory;

            LoadConfig();

            _handler.Initialize(AudioService, this);
            _youtube.Initialize(this);
        }

        [GameEventHandler]
        public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
        {
            if (AudioService == null)
                return HookResult.Continue;

            if (!AudioService.ConfigsLoaded)
                return HookResult.Continue;

            var message = @event.Text;

            var split = message.Split(' ');

            var param1 = split.Length > 0 ? split[0] : string.Empty;
            var param2 = split.Length > 1 ? split[1] : string.Empty;

            var isIndex = int.TryParse(param2, out int index);

            if (isIndex)
            {
                if (index < 1)
                    index = 1;

                _handler.AudioCommandCheck(param1, isIndex, index);
            }

            else
                _handler.AudioCommandCheck(param1, isIndex, -1);

            return HookResult.Continue;
        }

        [CommandHelper(1, "css_yt <video-url>")]
        [ConsoleCommand("css_yt")]
        public void YoutubeCommand(CCSPlayerController client, CommandInfo info)
        {
            var fullarg = info.ArgString;
            var splitArg = fullarg.Split(" ");
            bool start = false;
            int starttime = 0;

            if (splitArg.Length > 1)
            {
                start = int.TryParse(splitArg[1], out starttime);
            }

            var url = splitArg[0];

            Task.Run(async () => {

                if (start)
                    await _youtube.ProceedYoutubeVideo(url, starttime);

                else
                    await _youtube.ProceedYoutubeVideo(url);
            });
        }

        void LoadConfig()
        {
            if (AudioService == null)
            {
                _logger.LogError("AudioServices is null!");
                return;
            }

            var configPath = Path.Combine(ModuleDirectory, "sounds/sounds.json");

            if(!File.Exists(configPath))
            {
                _logger.LogError("Couldn't find config file! {0}", configPath);
                AudioService.ConfigsLoaded = false;
                return;
            }

            AudioService.AudioList = JsonConvert.DeserializeObject<List<CAudioConfig>>(File.ReadAllText(configPath));

            var totalCommand = 0;
            var totalSound = 0;

            foreach(var audio in AudioService.AudioList!)
            {
                if(audio.name == null || audio.sounds == null)
                    continue;

                foreach(var name in audio.name)
                {
                    totalCommand++;    
                }

                foreach(var sound in audio.sounds)
                {
                    totalSound++;
                }
            }

            _logger.LogInformation("Found config file with {0} commands and {1} sound files", totalCommand, totalSound);
            AudioService.ConfigsLoaded = true;
        }

        public int GetAudioIndex(string command)
        {
            if (AudioService == null)
                return -1;

            if (AudioService.AudioList == null)
                return -1;

            for(int i = 0; i < AudioService.AudioList.Count; i++)
            {
                for(int j = 0; j < AudioService.AudioList[i].name!.Count; j++)
                {
                    if (AudioService.AudioList[i].name![j] == command)
                        return i;
                }
            }

            return -1;
        }
    }
}
