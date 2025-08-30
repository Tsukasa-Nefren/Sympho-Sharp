using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace Sympho.Functions
{
    public class SubtitleCue
    {
        public double StartTime { get; set; }
        public double EndTime   { get; set; }
        public string Text      { get; set; } = "";
        public int LinePercent { get; set; } = 90; 
        public string Position { get; set; } = ""; // align:center, align:left, align:right
        public string Size { get; set; } = ""; // size:80%
    }

    public class Subtitles : IDisposable
    {
        private Sympho? _plugin;
        private readonly CacheManager _cache;
        private readonly ILogger<Sympho> _logger;
        private string? _ytDlpPath;

        // 다양한 색상 형식 지원을 위한 사전들
        private readonly Dictionary<string, string> _vttStyles = new();
        private readonly Dictionary<string, string> _predefinedColors = new()
        {
            // HTML 색상 이름들
            { "white", "#FFFFFF" }, { "black", "#000000" }, { "red", "#FF0000" }, { "green", "#00FF00" },
            { "blue", "#0000FF" }, { "yellow", "#FFFF00" }, { "cyan", "#00FFFF" }, { "magenta", "#FF00FF" },
            { "silver", "#C0C0C0" }, { "gray", "#808080" }, { "grey", "#808080" }, { "maroon", "#800000" },
            { "olive", "#808000" }, { "lime", "#00FF00" }, { "aqua", "#00FFFF" }, { "teal", "#008080" },
            { "navy", "#000080" }, { "fuchsia", "#FF00FF" }, { "purple", "#800080" }, { "orange", "#FFA500" },
            { "pink", "#FFC0CB" }, { "brown", "#A52A2A" }, { "gold", "#FFD700" }, { "violet", "#EE82EE" },
            
            // 유튜브에서 자주 사용하는 색상들
            { "bg_transparent", "transparent" }, { "bg_black", "#000000" }, { "bg_blue", "#0000FF" },
            { "bg_cyan", "#00FFFF" }, { "bg_green", "#00FF00" }, { "bg_magenta", "#FF00FF" },
            { "bg_red", "#FF0000" }, { "bg_white", "#FFFFFF" }, { "bg_yellow", "#FFFF00" }
        };

        private double _subtitleLeadTime = 0.0;
        private const double MAX_LEAD_TIME = 2.0;
        private const double MIN_LEAD_TIME = 0.0;

        // [수정됨] 시간 드리프트 보정 계수를 하드코딩합니다.
        // 자막이 점점 늦어지면 이 값을 1.0보다 작게 설정 (예: 0.999)
        // 자막이 점점 빨라지면 이 값을 1.0보다 크게 설정 (예: 1.001)
        private const double _driftCorrectionFactor = 1.007; 

        private double _audioProgressSec = 0.0;
        private bool _isAudioPlaying = false;
        
        private List<SubtitleCue> _currentSubtitles = new();
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? _subtitleTimer;
        
        private const double LINGER_DURATION = 5.0;
        private List<SubtitleCue> _lastShownCues = new List<SubtitleCue>();

        private int _playListenerId = -1;
        private int _playStartListenerId = -1;
        private int _playEndListenerId = -1;
        private bool _audioApiRegistered = false;

        private Audio.PlayHandler? _playHandler;
        private Audio.PlayStartHandler? _playStartHandler;
        private Audio.PlayEndHandler? _playEndHandler;

        private float _playbackStartTime = 0f;

        public Subtitles(ILogger<Sympho> logger, CacheManager cache) 
        { 
            _logger = logger; 
            _cache = cache; 
        }

        public void Initialize(Sympho plugin)
        {
            _plugin = plugin;
            var root = plugin.ModuleDirectory;
            var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "yt-dlp.exe", "yt-dlp" } : new[] { "yt-dlp", "yt-dlp.exe" };
            foreach (var name in candidates)
            {
                var full = Path.Combine(root, name);
                if (File.Exists(full)) { _ytDlpPath = full; break; }
            }
            if (_ytDlpPath is null) _logger.LogWarning("[Subtitles] yt-dlp executable not found in plugin directory. Subtitle downloads will be skipped.");
            else _logger.LogInformation("[Subtitles] yt-dlp found at: {path}", _ytDlpPath);

            RegisterAudioListeners();
        }

        private void RegisterAudioListeners()
        {
            try
            {
                _playHandler = OnAudioProgress;
                _playStartHandler = OnAudioStarted;
                _playEndHandler = OnAudioEnded;
                _playListenerId = Audio.RegisterPlayListener(_playHandler);
                _playStartListenerId = Audio.RegisterPlayStartListener(_playStartHandler);
                _playEndListenerId = Audio.RegisterPlayEndListener(_playEndHandler);
                _audioApiRegistered = true;
                _logger.LogInformation("[Subtitles] Audio API listeners registered successfully. IDs: Play={0}, Start={1}, End={2}", 
                    _playListenerId, _playStartListenerId, _playEndListenerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Subtitles] Failed to register audio API listeners. Subtitle sync may not work properly.");
                _audioApiRegistered = false;
            }
        }

        private void OnAudioProgress(int slot, int progressMs) { }

        private void OnAudioStarted(int slot)
        {
            Server.NextFrame(() =>
            {
                if (slot == -1)
                {
                    _audioProgressSec = 0.0;
                    _isAudioPlaying = true;
                    _playbackStartTime = Server.CurrentTime;
                    _logger.LogInformation("[Subtitles] Audio started, resetting progress and starting internal timer.");
                }
            });
        }

        private void OnAudioEnded(int slot)
        {
            Server.NextFrame(() =>
            {
                if (slot == -1)
                {
                    _isAudioPlaying = false;
                    _logger.LogInformation("[Subtitles] Audio ended.");
                    
                    foreach (var p in Utilities.GetPlayers())
                        if (p != null && p.IsValid)
                            try { p.PrintToCenterHtml(""); } catch { }
                }
            });
        }
        
        public void OnAudioStarted() 
        { 
            _audioProgressSec = 0.0;
            _isAudioPlaying = true;
        }

        public void ReportAudioDelay(double delayMilliseconds)
        {
            var delaySeconds = delayMilliseconds / 1000.0;
            if (delaySeconds > 0.05)
            {
                var newLeadTime = Math.Min(MAX_LEAD_TIME, Math.Max(MIN_LEAD_TIME, _subtitleLeadTime + delaySeconds * 0.8));
                if (Math.Abs(newLeadTime - _subtitleLeadTime) > 0.01)
                {
                    _subtitleLeadTime = newLeadTime;
                    _logger.LogInformation("[Subtitles] Audio delay detected. Adjusted subtitle lead time to {lead:F3}s.", _subtitleLeadTime);
                }
            }
        }

        public async Task<List<SubtitleCue>> FetchAndParseSubtitlesAsync(string url, string? langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return new List<SubtitleCue>();
            
            var cues = new List<SubtitleCue>();
            try
            {
                if (_plugin == null || _ytDlpPath == null || !File.Exists(_ytDlpPath)) return cues;
                var videoId = Youtube.GetYouTubeVideoId(url);
                if (string.IsNullOrEmpty(videoId)) return cues;
                var tmpDir = Path.GetDirectoryName(_cache.GetPathForUrl(url))!;
                var outTemplate = Path.Combine(tmpDir, "%(id)s.%(ext)s");
                
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    WorkingDirectory = _plugin.ModuleDirectory,
                    Arguments = $"--no-warnings --ignore-config --no-check-certificates --skip-download --write-subs --sub-langs \"{langCode}\" --convert-subs vtt --output \"{outTemplate}\" \"{url}\"",
                    UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                if (p == null) return cues;
                await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);
                
                var vttFile = Directory.EnumerateFiles(tmpDir, $"{videoId}*.vtt")
                                       .Select(fp => new FileInfo(fp))
                                       .FirstOrDefault(f => f.Name.Contains(langCode));

                if (vttFile == null) 
                {
                    _logger.LogWarning("[Subtitles] Could not find a downloaded VTT file for language '{langCode}'.", langCode);
                    return cues;
                }
                cues = await ParseVttFile(vttFile.FullName);
                _logger.LogInformation("[Subtitles] Successfully parsed {count} subtitle cues from VTT file.", cues.Count);
            }
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "An error occurred while fetching subtitles for language '{langCode}'", langCode); 
            }
            return cues;
        }

        private async Task<List<SubtitleCue>> ParseVttFile(string vttPath)
        {
            var cues = new List<SubtitleCue>();
            try
            {
                var content = await File.ReadAllTextAsync(vttPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(content)) return cues;
                ParseVttStyleBlock(content);
                content = Regex.Replace(content, @"NOTE\s+.*?(?=\r?\n\r?\n)", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var blocks = Regex.Split(content, @"(?:\r?\n){2,}");
                
                foreach (var block in blocks)
                {
                    var trimmed = block.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    
                    if (trimmed.StartsWith("WEBVTT") || trimmed.StartsWith("Kind:") || trimmed.StartsWith("Language:") || trimmed.StartsWith("STYLE") || trimmed.StartsWith("NOTE")) continue;
                    var parsedCue = ParseSingleCue(trimmed);
                    if (parsedCue != null) cues.Add(parsedCue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse VTT file: {path}", vttPath);
            }
            return cues.OrderBy(c => c.StartTime).ToList();
        }

        private SubtitleCue? ParseSingleCue(string cueBlock)
        {
            try
            {
                var lines = cueBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                string? timeLine = null;
                int timeLineIndex = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (Regex.IsMatch(line, @"[\d:.]+\s*-->\s*[\d:.]+"))
                    {
                        timeLine = line;
                        timeLineIndex = i;
                        break;
                    }
                }
                if (timeLine == null) return null;
                var timeMatch = Regex.Match(timeLine, @"^([\d:.,]+)\s*-->\s*([\d:.,]+)(.*)");
                if (!timeMatch.Success) return null;
                var start = ParseTimeToSeconds(timeMatch.Groups[1].Value);
                var end = ParseTimeToSeconds(timeMatch.Groups[2].Value);
                if (end <= start) return null;
                var attributes = timeMatch.Groups[3].Value.Trim();
                int linePercent = 90;
                var lineMatch = Regex.Match(attributes, @"line:(-?\d+(?:\.\d+)?%?)");
                if (lineMatch.Success && lineMatch.Groups[1].Value.EndsWith("%") && int.TryParse(lineMatch.Groups[1].Value.TrimEnd('%'), out int parsedPercent))
                {
                    linePercent = Math.Max(0, Math.Min(100, parsedPercent));
                }
                string position = "";
                var alignMatch = Regex.Match(attributes, @"align:(start|center|end|left|middle|right)");
                if (alignMatch.Success) position = alignMatch.Groups[1].Value;
                string size = "";
                var sizeMatch = Regex.Match(attributes, @"size:(\d+(?:\.\d+)?%?)");
                if (sizeMatch.Success) size = sizeMatch.Groups[1].Value;
                var textLines = lines.Skip(timeLineIndex + 1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (!textLines.Any()) return null;
                var rawText = string.Join(" ", textLines).Trim();
                if (string.IsNullOrWhiteSpace(rawText)) return null;
                var htmlText = VttParser_Enhanced(rawText);
                if (!string.IsNullOrWhiteSpace(htmlText))
                {
                    return new SubtitleCue { StartTime = start, EndTime = end, Text = htmlText, LinePercent = linePercent, Position = position, Size = size };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse individual cue block");
            }
            return null;
        }

        private void ParseVttStyleBlock(string fileContent)
        {
            _vttStyles.Clear();
            var styleMatches = Regex.Matches(fileContent, @"STYLE\s*([\s\S]*?)(?=\r?\n\r?\n|$)", RegexOptions.IgnoreCase);
            foreach (Match styleMatch in styleMatches) ParseCssStyles(styleMatch.Groups[1].Value);
            var noteMatches = Regex.Matches(fileContent, @"NOTE.*?Style:\s*([\s\S]*?)(?=\r?\n\r?\n|$)", RegexOptions.IgnoreCase);
            foreach (Match noteMatch in noteMatches) ParseCssStyles(noteMatch.Groups[1].Value);
        }

        private void ParseCssStyles(string styleBlock)
        {
            try
            {
                var cssRules = Regex.Matches(styleBlock, @"([^{]+)\s*\{\s*([^}]+)\s*\}", RegexOptions.IgnoreCase);
                foreach (Match rule in cssRules)
                {
                    var selector = rule.Groups[1].Value.Trim();
                    var properties = rule.Groups[2].Value.Trim();
                    var colorMatch = Regex.Match(properties, @"color:\s*([^;]+)", RegexOptions.IgnoreCase);
                    if (colorMatch.Success)
                    {
                        var hexColor = ParseColorValue(colorMatch.Groups[1].Value.Trim());
                        if (!string.IsNullOrEmpty(hexColor))
                        {
                            var classMatches = Regex.Matches(selector, @"\.([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
                            foreach (Match classMatch in classMatches)
                            {
                                var className = classMatch.Groups[1].Value.ToLowerInvariant();
                                _vttStyles[className] = hexColor;
                                _vttStyles["c." + className] = hexColor;
                            }
                            var cueMatch = Regex.Match(selector, @"::cue\s*\(\s*\.?([a-zA-Z0-9_-]+)\s*\)", RegexOptions.IgnoreCase);
                            if (cueMatch.Success)
                            {
                                var className = cueMatch.Groups[1].Value.ToLowerInvariant();
                                _vttStyles[className] = hexColor;
                                _vttStyles["c." + className] = hexColor;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error parsing CSS styles from VTT"); }
        }

        private string ParseColorValue(string colorValue)
        {
            colorValue = colorValue.Trim().ToLowerInvariant();
            if (colorValue.StartsWith("#")) return colorValue;
            var rgbMatch = Regex.Match(colorValue, @"rgba?\s*\((\d+),(\d+),(\d+)");
            if (rgbMatch.Success)
            {
                int r = int.Parse(rgbMatch.Groups[1].Value);
                int g = int.Parse(rgbMatch.Groups[2].Value);
                int b = int.Parse(rgbMatch.Groups[3].Value);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            if (_predefinedColors.TryGetValue(colorValue, out var namedColor)) return namedColor;
            return "";
        }

        private string VttParser_Enhanced(string vttText)
        {
            if (string.IsNullOrWhiteSpace(vttText)) return string.Empty;
            string cleanedText = vttText;
            cleanedText = Regex.Replace(cleanedText, @"\s+", " ").Replace("&nbsp;", " ").Replace("&#160;", " ").Replace("\u00A0", " ");
            cleanedText = cleanedText.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
            var result = new StringBuilder();
            var tagStack = new Stack<string>();
            int i = 0;
            while (i < cleanedText.Length)
            {
                if (cleanedText[i] == '<')
                {
                    int tagEnd = cleanedText.IndexOf('>', i);
                    if (tagEnd == -1) { result.Append(cleanedText[i++]); continue; }
                    string tagContent = cleanedText.Substring(i + 1, tagEnd - i - 1).Trim();
                    i = tagEnd + 1;
                    if (Regex.IsMatch(tagContent, @"^[\d:.]+$")) continue;
                    if (tagContent.StartsWith("/")) { if (tagStack.Count > 0) result.Append(tagStack.Pop()); }
                    else ProcessOpenTag(tagContent, result, tagStack);
                }
                else result.Append(cleanedText[i++]);
            }
            while (tagStack.Count > 0) result.Append(tagStack.Pop());
            return result.ToString().Trim();
        }

        private void ProcessOpenTag(string tagContent, StringBuilder result, Stack<string> tagStack)
        {
            var parts = tagContent.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var tagName = parts[0].ToLowerInvariant();
            string htmlOpenTag = "", htmlCloseTag = "";
            switch (tagName)
            {
                case "b": htmlOpenTag = "<b>"; htmlCloseTag = "</b>"; break;
                case "i": htmlOpenTag = "<i>"; htmlCloseTag = "</i>"; break;
                case "u": htmlOpenTag = "<u>"; htmlCloseTag = "</u>"; break;
                case "s": htmlOpenTag = "<s>"; htmlCloseTag = "</s>"; break;
                case "v":
                    var voiceMatch = Regex.Match(tagContent, @"v\s+([^>]+)");
                    if (voiceMatch.Success) result.Append($"<i>[{voiceMatch.Groups[1].Value.Trim()}]:</i> ");
                    break;
                case "rt": return;
                default:
                    string colorHex = "";
                    if (_vttStyles.TryGetValue(tagName, out var styleColor) || (tagName.StartsWith("c.") && _vttStyles.TryGetValue(tagName, out styleColor)) || _predefinedColors.TryGetValue(tagName, out styleColor))
                    {
                        colorHex = styleColor;
                    }
                    if (!string.IsNullOrEmpty(colorHex))
                    {
                        htmlOpenTag = $"<font color='{colorHex}'>";
                        htmlCloseTag = "</font>";
                    }
                    break;
            }
            if (!string.IsNullOrEmpty(htmlOpenTag))
            {
                result.Append(htmlOpenTag);
                tagStack.Push(htmlCloseTag);
            }
        }

        private static double ParseTimeToSeconds(string timeStr)
        {
            try
            {
                timeStr = timeStr.Trim().Replace(',', '.');
                var parts = timeStr.Split('.');
                var mainPart = parts[0];
                double.TryParse(parts.Length > 1 ? parts[1].PadRight(3, '0').Substring(0, 3) : "0", out var ms);
                var timeParts = mainPart.Split(':');
                double totalSeconds = 0;
                if (timeParts.Length == 3 && int.TryParse(timeParts[0], out int h) && int.TryParse(timeParts[1], out int m) && int.TryParse(timeParts[2], out int s))
                    totalSeconds = h * 3600 + m * 60 + s;
                else if (timeParts.Length == 2 && int.TryParse(timeParts[0], out m) && int.TryParse(timeParts[1], out s))
                    totalSeconds = m * 60 + s;
                else if (timeParts.Length == 1 && double.TryParse(timeParts[0], out var sec))
                    totalSeconds = sec;
                return totalSeconds + (ms / 1000.0);
            }
            catch { return 0.0; }
        }

        public void StartRealtimeSubtitles(List<SubtitleCue> cues)
        {
            if (_plugin == null || cues == null || cues.Count == 0) return;
            StopRealtimeSubtitles();
            _currentSubtitles = cues;
            _audioProgressSec = 0.0;
            _isAudioPlaying = true;
            _subtitleTimer = _plugin.AddTimer(0.05f, UpdateSubtitles, TimerFlags.REPEAT);
            _logger.LogInformation("[Subtitles] Started with {Count} cues. Correction Factor: {factor}", _currentSubtitles.Count, _driftCorrectionFactor);
        }

        public void StopRealtimeSubtitles()
        {
            _subtitleTimer?.Kill();
            _subtitleTimer = null;
            _currentSubtitles.Clear();
            _vttStyles.Clear();
            _lastShownCues.Clear();
            _audioProgressSec = 0.0;
            _isAudioPlaying = false;
            
            Server.NextFrame(() =>
            {
                foreach (var p in Utilities.GetPlayers()) 
                    if(p != null && p.IsValid) 
                        try { p.PrintToCenterHtml(""); } catch { } 
            });
        }

        private void UpdateSubtitles()
        {
            Server.NextFrame(() =>
            {
                if (_plugin == null || _currentSubtitles.Count == 0 || !_isAudioPlaying) return;

                // [수정됨] 경과 시간 계산 시 하드코딩된 보정 계수를 곱합니다.
                var elapsedTime = Server.CurrentTime - _playbackStartTime;
                _audioProgressSec = elapsedTime * _driftCorrectionFactor;

                var futureTime = _audioProgressSec;
                var activeCues = _currentSubtitles.Where(c => c.StartTime <= futureTime && futureTime < c.EndTime).ToList();

                List<SubtitleCue> finalCuesToShow;
                if (activeCues.Any())
                {
                    finalCuesToShow = activeCues
                        .GroupBy(c => c.LinePercent)
                        .Select(group => group.OrderByDescending(c => c.StartTime).First())
                        .OrderBy(c => c.LinePercent)
                        .ToList();
                    _lastShownCues = finalCuesToShow;
                }
                else
                {
                    if (_lastShownCues.Any() && futureTime <= _lastShownCues.Max(c => c.EndTime) + LINGER_DURATION)
                    {
                        finalCuesToShow = _lastShownCues;
                    }
                    else
                    {
                        finalCuesToShow = new List<SubtitleCue>();
                        _lastShownCues.Clear();
                    }
                }
                
                string innerHtml = finalCuesToShow.Any() ? string.Join("<br>", finalCuesToShow.Select(c => c.Text)) : "";
                string newHtml = string.IsNullOrWhiteSpace(innerHtml) ? "" : $"<font size='5'>{innerHtml}</font>";

                foreach (var p in Utilities.GetPlayers())
                {
                    if (p != null && p.IsValid)
                    {
                        try 
                        { 
                            p.PrintToCenterHtml(newHtml); 
                        } 
                        catch (Exception ex) 
                        { 
                            _logger.LogWarning(ex, "Error printing subtitle to player HUD."); 
                        }
                    }
                }
            });
        }

        public void Dispose()
        {
            StopRealtimeSubtitles();
            try
            {
                if (_audioApiRegistered)
                {
                    if (_playListenerId >= 0 && _playHandler != null) Audio.UnregisterPlayListener(_playHandler);
                    if (_playStartListenerId >= 0 && _playStartHandler != null) Audio.UnregisterPlayStartListener(_playStartHandler);
                    if (_playEndListenerId >= 0 && _playEndHandler != null) Audio.UnregisterPlayEndListener(_playEndHandler);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Subtitles] Error unregistering audio listeners.");
            }
        }
    }
}