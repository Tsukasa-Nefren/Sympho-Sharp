# Sympho

Sympho is a ModSharp module for Counter-Strike 2 servers. It downloads YouTube audio with `yt-dlp` and plays it through the `Audio` module voice channel.

## Requirements

- ModSharp / CS2 server environment
- .NET 10 SDK for building from source
- `Audio` module installed first
- `Sharp.Modules.AdminManager`, `Sharp.Modules.LocalizerManager`, and `Sharp.Modules.MenuManager`
- `ffmpeg` from the installed `Audio` module, or `ffmpeg` available on `PATH`
- `yt-dlp` next to `Sympho.dll` in the Sympho module directory

## Install

1. Install the `Audio` module release first.
2. Extract the Sympho release zip into the game `sharp` directory.
3. Restart the server or reload ModSharp modules.

Expected layout:

```text
sharp/
|-- locales/
|   `-- Sympho.json
|-- modules/
|   |-- Audio/
|   |   |-- Audio.dll
|   |   |-- ffmpeg.exe
|   |   `-- opus.dll
|   `-- Sympho/
|       |-- Sympho.dll
|       |-- Sympho.deps.json
|       |-- Ptr.Shared.dll
|       `-- yt-dlp.exe
`-- shared/
    `-- Audio.Shared/
        `-- Audio.Shared.dll
```

On first load, Sympho creates:

```text
sharp/modules/Sympho/config/settings.json
sharp/modules/Sympho/sympho_tmp/
```

## Commands

| Command | Access | Description |
| --- | --- | --- |
| `!yt <url> [start seconds] [subtitle language\|none]` | everyone | Queue a YouTube track. |
| `!skip` | admin | Skip the current track. |
| `!stopall` | admin | Stop playback and clear the queue. |
| `!togglesympho` | admin | Enable or disable Sympho playback. |
| `!reloadconfig` | admin | Reload `settings.json`. |

Admin commands require the `admin:kick` permission.

## Settings

`settings.json` is created in `sharp/modules/Sympho/config/`.

| Setting | Default | Description |
| --- | ---: | --- |
| `AllowYoutube` | `true` | Allows `!yt` playback. |
| `BypassYoutubeAdmin` | `true` | Lets admins bypass `AllowYoutube = false`. |
| `MaxAudioLength` | `300` | Maximum non-admin playback length in seconds. `0` disables the limit. |
| `EnableAntiSpam` | `true` | Enables cooldown control for non-admin users. |
| `MaxSpamPerInterval` | `5` | Number of non-admin plays before cooldown. |
| `AntiSpamCooldown` | `60.0` | Cooldown duration in seconds. |
| `TmpDir` | `sympho_tmp` | Cache directory under the Sympho module directory. |
| `TmpMaxAgeMinutes` | `120` | Maximum cache age. |
| `CacheMaxCount` | `50` | Maximum cached file count. `0` disables count cleanup. |
| `CacheReuseEnabled` | `true` | Reuses cached downloads while valid. |
| `QueueEnabled` | `true` | Queues requests while another track is playing. |
| `MaxQueueLength` | `10` | Maximum queue length. `0` disables the queue length limit. |

The `ms_sympho_volume` ConVar controls Sympho output volume from `0.1` to `1.0`.

## Build

```powershell
git clone --recurse-submodules https://github.com/Tsukasa-Nefren/Sympho-Sharp.git
cd Sympho-Sharp
git submodule update --init --recursive
dotnet build Sympho.sln -c Release
```

Windows builds download `yt-dlp.exe` automatically. Release artifacts are built by GitHub Actions when a `v*` tag is pushed.
