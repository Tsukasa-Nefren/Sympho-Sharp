using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Ptr.Shared.Extensions;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sympho.Functions;
using Sympho.Models;

namespace Sympho;

public sealed class Sympho : IModSharpModule
{
    private const string FallbackPrefix = "[Sympho]";
    public const string AudioChannel = "sympho";

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISharedSystem _sharedSystem;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IConVarManager _conVars;
    private readonly ISharpModuleManager _modules;
    private readonly ILogger<Sympho> _logger;
    private readonly AudioHandler _handler;
    private readonly Youtube _youtube;
    private readonly Subtitles _subs;
    private readonly CacheManager _cache;
    private readonly string _moduleDirectory;
    private readonly string _sharpPath;
    private readonly ConcurrentQueue<(string url, int start, IGameClient? player, bool isAdmin, string? langCode)> _queue = new();
    private readonly ConcurrentDictionary<int, (ulong SteamId, ushort UserId, float HiddenUntil)> _subtitleHudHiddenUntil = new();
    private CancellationTokenSource _cts = new();
    private IModSharpModuleInterface<IAudio>? _audioApi;
    private IModSharpModuleInterface<IAdminManager>? _adminManager;
    private IModSharpModuleInterface<ILocalizerManager>? _localizerManager;
    private IModSharpModuleInterface<IMenuManager>? _menuManager;
    private IConVar? _volumeCvar;
    private Guid _playStartListenerId = Guid.Empty;
    private Guid _playEndListenerId = Guid.Empty;
    private int _queueActive;
    private int _subtitleLookupActive;
    private List<SubtitleCue>? _pendingSubtitles;
    private const float SubtitleHudMenuResumeDelay = 1.0f;

    public Settings Config { get; private set; } = new();
    public static bool AllowPlaying { get; private set; } = true;

    public Sympho(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _modSharp = sharedSystem.GetModSharp();
        _clients = sharedSystem.GetClientManager();
        _conVars = sharedSystem.GetConVarManager();
        _modules = sharedSystem.GetSharpModuleManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<Sympho>();
        _moduleDirectory = Directory.Exists(dllPath)
            ? dllPath
            : Path.GetDirectoryName(dllPath) ?? AppContext.BaseDirectory;
        _sharpPath = Path.GetFullPath(sharpPath);

        _handler = new AudioHandler(_logger);
        _cache = new CacheManager(_logger);
        _subs = new Subtitles(_cache, _logger);
        _youtube = new Youtube(_handler, _subs, _cache, _logger);
    }

    public bool Init()
    {
        LoadConfig();
        _volumeCvar = _conVars.CreateConVar("ms_sympho_volume", 1.0f, 0.1f, 1.0f, "Volume of Sympho audio");

        _handler.Initialize(this);
        _youtube.Initialize(this);
        _subs.Initialize(this);
        _cache.Initialize(this);

        _clients.InstallCommandCallback("yt", YoutubeCommand);
        _clients.InstallCommandCallback("skip", SkipCommand);
        _clients.InstallCommandCallback("stopall", StopAllCommand);
        _clients.InstallCommandCallback("togglesympho", ToggleCommand);
        _clients.InstallCommandCallback("reloadconfig", ReloadConfigCommand);

        _cache.CleanupExpired();
        ResolveAudioApi(logFailure: true);
        ResolveAdminManager();
        ResolveLocalizer();
        ResolveMenuManager();
        return true;
    }

    public void Shutdown()
    {
        StopAllSound(null, announce: false);
        _clients.RemoveCommandCallback("yt", YoutubeCommand);
        _clients.RemoveCommandCallback("skip", SkipCommand);
        _clients.RemoveCommandCallback("stopall", StopAllCommand);
        _clients.RemoveCommandCallback("togglesympho", ToggleCommand);
        _clients.RemoveCommandCallback("reloadconfig", ReloadConfigCommand);
        UnregisterAudioEndListener();
        _subs.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _cache.CleanupExpired();
    }

    public void OnAllModulesLoaded()
    {
        ResolveAudioApi();
        ResolveAdminManager();
        ResolveLocalizer();
        ResolveMenuManager();
    }

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("Audio", StringComparison.OrdinalIgnoreCase))
        {
            ResolveAudioApi();
        }
        else if (name.Equals("Sharp.Modules.AdminManager", StringComparison.OrdinalIgnoreCase))
        {
            ResolveAdminManager();
        }
        else if (name.Equals("Sharp.Modules.LocalizerManager", StringComparison.OrdinalIgnoreCase))
        {
            ResolveLocalizer();
        }
        else if (name.Equals("Sharp.Modules.MenuManager", StringComparison.OrdinalIgnoreCase))
        {
            ResolveMenuManager();
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals("Audio", StringComparison.OrdinalIgnoreCase))
        {
            UnregisterAudioEndListener();
            _audioApi = null;
        }
        else if (name.Equals("Sharp.Modules.AdminManager", StringComparison.OrdinalIgnoreCase))
        {
            _adminManager = null;
        }
        else if (name.Equals("Sharp.Modules.LocalizerManager", StringComparison.OrdinalIgnoreCase))
        {
            _localizerManager = null;
        }
        else if (name.Equals("Sharp.Modules.MenuManager", StringComparison.OrdinalIgnoreCase))
        {
            _menuManager = null;
        }
    }

    private ECommandAction YoutubeCommand(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            ReplyKey(client, "sympho.usage.yt");
            return ECommandAction.Stopped;
        }

        var url = command.GetArg(1);
        var start = command.ArgCount >= 2 && int.TryParse(command.GetArg(2), out var parsed)
            ? Math.Max(0, parsed)
            : 0;

        if (command.ArgCount >= 3)
        {
            var lang = command.GetArg(3);
            EnqueueForPlayback(client, url, start, IsNoSubtitleValue(lang) ? null : lang);
        }
        else
        {
            _ = ShowSubtitleSelectionMenu(client, url, start);
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction SkipCommand(IGameClient client, StringCommand command)
    {
        if (!IsAdmin(client))
        {
            ReplyKey(client, "sympho.permission.skip");
            return ECommandAction.Stopped;
        }

        if (!Youtube.IsPlaying)
        {
            ReplyKey(client, "sympho.playback.none");
            return ECommandAction.Stopped;
        }

        _handler.StopAudio();
        _subs.StopRealtimeSubtitles();
        _pendingSubtitles = null;
        Youtube.IsPlaying = false;
        _cts.Cancel();
        PrintLocalizedToChatAll("sympho.playback.skipped_by", client.Name);
        return ECommandAction.Stopped;
    }

    private ECommandAction StopAllCommand(IGameClient client, StringCommand command)
    {
        if (!IsAdmin(client))
        {
            ReplyKey(client, "sympho.permission.stop");
            return ECommandAction.Stopped;
        }

        StopAllSound(client);
        return ECommandAction.Stopped;
    }

    private ECommandAction ToggleCommand(IGameClient client, StringCommand command)
    {
        if (!IsAdmin(client))
        {
            ReplyKey(client, "sympho.permission.toggle");
            return ECommandAction.Stopped;
        }

        AllowPlaying = !AllowPlaying;
        if (!AllowPlaying)
        {
            StopAllSound(null, announce: false);
        }

        PrintLocalizedToChatAll(AllowPlaying ? "sympho.toggle.enabled" : "sympho.toggle.disabled");
        return ECommandAction.Stopped;
    }

    private ECommandAction ReloadConfigCommand(IGameClient client, StringCommand command)
    {
        if (!IsAdmin(client))
        {
            ReplyKey(client, "sympho.permission.reload");
            return ECommandAction.Stopped;
        }

        LoadConfig();
        _cache.Initialize(this);
        _subs.Initialize(this);
        PrintLocalizedToChatAll("sympho.config.reloaded");
        return ECommandAction.Stopped;
    }

    public async Task ShowSubtitleSelectionMenu(IGameClient player, string url, int start)
    {
        if (GetMenuManager() is null)
        {
            ReplyKey(player, "sympho.menu.unavailable_no_subtitles");
            EnqueueForPlayback(player, url, start, null);
            return;
        }

        if (Interlocked.CompareExchange(ref _subtitleLookupActive, 1, 0) != 0)
        {
            ReplyKey(player, "sympho.subtitles.lookup_busy");
            return;
        }

        try
        {
            ReplyKey(player, "sympho.subtitles.fetching");
            var subtitles = await _youtube.ListAvailableSubtitlesAsync(url) ?? [];

            RunOnGameThread(() =>
            {
                if (!player.IsValid)
                {
                    return;
                }

                var menuManager = GetMenuManager();
                if (menuManager is null)
                {
                    ReplyKey(player, "sympho.menu.unavailable_no_subtitles");
                    EnqueueForPlayback(player, url, start, null);
                    return;
                }

                var menu = new Menu();
                var selectedSubtitleOption = false;
                menu.SetTitle(Localize(player, "sympho.menu.subtitle.title"));
                menu.OnEnter = HideSubtitleHudForMenu;
                menu.OnExit = menuClient =>
                {
                    ResumeSubtitleHudAfterMenu(menuClient);
                    if (!selectedSubtitleOption)
                    {
                        _logger.LogDebug("Subtitle selection closed without selection for {Player}.", menuClient.Name);
                    }
                };

                if (subtitles.Count == 0)
                {
                    menu.AddDisabledItem(Localize(player, "sympho.menu.subtitle.none_found"));
                }
                else
                {
                    foreach (var subtitle in subtitles)
                    {
                        var selected = subtitle;
                        menu.AddItem(selected.LanguageName, controller =>
                        {
                            selectedSubtitleOption = true;
                            controller.Exit();
                            EnqueueForPlayback(controller.Client, url, start, selected.LangCode);
                        });
                    }
                }

                menu.AddItem(Localize(player, "sympho.menu.subtitle.play_without"), controller =>
                {
                    selectedSubtitleOption = true;
                    controller.Exit();
                    EnqueueForPlayback(controller.Client, url, start, null);
                });

                menuManager.DisplayMenu(player, menu);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing subtitle selection menu.");
            RunOnGameThread(() =>
            {
                ReplyKey(player, "sympho.subtitles.fetch_failed_no_subtitles");
                EnqueueForPlayback(player, url, start, null);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _subtitleLookupActive, 0);
        }
    }

    public void EnqueueForPlayback(IGameClient? player, string url, int start, string? langCode = null)
    {
        var isAdmin = player == null || IsAdmin(player);

        if (!AllowPlaying)
        {
            ReplyKey(player, "sympho.playback.disabled");
            return;
        }

        if (GetAudio() is null)
        {
            ReplyKey(player, "sympho.audio.module_unavailable");
            return;
        }

        if (!Config.AllowYoutube && !(isAdmin && Config.BypassYoutubeAdmin))
        {
            ReplyKey(player, "sympho.youtube.disabled");
            return;
        }

        if (Config.EnableAntiSpam && AntiSpamData.GetCooldownLeft(CurrentTime) > 0 && !isAdmin)
        {
            ReplyKey(player, "sympho.antispam.wait", AntiSpamData.GetCooldownLeft(CurrentTime).ToString("0.0", CultureInfo.InvariantCulture));
            return;
        }

        if (Config.EnableAntiSpam && !isAdmin)
        {
            AntiSpamData.SetPlayedCount(AntiSpamData.GetPlayedCount() + 1);
        }

        if (Youtube.IsPlaying && !Config.QueueEnabled)
        {
            ReplyKey(player, "sympho.playback.wait_current");
            return;
        }

        if (Config.QueueEnabled && Config.MaxQueueLength > 0 && _queue.Count >= Config.MaxQueueLength)
        {
            ReplyKey(player, "sympho.queue.full");
            return;
        }

        _queue.Enqueue((url, start, player, isAdmin, langCode));
        ReplyKey(player, "sympho.queue.position", $"{_queue.Count}{(Config.MaxQueueLength > 0 ? $"/{Config.MaxQueueLength}" : "")}");

        if (Interlocked.CompareExchange(ref _queueActive, 1, 0) == 0)
        {
            _ = ProcessQueueAsync();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (_queue.TryDequeue(out var job))
            {
                _cts = new CancellationTokenSource();
                if (!AllowPlaying)
                {
                    continue;
                }

                var maxDuration = job.isAdmin || Config.MaxAudioLength <= 0 ? 0 : Config.MaxAudioLength;
                try
                {
                    Youtube.IsPlaying = true;
                    var result = await _youtube.ProceedYoutubeVideo(job.url, job.start, maxDuration, job.langCode);

                    if (!result.Success)
                    {
                        var failMessage = result.FailureReason switch
                        {
                            Youtube.YoutubeResult.Failure.DownloadFailed => "sympho.error.download_failed",
                            Youtube.YoutubeResult.Failure.ProcessTimeout => "sympho.error.processing_timeout",
                            _ => "sympho.error.unknown_processing"
                        };

                        RunOnGameThread(() => ReplyKey(job.player, failMessage));
                        Youtube.IsPlaying = false;
                        continue;
                    }

                    while (Youtube.IsPlaying && AllowPlaying && !_cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(500, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (Youtube.IsPlaying)
                    {
                        Youtube.IsPlaying = false;
                    }

                    RunOnGameThread(_subs.StopRealtimeSubtitles);

                    if (Config.EnableAntiSpam && !job.isAdmin && AntiSpamData.GetPlayedCount() >= Config.MaxSpamPerInterval)
                    {
                        AntiSpamData.SetCooldown(CurrentTime + Config.AntiSpamCooldown);
                        AntiSpamData.SetPlayedCount(0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Sympho queue");
        }
        finally
        {
            Interlocked.Exchange(ref _queueActive, 0);
            if (!_queue.IsEmpty && Interlocked.CompareExchange(ref _queueActive, 1, 0) == 0)
            {
                _ = ProcessQueueAsync();
            }
        }
    }

    public void StopAllSound(IGameClient? player, bool announce = true)
    {
        _queue.Clear();
        _handler.StopAudio();
        _subs.StopRealtimeSubtitles();
        _pendingSubtitles = null;
        Youtube.IsPlaying = false;
        _cts.Cancel();

        if (announce)
        {
            PrintStoppedAll(player);
        }
    }

    public IAudio? GetAudio()
    {
        ResolveAudioApi();
        return _audioApi?.Instance;
    }

    public double GetAudioPlaybackTime()
    {
        ResolveAudioApi();
        return _audioApi?.Instance?.GetGlobalPlaybackTime(AudioChannel) ?? -1;
    }

    public void RunOnGameThread(Action action)
        => _modSharp.InvokeAction(action);

    public Guid StartRepeatingTimer(Action action, double interval)
        => _modSharp.PushTimer(action, interval, GameTimerFlags.Repeatable);

    public void StopTimer(Guid timer)
        => _modSharp.StopTimer(timer);

    public void PrintToChatAll(string message)
        => _modSharp.PrintToChatAll(message);

    public void PrintLocalizedToChatAll(string key, params object?[] args)
    {
        var sent = false;
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!client.IsValid)
            {
                continue;
            }

            client.PrintToChat(Message(client, key, args));
            sent = true;
        }

        if (!sent)
        {
            _modSharp.LogMessage(Message(null, key, args));
        }
    }

    public void PrintYoutubeNowPlaying(string title, string duration, string uploader)
        => PrintLocalizedToChatAll("sympho.youtube.now_playing", title, duration, uploader);

    public void PrintCenterHtmlToAll(string message, int duration = 1)
        => _clients.PrintCenterHtmlToAll(message, duration);

    public void PrintSubtitleCenterHtml(string message, int duration = 1)
    {
        var recipients = _clients.GetGameClientList(inGame: true);
        var now = CurrentTime;

        for (var i = recipients.Count - 1; i >= 0; i--)
        {
            var client = recipients[i];
            if (!client.IsValid || IsSubtitleHudHidden(client, now))
            {
                recipients.RemoveAt(i);
            }
        }

        if (recipients.Count == 0)
        {
            return;
        }

        _clients.PrintCenterHtmlToClients(recipients, message, duration);
    }

    private void HideSubtitleHudForMenu(IGameClient client)
    {
        if (!client.IsValid)
        {
            return;
        }

        _subtitleHudHiddenUntil[client.Slot.AsPrimitive()] = (client.SteamId.AsPrimitive(), client.UserId.AsPrimitive(), float.PositiveInfinity);
    }

    private void ResumeSubtitleHudAfterMenu(IGameClient client)
    {
        if (!client.IsValid)
        {
            return;
        }

        _subtitleHudHiddenUntil[client.Slot.AsPrimitive()] = (client.SteamId.AsPrimitive(), client.UserId.AsPrimitive(), CurrentTime + SubtitleHudMenuResumeDelay);
    }

    private bool IsSubtitleHudHidden(IGameClient client, float now)
    {
        var slot = client.Slot.AsPrimitive();
        if (!_subtitleHudHiddenUntil.TryGetValue(slot, out var state))
        {
            return false;
        }

        if (state.SteamId != client.SteamId.AsPrimitive() || state.UserId != client.UserId.AsPrimitive())
        {
            _subtitleHudHiddenUntil.TryRemove(slot, out _);
            return false;
        }

        if (now < state.HiddenUntil)
        {
            return true;
        }

        _subtitleHudHiddenUntil.TryRemove(slot, out _);
        return false;
    }

    public void PrepareSubtitles(List<SubtitleCue> cues)
        => _pendingSubtitles = cues.Count == 0 ? null : cues;

    private void ReplyKey(IGameClient? client, string key, params object?[] args)
        => Reply(client, Message(client, key, args));

    public void Reply(IGameClient? client, string message)
    {
        if (client is null || !client.IsValid)
        {
            _modSharp.LogMessage(message);
            return;
        }

        client.PrintToChat(message);
    }

    private void PrintStoppedAll(IGameClient? player)
    {
        var sent = false;
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!client.IsValid)
            {
                continue;
            }

            client.PrintToChat(Message(client, "sympho.audio.stopped_by", player?.Name ?? Localize(client, "sympho.console")));
            sent = true;
        }

        if (!sent)
        {
            _modSharp.LogMessage(Message(null, "sympho.audio.stopped_by", player?.Name ?? Localize(null, "sympho.console")));
        }
    }

    private string Message(IGameClient? client, string key, params object?[] args)
        => $"{FallbackPrefix} {Localize(client, key, args)}";

    private string Localize(IGameClient? client, string key, params object?[] args)
    {
        if (client is not null && client.IsValid)
        {
            ResolveLocalizer();
            try
            {
                var locale = _localizerManager?.Instance?.For(client);
                if (locale is not null && locale.TryText(key, out var localized, args.AsSpan()))
                {
                    return localized;
                }
            }
            catch
            {
            }
        }

        return key;
    }

    public float Volume
        => _volumeCvar?.GetFloat() ?? 1.0f;

    public string ModuleDirectory
        => _moduleDirectory;

    public float CurrentTime
        => _modSharp.GetGlobals().CurTime;

    public string Prefix
        => FallbackPrefix;

    private void ResolveAudioApi(bool logFailure = false)
    {
        if (_audioApi?.Instance is not null)
        {
            return;
        }

        _audioApi = _modules.GetOptionalSharpModuleInterface<IAudio>(IAudio.Identity);
        RegisterAudioEndListener();
        if (_audioApi?.Instance is null && logFailure)
        {
            _logger.LogWarning("Audio shared module is not available. Load Audio before Sympho.");
        }
    }

    private void RegisterAudioEndListener()
    {
        if (_playEndListenerId != Guid.Empty || _audioApi?.Instance is null)
        {
            return;
        }

        _playStartListenerId = _audioApi.Instance.OnGlobalPlayStart(AudioChannel, OnAudioStarted);
        _playEndListenerId = _audioApi.Instance.OnGlobalPlayEnd(AudioChannel, OnAudioEnded);
    }

    private void UnregisterAudioEndListener()
    {
        if (_playStartListenerId == Guid.Empty && _playEndListenerId == Guid.Empty)
        {
            return;
        }

        try
        {
            if (_playStartListenerId != Guid.Empty)
            {
                _audioApi?.Instance?.RemoveListener(_playStartListenerId);
            }

            if (_playEndListenerId != Guid.Empty)
            {
                _audioApi?.Instance?.RemoveListener(_playEndListenerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister audio listeners.");
        }
        finally
        {
            _playStartListenerId = Guid.Empty;
            _playEndListenerId = Guid.Empty;
        }
    }

    private void OnAudioStarted()
    {
        RunOnGameThread(() =>
        {
            if (_pendingSubtitles is { Count: > 0 } cues)
            {
                _pendingSubtitles = null;
                _subs.StartRealtimeSubtitles(cues);
            }
        });
    }

    private void OnAudioEnded()
    {
        RunOnGameThread(() =>
        {
            Youtube.IsPlaying = false;
            _pendingSubtitles = null;
            _subs.StopRealtimeSubtitles();
        });
    }

    private bool IsAdmin(IGameClient client)
    {
        ResolveAdminManager();
        var admin = _adminManager?.Instance?.GetAdmin(client.SteamId);
        return admin?.HasPermission("admin:kick") == true;
    }

    private void ResolveAdminManager()
    {
        if (_adminManager?.Instance is not null)
        {
            return;
        }

        _adminManager = _modules.GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
    }

    private void ResolveLocalizer()
    {
        if (_localizerManager?.Instance is not null)
        {
            return;
        }

        _localizerManager = _modules.GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity);
        try
        {
            _localizerManager?.Instance?.LoadLocaleFile("Sympho");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Sympho locale file.");
        }
    }

    private IMenuManager? GetMenuManager()
    {
        ResolveMenuManager();
        return _menuManager?.Instance;
    }

    private void ResolveMenuManager()
    {
        if (_menuManager?.Instance is not null)
        {
            return;
        }

        _menuManager = _modules.GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity);
    }

    private static bool IsNoSubtitleValue(string value)
        => value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("-", StringComparison.OrdinalIgnoreCase);

    private void LoadConfig()
    {
        try
        {
            var configDir = Path.Combine(_sharpPath, "configs", "Sympho");
            Directory.CreateDirectory(configDir);

            var path = Path.Combine(configDir, "settings.json");
            var legacyPath = Path.Combine(_moduleDirectory, "config", "settings.json");
            if (!File.Exists(path) && File.Exists(legacyPath))
            {
                File.Move(legacyPath, path);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(legacyPath));
            }

            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new Settings(), SettingsJsonOptions));
            }

            Config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), SettingsJsonOptions) ?? new Settings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.json");
            Config = new Settings();
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    public string DisplayName => "Sympho Audio Player";
    public string DisplayAuthor => "Oylsister & Gemini, ported to ModSharp";
}
