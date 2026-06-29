using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sympho.Functions;

public class AudioHandler
{
    private readonly ILogger<Sympho> _logger;
    private Sympho? _plugin;

    private static readonly Regex RxDelay =
        new(@"Max\s+delay\s+exceeded:\s*(?<ms>\d+)\s*milliseconds", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxStart =
        new(@"Start\s+playing", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AudioHandler(ILogger<Sympho> logger) => _logger = logger;

    public void Initialize(Sympho plugin) => _plugin = plugin;

    public bool PlayAudio(string source)
    {
        try
        {
            if (!File.Exists(source))
            {
                _logger.LogWarning("[AudioHandler] PlayAudio: file not found: {src}", source);
                return false;
            }

            var audio = _plugin?.GetAudio();
            if (audio == null)
            {
                _logger.LogWarning("[AudioHandler] Audio module is not available.");
                return false;
            }

            audio.PlayGlobal(Sympho.AudioChannel, source);
            _logger.LogInformation("[AudioHandler] Playback started: {path}", source);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AudioHandler] PlayAudio failed");
            return false;
        }
    }

    public void StopAudio()
    {
        try { _plugin?.GetAudio()?.StopGlobal(Sympho.AudioChannel); } catch { }
    }

    public void ProcessConsoleMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            if (RxStart.IsMatch(line))
            {
                return;
            }

            var m = RxDelay.Match(line);
            if (m.Success && int.TryParse(m.Groups["ms"].Value, out var ms))
            {
                _logger.LogDebug("[AudioHandler] Audio delay reported: {ms}ms", ms);
            }
        }
        catch
        {
        }
    }
}
