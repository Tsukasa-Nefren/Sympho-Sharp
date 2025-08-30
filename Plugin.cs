using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Sympho.Functions;
using Sympho.Models;

namespace Sympho
{
    [MinimumApiVersion(80)]
    public partial class Sympho : BasePlugin, IPluginConfig<Settings>
    {
        public override string ModuleName => "Sympho Audio Player";
        public override string ModuleVersion => "2.4.2 (Thread-Safe Fix)";
        public override string ModuleAuthor => "Oylsister & Gemini";

        private readonly ILogger<Sympho> _logger;
        private readonly AudioHandler _handler;
        private readonly Youtube _youtube;
        private readonly Subtitles _subs;
        private readonly CacheManager _cache;
        private readonly Event _event;
        
        public Settings Config { get; set; } = new();
        public FakeConVar<float> CVAR_Volume = new("css_sympho_volume", "Volume of Sympho sound", 1.0f, ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.1f, 1.0f));
        public static bool AllowPlaying = true;
        
        private readonly ConcurrentQueue<(string url, int start, CCSPlayerController? player, bool isAdmin, string? langCode)> _queue = new();
        private int _queueActive = 0;
        private CancellationTokenSource _cts = new();
        private readonly Audio.PlayEndHandler _onPlayEnd;
        
        private bool _isListingSubtitles = false;

        public Sympho(ILogger<Sympho> logger, AudioHandler handler, Youtube youtube, Subtitles subs, CacheManager cache) 
        { 
            _logger = logger;
            _handler = handler;
            _youtube = youtube;
            _subs = subs;
            _cache = cache;
            _event = new Event(this);
            
            _onPlayEnd = (slot) => 
            {
                if (Youtube.IsPlaying)
                {
                    Youtube.IsPlaying = false;
                    Server.NextFrame(() => _subs.StopRealtimeSubtitles());
                }
            };
        }
        
        public void OnConfigParsed(Settings config) { Config = config; }
        
        public override void Load(bool hotReload)
        {
            _handler.Initialize(this);
            _youtube.Initialize(this);
            _subs.Initialize(this);
            _cache.Initialize(this);
            
            _handler.SetSubtitles(_subs);
            
            RegisterEventHandler<EventPlayerChat>(_event.OnPlayerChat);
            
            Audio.RegisterPlayEndListener(_onPlayEnd);
            _cache.CleanupExpired();
        }

        public override void Unload(bool hotReload)
        {
            StopAllSound(null);
            Audio.UnregisterPlayEndListener(_onPlayEnd);
            _subs?.Dispose();
            _cache?.CleanupExpired();
            _cts.Cancel();
            _cts.Dispose();
            (_handler as IDisposable)?.Dispose();
        }

        [CommandHelper(1, "css_yt <video-url> [start-seconds]")]
        [ConsoleCommand("css_yt", "Play audio from YouTube")]
        public void YoutubeCommand(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null)
            {
                info.ReplyToCommand("This command can only be used by a player in-game to select subtitles.");
                return;
            }

            var argString = info.ArgString?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(argString)) 
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} Usage: {info.GetArg(0)} <url> [start]");
                return;
            }

            var parts = argString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var url = parts[0];
            int start = 0;
            if (parts.Length >= 2 && int.TryParse(parts[1], out var s)) start = Math.Max(0, s);
            
            _ = ShowSubtitleSelectionMenu(player, url, start);
        }
        
        public async Task ShowSubtitleSelectionMenu(CCSPlayerController player, string url, int start)
        {
            if (_isListingSubtitles)
            {
                player.PrintToChat($" {Localizer["Prefix"]} Another subtitle selection is already in progress. Please wait.");
                return;
            }

            _isListingSubtitles = true;
            try
            {
                player.PrintToChat($" {Localizer["Prefix"]} Fetching available subtitles...");
                
                // This part runs in the background.
                var subtitles = await _youtube.ListAvailableSubtitlesAsync(url);

                // FIX: Switch back to the main thread before interacting with the player's UI.
                Server.NextFrame(() =>
                {
                    if (!player.IsValid) return; // Always check if the player is still connected.

                    var menu = new ChatMenu("Choose a Subtitle");
                    
                    if (subtitles != null && subtitles.Count > 0)
                    {
                        foreach (var sub in subtitles)
                        {
                            menu.AddMenuOption(sub.LanguageName, (p, option) => {
                                EnqueueForPlayback(p, url, start, sub.LangCode);
                            });
                        }
                    }
                    else
                    {
                        player.PrintToChat($" {Localizer["Prefix"]} No manual subtitles found.");
                    }

                    menu.AddMenuOption("Play without Subtitles", (p, option) => {
                        EnqueueForPlayback(p, url, start, null);
                    });

                    MenuManager.OpenChatMenu(player, menu);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error showing subtitle selection menu.");
                // Also dispatch the error message to the main thread.
                Server.NextFrame(() =>
                {
                    if (player.IsValid)
                    {
                        player.PrintToChat($" {Localizer["Prefix"]} An error occurred while fetching subtitles.");
                    }
                });
            }
            finally
            {
                _isListingSubtitles = false;
            }
        }
        
        public void EnqueueForPlayback(CCSPlayerController? player, string url, int start, string? langCode)
        {
            Action<string> reply = msg => player?.PrintToChat(msg);

            if (!AllowPlaying) { reply($" {Localizer["Prefix"]} {Localizer["Audio.Disabled"]}"); return; }
            bool isAdmin = player == null || AdminManager.PlayerHasPermissions(player, "@css/kick");
            if (!Config.AllowYoutube && !(isAdmin && Config.BypassYoutubeAdmin)) { reply($" {Localizer["Prefix"]} {Localizer["Youtube.NotAllowed"]}"); return; }
            if (Config.EnableAntiSpam && AntiSpamData.GetCooldownLeft() > 0 && !isAdmin) { reply($" {Localizer["Prefix"]} {Localizer["Spam.Remaining", AntiSpamData.GetCooldownLeft().ToString("0.0")]}"); return; }
            if (Config.EnableAntiSpam && !isAdmin) AntiSpamData.SetPlayedCount(AntiSpamData.GetPlayedCount() + 1);
            if (Youtube.IsPlaying && !Config.QueueEnabled) { reply($" {Localizer["Prefix"]} {Localizer["Youtube.WaitForFinish"]}"); return; }
            if (Config.QueueEnabled && Config.MaxQueueLength > 0 && _queue.Count >= Config.MaxQueueLength) { reply($" {Localizer["Prefix"]} Queue is full."); return; }
            
            _queue.Enqueue((url, start, player, isAdmin, langCode));
            
            var queuePosition = _queue.Count;
            var maxQueue = Config.MaxQueueLength > 0 ? $"/{Config.MaxQueueLength}" : "";
            reply($" {Localizer["Prefix"]} Queued. Position: {queuePosition}{maxQueue}");

            if (Interlocked.CompareExchange(ref _queueActive, 1, 0) == 0) 
            {
                _ = ProcessQueueAsync();
            }
        }

        [RequiresPermissions("@css/kick")]
        [ConsoleCommand("css_skip", "Skip current audio (admin)")]
        public void Skip(CCSPlayerController? player, CommandInfo info)
        {
            if (!Youtube.IsPlaying) { info.ReplyToCommand("No audio is currently playing to skip."); return; }
            Functions.AudioHandler.StopAudio();
            Youtube.IsPlaying = false; 
            _cts.Cancel();
            
            var adminName = player?.PlayerName ?? "Console";
            Server.PrintToChatAll($" {Localizer["Prefix"]} {adminName} skipped the current audio.");
        }
        
        [RequiresPermissions("@css/kick")]
        [ConsoleCommand("css_stopall", "Stop all playing audios")]
        public void StopAllSound(CCSPlayerController? player, CommandInfo info) { StopAllSound(player); }
        
        public void StopAllSound(CCSPlayerController? player)
        {
            _queue.Clear();
            if (Youtube.IsPlaying)
            {
                Functions.AudioHandler.StopAudio();
                Youtube.IsPlaying = false;
                _cts.Cancel();

                var adminName = player?.PlayerName ?? "Console";
                Server.PrintToChatAll($" {Localizer["Prefix"]} {adminName} stopped all sounds and cleared the queue.");
            }
        }

        [ConsoleCommand("css_togglesympho", "Toggle Sympho")]
        public void ToggleSympho(CCSPlayerController? player, CommandInfo info)
        {
            AllowPlaying = !AllowPlaying;
            if (!AllowPlaying) { StopAllSound(null); }
            Server.PrintToChatAll($" {Localizer["Prefix"]} Sympho has been {(AllowPlaying ? "Enabled" : "Disabled")}.");
        }

        [RequiresPermissions("@css/kick")]
        [ConsoleCommand("css_reloadconfig", "Reload Sympho config")]
        public void ReloadConfig(CCSPlayerController? player, CommandInfo info) { LoadConfig(); Server.PrintToChatAll($" {Localizer["Prefix"]} Configuration reloaded."); }
        
        [ConsoleCommand("css_audio_delay", "Report audio delay manually (debug)")]
        public void ReportAudioDelay(CCSPlayerController? player, CommandInfo info)
        {
            if (string.IsNullOrEmpty(info.GetArg(1)))
            {
                info.ReplyToCommand("Usage: css_audio_delay <milliseconds>");
                return;
            }
            
            if (double.TryParse(info.GetArg(1), out double delayMs))
            {
                _handler.ProcessConsoleMessage($"[Audio] Max delay exceeded: {delayMs} milliseconds");
                info.ReplyToCommand($"Reported audio delay: {delayMs}ms");
            }
            else
            {
                info.ReplyToCommand("Invalid delay value. Usage: css_audio_delay <milliseconds>");
            }
        }
        
        private async Task ProcessQueueAsync()
        {
            try
            {
                while (_queue.TryDequeue(out var job))
                {
                    _cts = new CancellationTokenSource();
                    if (!AllowPlaying) { continue; }
                    int endLimit = job.isAdmin || Config.MaxAudioLength <= 0 ? 0 : job.start + Config.MaxAudioLength;
                    try
                    {
                        Youtube.IsPlaying = true;
                        var result = await _youtube.ProceedYoutubeVideo(job.url, job.start, endLimit, job.langCode);

                        if(!result.Success)
                        {
                            var failMessage = result.FailureReason switch
                            {
                                Youtube.YoutubeResult.Failure.DownloadFailed => "Failed to download audio. The video may be private or unavailable.",
                                Youtube.YoutubeResult.Failure.ProcessTimeout => "Processing timed out. Please try again.",
                                _ => "An unknown error occurred while processing the video."
                            };
                            
                            if (job.player != null && job.player.IsValid)
                            {
                                job.player.PrintToChat($" {Localizer["Prefix"]} {failMessage}");
                            }
                            else
                            {
                                _logger.LogWarning(failMessage + " URL: {url}", job.url);
                            }

                            Youtube.IsPlaying = false; 
                            continue;
                        }
                        
                        while (Youtube.IsPlaying && AllowPlaying && !_cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, _cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { /* Skipped */ }
                    finally
                    {
                        if (Youtube.IsPlaying) Youtube.IsPlaying = false;
                        Server.NextFrame(() => _subs?.StopRealtimeSubtitles());
                        if (Config.EnableAntiSpam && !job.isAdmin)
                        {
                            if (AntiSpamData.GetPlayedCount() >= Config.MaxSpamPerInterval)
                            {
                                var cooldownTime = Server.CurrentTime + Config.AntiSpamCooldown;
                                AntiSpamData.SetCooldown(cooldownTime);
                                AntiSpamData.SetPlayedCount(0);
                                _logger.LogInformation("[AntiSpam] Cooldown triggered for non-admins until {time}", cooldownTime);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Error in ProcessQueueAsync"); }
            finally { Interlocked.Exchange(ref _queueActive, 0); }
        }
        
        void LoadConfig()
        {
            try
            {
                var path = Path.Combine(ModuleDirectory, "config", "settings.json");
                if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, JsonConvert.SerializeObject(new Settings(), Formatting.Indented));
                var json = File.ReadAllText(path);
                var newConfig = JsonConvert.DeserializeObject<Settings>(json);
                if (newConfig != null) { Config = newConfig; OnConfigParsed(Config); }
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load settings.json"); Config = new Settings(); OnConfigParsed(Config); }
        }
    }
}