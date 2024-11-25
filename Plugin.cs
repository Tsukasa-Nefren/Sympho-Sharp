using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.UserMessages;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Sympho.Models;

namespace Sympho
{
    public partial class Sympho : BasePlugin
    {
        public override string ModuleName => "Sympho Audio Player";
        public override string ModuleVersion => "1.0";
        public override string ModuleAuthor => "Oylsister";

        public bool configsLoaded = false;
        private ILogger<Sympho> _logger;
        public List<CAudioConfig>? audioList;

        public Sympho(ILogger<Sympho> logger)
        {
            _logger = logger;
        }

        public override void Load(bool hotReload)
        {
            LoadConfig();
        }

        [GameEventHandler]
        public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
        {
            if (!configsLoaded)
            {
                return HookResult.Continue;
            }

            var message = @event.Text;

            var split = message.Split(' ');

            var param1 = split.Length > 0 ? split[0] : string.Empty;
            var param2 = split.Length > 1 ? split[1] : string.Empty;

            var isIndex = int.TryParse(param2, out int index);

            if (isIndex)
            {
                if (index < 1)
                    index = 1;

                AudioCommandCheck(param1, isIndex, index);
            }

            else
                AudioCommandCheck(param1, isIndex, -1);

            return HookResult.Continue;
        }

        void LoadConfig()
        {
            var configPath = Path.Combine(Server.GameDirectory, "csgo\\addons\\counterstrikesharp\\configs\\Sympho\\sounds.json");

            if(!File.Exists(configPath))
            {
                _logger.LogError("Couldn't find config file! {0}", configPath);
                configsLoaded = false;
                return;
            }

            audioList = JsonConvert.DeserializeObject<List<CAudioConfig>>(File.ReadAllText(configPath));

            var totalCommand = 0;
            var totalSound = 0;

            foreach(var audio in audioList!)
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
            configsLoaded = true;
        }

        public int GetAudioIndex(string command)
        {
            if (audioList == null)
                return -1;

            for(int i = 0; i < audioList.Count; i++)
            {
                for(int j = 0; j < audioList[i].name!.Count; j++)
                {
                    if (audioList[i].name![j] == command)
                        return i;
                }
            }

            return -1;
        }
    }
}
