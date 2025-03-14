using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using Microsoft.Extensions.Logging;
using Sympho.Models;

namespace Sympho.Functions
{
    public class AudioHandler
    {
        private readonly ILogger<Sympho> _logger;
        private AudioService? _audio;
        private Sympho? _plugin;

        public AudioHandler(Sympho plugin, ILogger<Sympho> logger)
        {
            _plugin = plugin;
            _logger = logger;
        }

        public void Initialize(AudioService audio)
        {
            _audio = audio;
        }

        public void AudioCommandCheck(CCSPlayerController client, string command, bool specific, int soundIndex = -1)
        {
            var admin = AdminManager.PlayerHasPermissions(client, "@css/kick");

            if(AntiSpamData.GetCooldownLeft() > 0 && !admin)
            {
                client.PrintToChat($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["AntiSpam.Cooldown", (int)AntiSpamData.GetCooldownLeft()]}");
                return;
            }

            if(!admin && AntiSpamData.GetPlayedCount() >= _plugin?.Config.MaxSpamPerInterval)
            {
                AntiSpamData.SetCooldown(Server.CurrentTime + _plugin.Config.AntiSpamCooldown);
                AntiSpamData.SetPlayedCount(0);
                Server.PrintToChatAll($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["AntiSpam.StopByAntiSpam", _plugin.Config.AntiSpamCooldown]}");
                return;
            }

            if (_audio == null)
                return;

            if (!_audio.ConfigsLoaded)
                return;

            // null or there is no audio in list.
            if (_audio.AudioList == null || _audio.AudioList.Count <= 0) return;

            // index always start with 0 but since we receive command from player who start count '1'
            if (specific)
                soundIndex -= 1;

            // if not specific let's assume as 0 first.
            else
                soundIndex = 0;

            var audioData = _audio.AudioList.Where(p => p.name != null && p.name.Contains(command)).FirstOrDefault();

            if(audioData == null)
                return;

            // in case there is only 1 sound
            if (audioData.sounds?.Count <= 1)
            {
                soundIndex = 0;
            }

            // if has a lot
            else
            {
                // if not specific then random number!
                if (!specific)
                {
                    var random = new Random();
                    soundIndex = random.Next(0, audioData.sounds!.Count);
                }
            }

            if(!Sympho.AllowPlaying)
            {
                client.PrintToChat($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["Audio.Disabled"]}");
                return;
            }

            if(Youtube.IsPlaying && Audio.IsAllPlaying())
            {
                client.PrintToChat($" {_plugin?.Localizer["Prefix"]} {_plugin?.Localizer["Youtube.WaitForFinish"]}");
                return;
            }

            Youtube.IsPlaying = false;

            // combine path of sound file
            var soundPath = Path.Combine(_audio.PluginDirectory!, $"sounds/{audioData.sounds![soundIndex]}");

            PlayAudio(soundPath);

            if(!admin)
                AntiSpamData.SetPlayedCount(AntiSpamData.PlayedCount + 1);
        }

        public void PlayAudio(string path)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("File is not found : {0}", path);
                return;
            }

            Audio.PlayFromFile(path, _plugin?.CVAR_Volume.Value ?? 1.0f);
        }

        public static void StopAudio()
        {
            Audio.StopAllPlaying();
        }
    }
}
