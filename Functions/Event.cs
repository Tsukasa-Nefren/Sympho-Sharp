using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using Sympho.Models;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;

namespace Sympho.Functions
{
    public class Event
    {
        private readonly ILogger<Sympho> _logger;
        private AudioService? audioService;
        private AudioHandler? audioHandler;
        private Sympho? _plugin;

        public Event(ILogger<Sympho> logger)
        {
            _logger = logger;
        }

        public void Initialize(AudioService service, AudioHandler handler, Sympho plugin)
        {
            audioService = service;
            audioHandler = handler;
            _plugin = plugin;

            if(plugin == null)
            {
                _logger.LogError("Core plugin is null!");
                return;
            }

            plugin.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
            plugin.RegisterListener<OnMapStart>(OnMapStart);
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
