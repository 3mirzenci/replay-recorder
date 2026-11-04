# Replay Recorder

A lightweight instant-replay recorder for Windows, an alternative to SteelSeries Moments and NVIDIA ShadowPlay. Press **Alt+.** to save the last 60 seconds. Clips keep your game and mic audio on separate tracks, and you can export any clip for Discord in one click: it's guaranteed to fit under the upload limit.

## ⬇️ Download

**[Download Replay Recorder for Windows](https://github.com/3mirzenci/replay-recorder/releases/latest/download/ReplayRecorder-Setup.exe)**

To install:
1. Run the downloaded file.
2. Windows may say **"Windows protected your PC"**, because the app isn't code-signed (signing costs money every year). Click **More info**, then **Run anyway**.
3. Click through setup. It installs everything it needs, including ffmpeg and the mpv player.

You don't need a GitHub account. Updates arrive inside the app: when there's a new version, an **Update available** box shows up in the library and in the tray menu. One click installs it, and your clips and settings stay.

Two programs:

- **The recorder** (`ReplayRecorder.Recorder.exe`, tray icon) runs while you play. It keeps the last N seconds and saves a clip on a hotkey.
- **The library** (`ReplayRecorder.exe`) is a separate window for watching, trimming and exporting clips. Open it by double-clicking the tray icon. Closing it doesn't stop the recorder, and while it's closed it uses nothing.

Nothing is injected into games, so it's safe with anti-cheats like Vanguard and FACEIT. The app doesn't use accounts, telemetry or the cloud. The only network request it makes is checking this page for updates.

### The library

- **Clips are grouped by day**, newest first. While you scroll, the current day's header stays at the top.
- **Click a clip to open it.** Hover over a thumbnail and move the mouse across it to preview the clip.
- **The ☆ in the corner** stars a clip. Starred clips are listed under **Favorites** in the sidebar.
- **Selecting:**
  - Click a clip's corner circle, or a day header to select that whole day. Ctrl+click and Shift+click also work.
  - While anything is selected, a click selects or unselects a clip instead of opening it.
  - The bar at the bottom can open, show in folder, copy files, export everything for Discord (each clip keeps its saved trim and volumes) or delete.
- **Right-click** gives the same actions, plus Rename (F2) and favorites.
- **Large libraries stay light:** thumbnails are only loaded for clips on or near the screen. With 1000 clips the library opens in about 2 s and uses about 250 MB.

### Library shortcuts

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / Shift+→ | Back / forward 5 s |
| J / L | Back / forward 10 s |
| I / O | Set trim start / end at the playhead |
| P | Preview the trimmed range on a loop |
| Home / End | Jump to trim start / end |
| Del | Delete the clip for good (asks first) |
| Esc | Back to the library |

### Export for Discord

In the player, click **Export for Discord** or press **E**. Replay Recorder encodes the trimmed range, with your game/mic balance, into an MP4 that is guaranteed to be under the target size (default 19.5 MB, set by `DiscordTargetMB`). Then:

- **drag the tile** into a Discord chat (a real Windows file drag), or
- click **Copy file** and press **Ctrl+V** in Discord, or
- click **Open folder**.

Exports are temporary, so they don't fill up your drive: they're kept in `%LOCALAPPDATA%\Replay Recorder\exports` and deleted 5 minutes after they're made (enough time for Discord to upload), whether the library is still open or not. The original clip is never changed. To keep exports, pick your own Discord exports folder in Settings; files there are never deleted.

How it fits the file under the limit:
- **Bitrate:** video bitrate = target bits ÷ duration − 128 kbps audio − 2% container overhead.
- **Resolution and fps:** it picks the best that bitrate can carry well: 1440p60 at 14 Mbps or more, 1080p60 at 6 or more, 720p60 at 3 or more, 720p30 at 1.5 or more, then 480p30 and 360p30. It never goes above the source.
- **Encoding:** NVENC with GPU decode and scaling, and two-pass libx264 if NVENC isn't available.
- **Size check:** the real file size is checked. If it's over, it re-encodes at a proportionally lower bitrate. A file only gets its final name once it's under the target.
- **Fast path:** if a plain cut of the original already fits and the volumes are untouched, it copies without re-encoding (instant, lossless, and starts on the nearest keyframe).

Measured on a 1440p60 clip with a 19.5 MB target:

| Length | Result | Size |
|---|---|---|
| 3 s | stream copy, 1440p60 | 10.4 MB |
| 10 s | 1440p60, 15.2 Mbps | 19.0 MB |
| 30 s | 720p60, 5.0 Mbps | 19.5 MB |
| 62 s | 720p30, 2.3 Mbps | 18.9 MB |
| 3 min | 480p30, 0.7 Mbps | 18.8 MB |
| 6 min | 360p30, 0.3 Mbps (2 passes) | 18.4 MB |

You can also drag the blue handles on the timeline. Trim and game/mic volume are saved per clip in `%LOCALAPPDATA%\Replay Recorder\cache\edits.json`. The original clip file is never changed.

## How recording works

- Screen capture uses Windows Graphics Capture (or Desktop Duplication). The frames go straight into NVENC on the GPU. Nothing is injected into games, so it's safe with Vanguard and FACEIT.
- The replay buffer is 2-second segments on disk. Old segments are deleted automatically. Saving joins them without re-encoding, which takes about 0.2 s.
- **RAM buffer** (Settings → Instant replay): the segments are kept in memory instead, so nothing is written to the SSD until you save. It costs about 250 MB of RAM for a 60-second buffer. Full recordings still go to disk, because they can be long.
- **A short chime** plays when a clip is saved, and a lower one if saving failed, because the pop-up can't show over exclusive-fullscreen games. You can turn it off in Settings → General. The chime is quiet, but the recorder hears it, so it ends up faintly in the buffer after a save.
- Clips have three audio tracks:
  1. **Everything**: game, voice chat and mic mixed together. Every normal player (Media Player, Discord, browsers) plays this one.
  2. **Game + voice chat**: what you hear, Discord included.
  3. **Microphone**.

  Tracks 2 and 3 let the library rebalance game and mic later.
- Virtual audio devices (HyperX NGENUITY, SteelSeries Sonar, VB-Cable) are detected automatically. Replay Recorder records the real headset behind them, because loopback of a virtual device is silent.
- Clips are saved as `Videos\Replay Recorder\<Game>\<Game> yyyy-MM-dd HH-mm-ss.mp4`.

## Install details

Setup needs no admin rights and installs to `%LOCALAPPDATA%\Programs\Replay Recorder`. It:
- installs ffmpeg and mpv with winget if they're missing;
- adds Start menu shortcuts, plus optional ones for a desktop icon and starting with Windows;
- starts the recorder when it finishes.

Uninstalling stops both apps and removes the autostart entry. Your clips and settings stay.

Requirements: Windows 10 2004 or later, or Windows 11. An NVIDIA GPU for NVENC (Intel Quick Sync and x264 are fallbacks). The .NET runtime is bundled.

## Build from source

You need the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`), ffmpeg (`winget install Gyan.FFmpeg`) and mpv (`winget install shinchiro.mpv`).

```
dotnet build ReplayRecorder.sln -c Release
build\Release\ReplayRecorder.Recorder.exe
```
Both apps build into `build\<Configuration>\`.

To build the installer you also need Inno Setup 6 (`winget install JRSoftware.InnoSetup`):

```
powershell -ExecutionPolicy Bypass -File build.ps1               # dist\ReplayRecorder-Setup.exe, ~57 MB
powershell -ExecutionPolicy Bypass -File build.ps1 -BundleTools  # offline installer with ffmpeg + mpv inside
```

The offline installer contains ffmpeg, which is GPL-licensed. If you share it, include ffmpeg's licence (the build copies it to `ffmpeg\LICENSE.txt`) and point to ffmpeg's source at https://ffmpeg.org.

### Releasing a new version

```
powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "What changed"
```

This bumps the version in `Directory.Build.props` (1.0.3 becomes 1.0.4, or pass `-Version 1.1.0`) and builds the installer. Then it commits, tags and pushes, and creates the GitHub release. Every installed copy sees the update the next time it checks, at library start or every 6 hours in the tray.

The updater only downloads `ReplayRecorder-Setup.exe` from this repo's latest release, over HTTPS, from GitHub's own servers. The file must match the size and SHA-256 that GitHub lists for that file before it runs. Anyone who can publish releases here can ship code to every user, so keep two-factor authentication on the GitHub account.

## Using it

| Action | Default |
|---|---|
| Save the last 60 s | **Alt+.** |
| Start/stop a full recording | **Ctrl+Alt+F9** |
| Tray menu | right-click the icon (green = buffering, red = recording, grey = paused) |

From the command line, with the recorder already running:

- `ReplayRecorder.Recorder.exe --save`: save a clip (useful for Stream Deck or macros).
- `ReplayRecorder.Recorder.exe --toggle-record`: start or stop a full recording.
- `ReplayRecorder.Recorder.exe --diag-audio`: write a report on every audio device to `%LOCALAPPDATA%\Replay Recorder\logs\audio-diagnostics.txt`.

You can change the hotkeys in settings, e.g. `"SaveClipHotkey": "Ctrl+Shift+F10"`. A single punctuation key such as `.` `,` or `-` means the key that types that character on your current keyboard layout. Avoid Alt+F10 and Alt+F9, because the NVIDIA app overlay uses them. If a hotkey is taken, the tray shows a warning.

## Settings

Open them from the tray menu (*Settings…*) or the library sidebar. The settings window covers replay length, hotkeys, monitor, resolution and fps, quality, audio devices with live level meters, folders and the storage limit, the Discord size target, startup and game names. Saving applies the new settings right away. Only screen, quality, buffer storage or audio-device changes restart capture, which empties the replay buffer. Everything is stored in `%APPDATA%\Replay Recorder\settings.json`, which you can also edit by hand:

| Key | Meaning |
|---|---|
| `BufferSeconds` | Replay length, 15–300 |
| `Monitor` | `\\.\DISPLAY1` etc., or empty for the primary monitor (the log lists them) |
| `Capture` | `Wgc` (default) or `Dda` |
| `Height` | `0` = native (capped at `MaxNativeHeight`, default 1440), or `1080` / `720` |
| `Fps` | `60` or `30` |
| `Quality` | NVENC CQ: 19 = high, 21 = default, 25 = smaller files |
| `MaxBitrateMbps` | Bitrate cap (default 50) |
| `SystemAudioDeviceId` / `MicDeviceId` | Endpoint ids from `--diag-audio`, or empty for automatic |
| `SystemVolumeDb` / `MicVolumeDb` | Gain per track (-30 to +20 dB) |
| `MicEnabled` | `false` keeps a silent mic track |
| `ClipsFolder` / `BufferFolder` | Where clips and the rolling buffer go |
| `GameNames` | Map of exe name to folder name (e.g. `"cs2.exe": "CS2"`) |

## Measuring resource use

Use this to compare Replay Recorder with SteelSeries GG in a real game:

```
powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Seconds 300
powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Seconds 300 -Names SteelSeriesGG,SteelSeriesEngine,SteelSeriesSonar
```

## Logs

`%LOCALAPPDATA%\Replay Recorder\logs\recorder-<date>.log` lists the display and GPU for each monitor, the audio devices in use, the full ffmpeg command, and every saved clip.

## Troubleshooting

- **No game audio** while using HyperX NGENUITY, SteelSeries Sonar or VB-Cable: Replay Recorder should pick the real headset automatically. If it doesn't, choose it under Settings → Audio.
- **Hotkey does nothing:** another app has it (the NVIDIA overlay takes Alt+F9 and Alt+F10). The tray warns you when this happens. Pick another hotkey in settings.
- **Washed-out clips:** Windows HDR is on for that monitor. Clips are recorded in SDR, and the settings window warns about this.
- **Library says ffmpeg or mpv is missing:** run `winget install Gyan.FFmpeg` and `winget install shinchiro.mpv`, then reopen Replay Recorder.
- **Storage:** the tray warns once when the clips folder passes its limit (default 50 GB). Deleting a clip from the library deletes it for good (it asks first).
