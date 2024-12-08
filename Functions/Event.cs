using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using Sympho.Models;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;
using System.Runtime;

namespace Sympho.Functions
{
    public class Event
    {
        private readonly ILogger<Sympho> _logger;
        private AudioService? audioService;
        private AudioHandler? audioHandler;
        private Sympho? _plugin;
        private Settings? _settings;

        public Event(Sympho plugin, ILogger<Sympho> logger)
        {
            _plugin = plugin;
            _logger = logger;
        }

        public void Initialize(AudioService service, AudioHandler handler)
        {
            audioService = service;
            audioHandler = handler;

            if(_plugin == null)
            {
                _logger.LogError("Core plugin is null!");
                return;
            }

            _plugin.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
            _plugin.RegisterListener<OnMapStart>(OnMapStart);
        }

        public void InitialConfigs(Settings settings)
        {
            _settings = settings;
        }

        public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
        {
            if (audioService == null)
                return HookResult.Continue;

            if (!audioService.ConfigsLoaded)
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

                audioHandler?.AudioCommandCheck(param1, isIndex, index);
            }

            else
                audioHandler?.AudioCommandCheck(param1, isIndex, -1);

            return HookResult.Continue;
        }

        public void OnMapStart(string mapname)
        {
            ClearTempFiles();
        }

        public void ClearTempFiles()
        {
            var path = Path.Combine(_plugin!.ModuleDirectory, "temp");

            try
            { 
                string[] files = Directory.GetFiles(path); 

                foreach (string file in files) 
                {
                    File.Delete(file); 
                }
                _logger.LogInformation("All sound files in the temp folder have been deleted."); 
            }
            catch (Exception ex) 
            {
                _logger.LogError("An error occurred: {0}", ex.Message); 
            }

        }
    }
}
