using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sympho.Functions
{
    public class AudioHandler
    {
        private readonly ILogger<Sympho> _logger;
        private Sympho? _plugin;
        private Subtitles? _subtitles;

        // Regex for parsing log lines for subtitle delay correction
        private static readonly Regex RxDelay =
            new(@"Max\s+delay\s+exceeded:\s*(?<ms>\d+)\s*milliseconds", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxStart =
            new(@"Start\s+playing", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AudioHandler(ILogger<Sympho> logger) => _logger = logger;
        
        public void Initialize(Sympho plugin) => _plugin = plugin;

        public void SetSubtitles(Subtitles subtitles) => _subtitles = subtitles;
        
        public void PlayAudio(string source)
        {
            try
            {
                if (!File.Exists(source))
                {
                    _logger.LogWarning("[AudioHandler] PlayAudio: file not found: {src}", source);
                    return;
                }

                _subtitles?.OnAudioStarted();
                var volume = GetVolumeOrDefault();
                global::Audio.PlayFromFile(source, volume);

                _logger.LogInformation("[AudioHandler] Playback started: {path} (vol={vol})", source, volume);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AudioHandler] PlayAudio failed");
            }
        }

        public static void StopAudio()
        {
            try { global::Audio.StopAllPlaying(); } catch { /* ignore */ }
        }

        /// <summary>
        /// Forwards console/log lines to the Subtitles module to detect delays and start events.
        /// </summary>
        public void ProcessConsoleMessage(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                if (RxStart.IsMatch(line))
                {
                    _subtitles?.OnAudioStarted();
                    return;
                }

                var m = RxDelay.Match(line);
                if (m.Success && int.TryParse(m.Groups["ms"].Value, out var ms))
                {
                    _subtitles?.ReportAudioDelay(ms);
                }
            }
            catch
            {
                // ignore
            }
        }

        private float GetVolumeOrDefault()
        {
            try
            {
                if (_plugin == null) return 1.0f;
                // A more direct and safer way to get the FakeConVar value.
                var cvar = _plugin.CVAR_Volume;
                if (cvar != null) return cvar.Value;
            }
            catch { /* ignore */ }

            return 1.0f;
        }
    }
}