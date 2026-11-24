# Songify

Windows app that watches YouTube playlists and saves each song as an audio file in the format, bitrate and folder you choose.

For each video in a playlist it:

1. Searches YouTube Music for the official album track (no music-video intros/outros) and checks it against the video's title, artist and length.
2. If no confident match exists and **Record it from the Spotify app** is on (off by default), finds the song on Spotify and records it from the Spotify desktop app (see [Spotify recording](#spotify-recording)).
3. Otherwise saves the video's own audio and flags it "Saved – check".
4. Converts to MP3, AAC, Opus, Ogg Vorbis, FLAC or WAV with ffmpeg, or with **Original** keeps YouTube's audio stream untouched (usually Opus, about 130–160 kbps, saved as `.opus`). Writes title/artist/album tags and cover art to every format except WAV (and the rare Original that isn't Opus, AAC, Vorbis or MP3, saved as `.mka`); the song list's Quality column shows each file's codec and bitrate.

Songify remembers every video it has processed, so a song is never downloaded twice and a restart resumes unfinished work.

## Checking and editing songs

Songs saved from the video's audio or from a lower-confidence match are listed under **To check**. Double-click a saved song (or select it and press F2, or right-click → **Edit song…**) to fix its title, artist, album, album artist and year. Saving rewrites the file's tags without re-encoding the audio, keeps the cover art, renames the file from the **File name** pattern, and marks the song as checked. Close the song in your music player first: Songify can't change a file that's open, and leaves it untouched if it is. To clear the flag without editing, right-click → **Mark as checked** (works on several selected songs).

Editing fixes names, not the recording: if the saved audio is the wrong version, editing won't change that.

## Spotify recording

For songs YouTube Music doesn't have, Songify can record the album version from the Spotify desktop app (a Free account works). Without the Spotify API it finds the song through Deezer (for the recording's ISRC) and MusicBrainz (which links many ISRCs to Spotify), so many songs, especially less popular ones, can't be found and fall back to the video's audio.

How a recording works: Songify opens the song in Spotify, records only Spotify's audio (Windows per-app capture), and uses what Spotify reports as playing to cut out ads and anything played after the song. Recordings run in real time, one at a time. While recording, Songify mutes your PC's sound output (the recording isn't affected); if Songify crashes mid-recording, it unmutes on its next start. With **Only when idle** (on by default), it waits until nobody has used the keyboard or mouse for the chosen time and no other app is playing sound, and drops and later retries a recording if you touch the PC.

- Keep Spotify's own volume at 100% and don't mute Spotify in the Windows volume mixer: the recording follows them.
- Turn off Crossfade and Automix in Spotify's Playback settings if your account has them: they blend the song into the next one.
- With **Original** format, recordings are saved as Ogg Vorbis 160 kbps, Spotify Free's own quality.
- If Spotify plays something else (Free accounts have an unpublished daily limit on choosing songs), Songify tries that song again an hour later, up to three times, then saves the video's audio. Other songs keep recording meanwhile. Any other recording failure saves the video's audio right away.
- If Deezer or MusicBrainz can't be reached, the song is marked Failed (as when YouTube Music is down) rather than saved as video audio; use **Try again** later.
- Recording breaks Spotify's Terms of Use and could get the account banned. Use a separate free account.

## Download

1. Open the [latest release](https://github.com/Cothek/Songify/releases/latest) and download **Songify-win-x64.zip** (Windows 10 version 2004 or later, or Windows 11; 64-bit).
2. Unzip it anywhere and run **Songify.exe**. Nothing else needs to be installed.
3. Windows may say "Windows protected your PC", because the app isn't code-signed. Click **More info → Run anyway**.

## Build from source

Requires the .NET 10 SDK (use the `dotnet` command line or Visual Studio 2026; Visual Studio 2022 can't open .NET 10 projects).

```powershell
dotnet run --project src/Songify.App
```

To build the same single-file `Songify.exe` as the download (into `artifacts\publish\win-x64`):

```powershell
dotnet publish src/Songify.App -p:PublishProfile=win-x64
```

## First start

On first start Songify downloads its tools (about 150 MB) into `%LOCALAPPDATA%\Songify\tools`: [yt-dlp](https://github.com/yt-dlp/yt-dlp), [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) and [Deno](https://deno.com) (yt-dlp needs a JavaScript runtime for YouTube). yt-dlp updates itself once a day.

Settings and the track library live in `%APPDATA%\Songify` (`settings.json`, `library.json`, `songify.log`). Set `SONGIFY_DATA_DIR` to use a different folder, e.g. for testing.

## Tests

```powershell
dotnet test
```

Tests that run the real tools (the end-to-end test against YouTube, and the ffmpeg encoding and song-editing tests) are skipped unless `SONGIFY_INTEGRATION=1`; the ffmpeg tests need the tools already downloaded by a first start.

## Releasing

GitHub Actions (`.github/workflows/build.yml`) builds and tests every push to `main` and every pull request. To publish a download, push a version tag; the workflow attaches `Songify-win-x64.zip` to a GitHub release (re-running it replaces the zip):

```powershell
git tag v0.1.0
git push origin v0.1.0
```

A tag with a suffix, like `v0.2.0-beta.1`, is published as a pre-release, so the download link above keeps pointing at the latest stable version.

## Layout

- `src/Songify.Core` – everything but the UI: playlist polling and work queue (`Processing/`), title parsing and match scoring (`Matching/`), yt-dlp and YouTube Music clients (`YouTube/`), ffmpeg encoding and file naming (`Audio/`), tool downloads (`Tools/`).
- `src/Songify.Core/Spotify` – Spotify song lookup (`SpotifyTrackResolver`), the recorder interface, and finding the song in a capture (`RecordingCutter`).
- `src/Songify.Spotify` – the Windows-only recorder: per-app audio capture (NAudio), the Windows media session (what Spotify reports as playing), output mute and idle detection. Needs Windows 10 version 2004 or later.
- `src/Songify.App` – WPF window (MVVM via CommunityToolkit.Mvvm). The look comes from [WPF UI](https://github.com/lepoco/wpfui) (Windows 11 Fluent): use its theme brushes (`TextFillColorPrimaryBrush`, `SystemFillColorCriticalBrush`, …) via `DynamicResource` so colors follow the light/dark setting (Appearance → Theme; "Match Windows" by default). App-specific styles are in `App.xaml`.
- `tests/Songify.Tests` – xUnit tests.

## Notes

- Downloading from YouTube is against YouTube's Terms of Service. Keep the files for personal use.
- Planned: optionally removing saved songs from the YouTube playlist (needs YouTube Data API sign-in).
