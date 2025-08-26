using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;

namespace Sympho.Functions
{
    /// <summary>
    /// 오디오 재생 트리거/중지와 자막 모듈 연동.
    /// - PlayAudio(...)는 global::Audio.PlayFromFile(...) 직접 호출
    /// - StopAudio()는 global::Audio.StopAllPlaying() 호출
    /// - ProcessConsoleMessage(...) 복구(지연/시작 로그 파싱)
    /// </summary>
    public class AudioHandler
    {
        private readonly ILogger<Sympho> _logger;
        private Sympho? _plugin;
        private Subtitles? _subtitles;

        // 로그 파싱용 정규식 (자막 지연 보정용)
        private static readonly Regex RxDelay =
            new(@"Max\s+delay\s+exceeded:\s*(?<ms>\d+)\s*milliseconds", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxStart =
            new(@"Start\s+playing", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AudioHandler(ILogger<Sympho> logger) => _logger = logger;

        // -------- 초기화/주입 --------
        public void Initialize(Sympho plugin) => _plugin = plugin;

        public void Initialize(Sympho plugin, Subtitles subtitles)
        {
            _plugin = plugin;
            _subtitles = subtitles;
        }

        /// <summary>Plugin.cs에서 두 번째 인수로 다른 객체가 넘어오는 경우 호환.</summary>
        public void Initialize(Sympho plugin, object second)
        {
            _plugin = plugin;

            if (second is Subtitles s)
            {
                _subtitles = s;
                return;
            }

            try
            {
                var t = second.GetType();
                var prop = t.GetProperty("Subtitles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? t.GetProperty("Subtitle",  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(Subtitles))
                {
                    _subtitles = (Subtitles?)prop.GetValue(second);
                    return;
                }

                var get = t.GetMethod("GetSubtitles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Array.Empty<Type>());
                if (get != null && get.ReturnType == typeof(Subtitles))
                {
                    _subtitles = (Subtitles?)get.Invoke(second, null);
                }
            }
            catch { /* ignore */ }
        }

        public void SetSubtitles(Subtitles subtitles) => _subtitles = subtitles;

        // -------- 재생/정지 --------

        public void PlayAudio(string source)
        {
            try
            {
                var path = NormalizeAudioPath(source);
                if (path == null)
                {
                    _logger.LogWarning("[AudioHandler] PlayAudio: file not found: {src}", source);
                    return;
                }

                // 자막 모듈에 재생 시작 알림
                _subtitles?.OnAudioStarted();

                // 볼륨 추출(없으면 1.0)
                var volume = GetVolumeOrDefault();

                // 정적 Audio API 호출 (AudioPlayer.cs 제공)
                global::Audio.PlayFromFile(path, volume);

                _logger.LogInformation("[AudioHandler] Playback started: {path} (vol={vol})", path, volume);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AudioHandler] PlayAudio failed");
            }
        }

        public void PlayAudio(ulong steamId, string source) => PlayAudio(source);

        public void PlayAudio(string source, bool force) => PlayAudio(source);

        /// <summary>Plugin.cs에서 정적으로 호출됨</summary>
        public static void StopAudio()
        {
            try { global::Audio.StopAllPlaying(); } catch { /* ignore */ }
        }

        /// <summary>
        /// 콘솔/로그 라인 전달 시 자막 지연과 시작 이벤트를 잡아 Subtitles에 전달.
        /// Plugin.cs(177)에서 호출되는 호환 메서드.
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

        // -------- 유틸 --------

        private string? NormalizeAudioPath(string inputPath)
        {
            try
            {
                if (File.Exists(inputPath)) return inputPath;

                var dir = Path.GetDirectoryName(inputPath);
                var name = Path.GetFileNameWithoutExtension(inputPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
                    return null;

                var candidates = Directory.GetFiles(dir, $"{name}*.mp3")
                                          .OrderByDescending(File.GetLastWriteTimeUtc)
                                          .ToList();
                if (candidates.Count > 0)
                    return candidates[0];
            }
            catch { /* ignore */ }

            return null;
        }

        private float GetVolumeOrDefault()
        {
            try
            {
                if (_plugin == null) return 1.0f;

                var prop = _plugin.GetType().GetProperty("CVAR_Volume",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var cvar = prop.GetValue(_plugin);
                    if (cvar != null)
                    {
                        var valProp = cvar.GetType().GetProperty("Value",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (valProp != null)
                        {
                            var val = valProp.GetValue(cvar);
                            if (val is float f) return f;
                            if (val is double d) return (float)d;
                            if (val is decimal m) return (float)m;
                            if (float.TryParse(val?.ToString(), out var parsed)) return parsed;
                        }
                    }
                }

                var volProp = _plugin.GetType().GetProperty("Volume",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volProp != null)
                {
                    var val = volProp.GetValue(_plugin);
                    if (val is float f) return f;
                    if (val is double d) return (float)d;
                    if (val is decimal m) return (float)m;
                    if (float.TryParse(val?.ToString(), out var parsed)) return parsed;
                }
            }
            catch { /* ignore */ }

            return 1.0f;
        }
    }
}
