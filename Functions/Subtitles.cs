using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;       // NullLogger
using CounterStrikeSharp.API;                          // Utilities.GetPlayers()
using CounterStrikeSharp.API.Modules.Timers;          // TimerFlags
using YoutubeDLSharp;                                 // 영상 길이 조회

namespace Sympho.Functions
{
    public class SubtitleCue
    {
        public double StartTime { get; set; }   // seconds
        public double EndTime   { get; set; }   // seconds
        public string Text      { get; set; } = "";
    }

    /// <summary>
    /// 자막 랜더러: yt-dlp로 VTT를 받아 파싱하고, Stopwatch 기반으로 HUD에 출력.
    /// - 오디오 지연(로그) 반영
    /// - "끝에서 X초 늦는" 현상 보정:
    ///   * 곡선형 보정(기본): shift = targetEndDrift * (t/duration)^p, elapsed = baseElapsed + shift
    ///   * 명시적 비율(SetDriftRatio 호출) 시: 선형 비례 elapsed = baseElapsed * (1 + ratio)
    /// </summary>
    public class Subtitles : IDisposable
    {
        private Sympho? _plugin;
        private ILogger _logger;

        // 실행 경로
        private string? _ytDlpPath;         // 플러그인 루트의 yt-dlp 실행파일
        private string? _ffmpegPath;        // 필요시 사용 (없어도 무관)

        // 시간/지연
        private readonly Stopwatch _stopwatch = new();   // 기준 시계
        private readonly object _delayLock = new();
        private double _totalAudioDelay = 0.0;           // 보고된 오디오 지연(초) - 최대값 유지
        private const double INITIAL_BUFFER_DELAY = 0.00;
        private const double MAX_DELAY_CAP        = 0.50;
        private DateTime _lastDelayReport = DateTime.MinValue;

        // ---- 드리프트 보정 파라미터 ----
        private double _driftTargetAtEndSec = 3.5;  // 끝에서 당길 목표 초 (0이면 비활성)
        private double _expectedDurationSec = 0.0;  // 자막 기준 길이(마지막 cue EndTime)
        private double _externalDurationSec = 0.0;  // YoutubeDLSharp로 얻은 길이(초)
        private double _durationBasisSec    = 0.0;  // 보정 기준 길이(외부 or 자막)
        private double _driftRatio          = 0.0;  // 선형 비율 (명시적일 때만 사용)
        private bool   _explicitDriftRatio  = false;

        // 곡선형 보정용 power (p>1이면 초반 당김 감소). 기본 auto.
        private bool   _useAutoPower = true;
        private double _driftPower   = 1.8;  // 수동 설정 시 사용

        // 자막/표시 상태
        private List<SubtitleCue> _currentSubtitles = new();
        private List<SubtitleCue> _lastDisplayedCues = new();
        private string _lastDisplayedHtml = "";
        private double _lastCueEndTime = -1.0;
        private float  _timeSinceLastHudUpdate = 0.0f;

        // 업데이트 타이머 (모호성 방지: 풀네임 타입 사용)
        private CounterStrikeSharp.API.Modules.Timers.Timer? _subtitleTimer;
        private const float TICK_4_INTERVAL      = 0.0625f; // 4틱(≈62.5ms)
        private const float HUD_REFRESH_INTERVAL = 0.25f;   // 250ms

        private bool _disposed;

        // ===== 생성자들 =====
        public Subtitles()                      { _logger = NullLogger.Instance; }
        public Subtitles(ILogger logger)        { _logger = logger ?? NullLogger.Instance; }

        /// <summary>필요하다면 동적으로 로거를 연결</summary>
        public void AttachLogger(ILogger logger){ _logger = logger ?? NullLogger.Instance; }

        public void Initialize(Sympho plugin)
        {
            _plugin = plugin;

            // 플러그인 루트에서 yt-dlp / ffmpeg 경로 확정
            var root = plugin.ModuleDirectory;
            var ytdlpCand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "yt-dlp.exe", "yt-dlp" }
                : new[] { "yt-dlp", "yt-dlp.exe" };

            foreach (var name in ytdlpCand)
            {
                var full = Path.Combine(root, name);
                if (File.Exists(full)) { _ytDlpPath = full; break; }
            }

            var ffmpegCand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "ffmpeg.exe", "ffmpeg" }
                : new[] { "ffmpeg", "ffmpeg.exe" };

            foreach (var name in ffmpegCand)
            {
                var full = Path.Combine(root, name);
                if (File.Exists(full)) { _ffmpegPath = full; break; }
            }

            if (_ytDlpPath is null)
                _logger.LogWarning("[Subtitles] yt-dlp not found in plugin root; subtitles download will be skipped.");
            else
                _logger.LogInformation("[Subtitles] yt-dlp: {exe}", _ytDlpPath);
        }

        // ===== 외부 설정 API =====

        /// <summary>끝에서 당길 초(곡선형 보정 기준). 0이면 보정하지 않음.</summary>
        public void SetGlobalEndDrift(double seconds)
        {
            _driftTargetAtEndSec = Math.Max(0.0, seconds);
            if (!_explicitDriftRatio) _driftRatio = 0.0; // 다음 시작 시 재계산
            _logger.LogInformation("[Subtitles] Set end-drift target: {sec:F3}s", _driftTargetAtEndSec);
        }

        /// <summary>선형 보정 비율을 직접 지정 (예: 0.012 = 1.2%). 지정 시 곡선형 대신 선형 사용.</summary>
        public void SetDriftRatio(double ratio)
        {
            _driftRatio = Math.Max(0.0, ratio);
            _explicitDriftRatio = true;
            _logger.LogInformation("[Subtitles] Set drift ratio (linear): {pct:P3}", _driftRatio);
        }

        /// <summary>곡선형 보정 power를 수동 지정 (p &gt;= 1). null 이면 자동.</summary>
        public void SetDriftPower(double? power)
        {
            if (power.HasValue)
            {
                _driftPower   = Math.Max(1.0, power.Value);
                _useAutoPower = false;
                _logger.LogInformation("[Subtitles] Set drift power (manual): {p:F2}", _driftPower);
            }
            else
            {
                _useAutoPower = true;
                _logger.LogInformation("[Subtitles] Drift power set to AUTO");
            }
        }

        // ===== 오디오 이벤트/지연 보고 =====

        public void OnAudioStarted()
        {
            lock (_delayLock) _totalAudioDelay = 0.0;
            _stopwatch.Restart();
            _timeSinceLastHudUpdate = 0.0f;
            _logger.LogDebug("[Subtitles] Audio started: reset delay & stopwatch");
        }

        public void ReportAudioDelay(double delayMilliseconds)
        {
            var newDelay = Math.Max(0.0, delayMilliseconds / 1000.0);
            lock (_delayLock)
            {
                _totalAudioDelay = Math.Max(_totalAudioDelay, newDelay);
                if (_totalAudioDelay > MAX_DELAY_CAP) _totalAudioDelay = MAX_DELAY_CAP;
            }
            _lastDelayReport = DateTime.UtcNow;
        }

        // ===== 자막 로드/파싱 =====

        public async Task<List<SubtitleCue>> FetchAndParseSubtitlesAsync(string url)
        {
            var cues = new List<SubtitleCue>();
            try
            {
                if (_plugin == null) return cues;

                var videoId = GetYouTubeVideoId(url);
                if (string.IsNullOrEmpty(videoId)) return cues;

                var root = _plugin.ModuleDirectory;
                var tmp  = Path.Combine(root, "tmp");
                Directory.CreateDirectory(tmp);

                if (_ytDlpPath is null || !File.Exists(_ytDlpPath))
                {
                    _logger.LogWarning("[Subtitles] yt-dlp missing; skipping subtitle download.");
                    return cues;
                }

                var outTemplate = Path.Combine(tmp, "%(id)s.%(ext)s");
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    WorkingDirectory = root,
                    Arguments =
                        "--no-warnings --ignore-config --no-check-certificates --skip-download " +
                        "--write-subs --no-write-auto-subs --sub-langs \"ko,en,ja\" --convert-subs vtt " +
                        $"--output \"{outTemplate}\" \"{url}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                _logger.LogInformation("[Subtitles] running yt-dlp: {exe} (wd={wd})", psi.FileName, psi.WorkingDirectory);

                using var p = Process.Start(psi);
                if (p == null)
                {
                    _logger.LogWarning("[Subtitles] failed to start yt-dlp process.");
                    return cues;
                }

                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                var stderr = await errTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogDebug("[yt-dlp] {msg}", stderr.Trim());

                // 최신 vtt 선택 (id로 시작하는 파일)
                var vtt = Directory.EnumerateFiles(tmp, "*.vtt")
                    .Select(fp => new FileInfo(fp))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .FirstOrDefault(fi =>
                        Path.GetFileName(fi.FullName).StartsWith(videoId, StringComparison.OrdinalIgnoreCase));

                if (vtt == null)
                {
                    _logger.LogInformation("[Subtitles] No non-auto VTT subtitles found.");
                    return cues;
                }

                cues = await ParseVttFile(vtt.FullName);

                // --- 여기서 YoutubeDLSharp로 영상 길이 조회 → 드리프트 기준 준비 ---
                await TryUpdateExternalDurationAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch/parse subtitles");
            }
            return cues;
        }

        private async Task TryUpdateExternalDurationAsync(string url)
        {
            try
            {
                if (_ytDlpPath is null) return;

                var ytdl = new YoutubeDL
                {
                    YoutubeDLPath = _ytDlpPath,
                    FFmpegPath    = _ffmpegPath
                };

                var res = await ytdl.RunVideoDataFetch(url);
                if (res.Success && res.Data.Duration.HasValue)
                {
                    _externalDurationSec = Math.Max(0.0, res.Data.Duration.Value);
                    _logger.LogInformation("[Subtitles] Video duration via DLSharp: {sec:F2}s", _externalDurationSec);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Subtitles] Failed to query duration via DLSharp");
            }
        }

        private async Task<List<SubtitleCue>> ParseVttFile(string vttPath)
        {
            var cues = new List<SubtitleCue>();
            try
            {
                string content;
                using (var sr = new StreamReader(vttPath, Encoding.UTF8, true))
                    content = await sr.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(content)) return cues;

                var blocks = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var block in blocks)
                {
                    var trimmed = block.Trim();
                    if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;

                    var lines = trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    var timeLine = lines.FirstOrDefault(l => l.Contains("-->"));
                    if (timeLine is null) continue;

                    var m = Regex.Match(timeLine, @"^([\d:.]+)\s*-->\s*([\d:.]+)");
                    if (!m.Success) continue;

                    var start = ParseTimeToSeconds(m.Groups[1].Value);
                    var end   = ParseTimeToSeconds(m.Groups[2].Value);
                    if (end <= start) continue;

                    // 멀티라인 → <br /> 유지
                    var text = string.Join("<br />", lines.SkipWhile(l => !l.Contains("-->")).Skip(1)).Trim();
                    text = Regex.Replace(text, "<.*?>", string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        cues.Add(new SubtitleCue { StartTime = start, EndTime = end, Text = text });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse VTT: {path}", vttPath);
            }
            return cues.OrderBy(c => c.StartTime).ToList();
        }

        private static double ParseTimeToSeconds(string t)
        {
            var parts = t.Trim().Split('.');
            var main = parts[0];
            var ms = (parts.Length > 1 && int.TryParse(parts[1], out var _ms)) ? _ms : 0;

            var hms = main.Split(':');
            int h = 0, m = 0, s = 0;
            if (hms.Length == 3) { h = int.Parse(hms[0]); m = int.Parse(hms[1]); s = int.Parse(hms[2]); }
            else if (hms.Length == 2) { m = int.Parse(hms[0]); s = int.Parse(hms[1]); }

            return h * 3600 + m * 60 + s + ms / 1000.0;
        }

        // ===== 실시간 렌더링 =====

        public void StartRealtimeSubtitles(List<SubtitleCue> cues)
        {
            if (cues == null || cues.Count == 0) return;

            StopRealtimeSubtitles();

            _currentSubtitles = cues;
            _lastDisplayedCues.Clear();
            _lastDisplayedHtml = "";
            _lastCueEndTime = -1.0;
            _timeSinceLastHudUpdate = 0.0f;

            // 자막 기준 총 길이(마지막 cue EndTime)
            _expectedDurationSec = Math.Max(0.0, cues.Max(c => c.EndTime));
            _durationBasisSec    = (_externalDurationSec > 5.0) ? _externalDurationSec : _expectedDurationSec;

            OnAudioStarted(); // stopwatch 리셋

            _subtitleTimer = _plugin!.AddTimer(
                TICK_4_INTERVAL,
                () => UpdateSubtitles(TICK_4_INTERVAL),
                TimerFlags.REPEAT
            );

            if (_explicitDriftRatio)
            {
                _logger.LogInformation("[Subtitles] started with {Count} cues, linear ratio: {pct:P3}",
                    _currentSubtitles.Count, _driftRatio);
            }
            else if (_driftTargetAtEndSec > 0.0 && _durationBasisSec > 5.0)
            {
                var p = _useAutoPower ? ComputeAutoPower(_durationBasisSec) : _driftPower;
                _logger.LogInformation("[Subtitles] started with {Count} cues, curve drift: target {end:F2}s, basis {dur:F2}s, power {p:F2}",
                    _currentSubtitles.Count, _driftTargetAtEndSec, _durationBasisSec, p);
            }
            else
            {
                _logger.LogInformation("[Subtitles] started with {Count} cues (no drift correction)", _currentSubtitles.Count);
            }
        }

        public void StopRealtimeSubtitles()
        {
            _subtitleTimer?.Kill();
            _subtitleTimer = null;

            _currentSubtitles.Clear();
            _lastDisplayedCues.Clear();
            _lastDisplayedHtml = "";
            _lastCueEndTime = -1.0;
            _timeSinceLastHudUpdate = 0.0f;

            lock (_delayLock) _totalAudioDelay = 0.0;

            // 다음 틱에 HUD 지우기
            _plugin?.AddTimer(0.0f, () =>
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    try { p.PrintToCenterHtml(""); } catch { }
                }
            });

            // 다음 곡에서 자동 계산을 다시 허용 (명시 세팅은 유지)
            _externalDurationSec = 0.0;
            _expectedDurationSec = 0.0;
            _durationBasisSec    = 0.0;
        }

        private void UpdateSubtitles(float frameTime)
        {
            const double LINGER = 7.0; // 마지막 자막 잔상 유지

            _timeSinceLastHudUpdate += frameTime;

            // Stopwatch 기반 진행 시간 + 지연 보정
            var rawElapsed = _stopwatch.Elapsed.TotalSeconds;

            double delay;
            lock (_delayLock) delay = _totalAudioDelay;

            var baseElapsed = Math.Max(0.0, rawElapsed - (INITIAL_BUFFER_DELAY + delay));

            double elapsed;

            if (_explicitDriftRatio && _driftRatio > 0.0)
            {
                // 명시적 선형 보정(과거 방식 유지)
                elapsed = baseElapsed * (1.0 + _driftRatio);
            }
            else if (_driftTargetAtEndSec > 0.0 && _durationBasisSec > 5.0)
            {
                // 곡선형 보정: shift = target * (t/d)^p
                var x = Math.Clamp(baseElapsed / _durationBasisSec, 0.0, 1.0);
                var p = _useAutoPower ? ComputeAutoPower(_durationBasisSec) : _driftPower;
                var shift = _driftTargetAtEndSec * Math.Pow(x, p);
                elapsed = baseElapsed + shift;
            }
            else
            {
                elapsed = baseElapsed;
            }

            // 표시할 자막 선택
            var active = _currentSubtitles.Where(c => c.StartTime <= elapsed && elapsed <= c.EndTime).ToList();

            string newHtml;
            if (active.Count > 0)
            {
                newHtml = BuildSubtitleHtml(active);
                _lastDisplayedCues = active;
                _lastCueEndTime = active.Max(c => c.EndTime);
            }
            else if (_lastDisplayedCues.Count > 0 && elapsed <= _lastCueEndTime + LINGER)
            {
                newHtml = BuildSubtitleHtml(_lastDisplayedCues);
            }
            else
            {
                newHtml = "<font color='white' size='5'>&nbsp;</font>";
                if (_lastDisplayedCues.Count > 0) _lastDisplayedCues.Clear();
            }

            // HUD 업데이트(변경되었거나 250ms 주기로)
            if (newHtml != _lastDisplayedHtml || _timeSinceLastHudUpdate >= HUD_REFRESH_INTERVAL)
            {
                _lastDisplayedHtml = newHtml;
                _timeSinceLastHudUpdate = 0.0f;

                _plugin!.AddTimer(0.0f, () =>
                {
                    foreach (var p in Utilities.GetPlayers())
                    {
                        try { p.PrintToCenterHtml(newHtml); } catch { }
                    }
                });
            }

            // 지연 로그가 한동안 없으면 지연값 천천히 감쇠(선택)
            if (_totalAudioDelay > 0.0 && (DateTime.UtcNow - _lastDelayReport).TotalSeconds > 2.0)
            {
                lock (_delayLock)
                {
                    _totalAudioDelay = Math.Max(0.0, _totalAudioDelay - 0.01); // 초당 0.01s 감소
                }
            }
        }

        private static double BuildPowerManualOrAuto(double manual, bool useAuto, double basisSec)
        {
            return useAuto ? ComputeAutoPower(basisSec) : Math.Max(1.0, manual);
        }

        /// <summary>
        /// 자동 power: 곡이 짧을수록 p를 크게(초반 당김 억제 강화).
        /// 예) 90s → ≈2.53, 135s → ≈2.09, 240s → ≈1.70, 300s → ≈1.60
        /// </summary>
        private static double ComputeAutoPower(double basisSec)
        {
            // p = clamp(1.2 + 120 / basis, 1.2, 2.6)
            var p = 1.2 + 120.0 / Math.Max(30.0, basisSec);
            if (p < 1.2) p = 1.2;
            if (p > 2.6) p = 2.6;
            return p;
        }

        private static string BuildSubtitleHtml(IEnumerable<SubtitleCue> cues)
        {
            var text = string.Join("<br />", cues.Select(c => WebUtility.HtmlEncode(c.Text)));
            return $"<font color='white' size='5'>{text}</font>";
        }

        // ===== 유틸 =====

        private static string GetYouTubeVideoId(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            var rx = new Regex(@"(?:youtu\.be\/|v=)([A-Za-z0-9_\-]{6,})", RegexOptions.IgnoreCase);
            var m = rx.Match(url);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        // ===== IDisposable =====
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { StopRealtimeSubtitles(); } catch { /* ignore */ }
        }
    }
}
