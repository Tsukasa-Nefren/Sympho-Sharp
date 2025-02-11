using System.Xml.Schema;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Sympho.Functions;
using Sympho.Models;

namespace Sympho
{
    public partial class Sympho : BasePlugin, IPluginConfig<Settings>
    {
        public override string ModuleName => "Sympho Audio Player";
        public override string ModuleVersion => "Alpha 1.2";
        public override string ModuleAuthor => "Oylsister";

        private ILogger<Sympho> _logger;
        private AudioHandler? _handler;
        private Youtube? _youtube;
        private Event? _event;
        public AudioService? AudioService { get; private set; }
        public Settings Config { get; set; } = new();
        public CounterStrikeSharp.API.Modules.Timers.Timer? SpamTimerCheck = null;
        public FakeConVar<float> CVAR_Volume = new FakeConVar<float>("css_sympho_volume", "Volume of Sympho sound", 1.0f, ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.1f, 1.0f));

        public Sympho(ILogger<Sympho> logger)
        {
            _logger = logger;
        }

        public void OnConfigParsed(Settings config)
        {
            Config = config;

            _event?.InitialConfigs(config);
        }

        public override void Load(bool hotReload)
        {
            AudioService = new AudioService();
            AudioService.PluginDirectory = ModuleDirectory;

            _handler = new(this, _logger);
            _youtube = new(this, _handler, _logger);
            _event = new(this, _logger);

            LoadConfig();

            _handler.Initialize(AudioService);
            _youtube.Initialize();
            _event.Initialize(AudioService, _handler);
        }

        [CommandHelper(1, "css_yt <video-url> [start-seconds]")]
        [ConsoleCommand("css_yt")]
        public void YoutubeCommand(CCSPlayerController client, CommandInfo info)
        {
            bool allow = true;
            bool admin = AdminManager.PlayerHasPermissions(client, "@css/kick");

            if (!Config.AllowYoutube)
            {
                if (admin)
                {
                    if(Config.BypassYoutubeAdmin)
                        allow = true;

                    else
                        allow = false;
                }

                else
                    allow = false;

                if(!allow)
                {
                    info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Youtube.NotAllowed"]}");
                    return;
                }
            }

            if(AntiSpamData.GetCooldownLeft() > 0 && !admin)
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["AntiSpam.Cooldown", (int)AntiSpamData.GetCooldownLeft()]}");
                return;
            }

            if(Youtube.IsPlaying && Audio.IsAllPlaying())
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Youtube.WaitForFinish"]}");
                return;
            }

            var fullarg = info.ArgString;
            var splitArg = fullarg.Split(" ");
            bool start = false;
            int starttime = 0;

            if (splitArg.Length > 1)
            {
                start = int.TryParse(splitArg[1], out starttime);
            }

            var url = splitArg[0];

            if(!splitArg[0].StartsWith("https://www.youtube.com/"))
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} Your didn't provide a link!");
                return;
            }

            // if normal player then
            if(!admin)
                AntiSpamData.SetPlayedCount(AntiSpamData.PlayedCount + 1);

            Task.Run(async () => {

                if(Config.MaxAudioLength > 0)
                {
                    if (start)
                        await _youtube!.ProceedYoutubeVideo(url, starttime, admin ? 0 : starttime + Config.MaxAudioLength);

                    else
                        await _youtube!.ProceedYoutubeVideo(url, 0, Config.MaxAudioLength);
                }

                else
                {
                    if (start)
                        await _youtube!.ProceedYoutubeVideo(url, starttime);

                    else
                        await _youtube!.ProceedYoutubeVideo(url);
                }
            });
        }

        [RequiresPermissions("@css/kick")]
        [ConsoleCommand("css_stopall")]
        public void StopAllSound(CCSPlayerController client, CommandInfo info)
        {
            if (Audio.IsAllPlaying())
            {
                Server.PrintToChatAll($" {Localizer["Prefix"]} {Localizer["Audio.AllStop"]}");
                AudioHandler.StopAudio();
            }
        }

        [ConsoleCommand("css_search")]
        public void SearchCommand(CCSPlayerController client, CommandInfo info)
        {
            if (info.ArgCount < 2)
            {
                var result = AudioService?.AudioList?.SelectMany(sound => sound.name!).ToList();

                if (result == null || result.Count <= 0)
                {
                    info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.NoResult"]}");
                    return;
                }

                var soundlist = string.Join(", ", result.Take(20));

                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.Result"]} {soundlist}");

                if(result.Count > 20)
                    info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.MoreResult", result.Count - 20]}");
            }

            else
            {
                var search = info.ArgString;
                var result = AudioService?.AudioList?.SelectMany(sound => sound.name!).Where(sound => sound.Contains(search)).ToList();

                if (result == null || result.Count <= 0)
                {
                    info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.NoResult"]}");
                    return;
                }

                var soundlist = string.Join(", ", result.Take(20));
                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.Result"]} {soundlist}");

                if (result.Count > 20)
                    info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Search.MoreResult", result.Count - 20]}");
            }
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

        public void CheckSpam()
        {
            // if already blocked then do nothing.
            if(AntiSpamData.AvailableAgain > Server.CurrentTime)
                return;

            // just reset the played count.
            AntiSpamData.SetPlayedCount(0);
        }
    }
}
