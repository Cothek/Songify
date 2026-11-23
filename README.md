# Songify

Windows app that watches YouTube playlists and saves each song as an audio file in the format, bitrate and folder you choose.

For each video in a playlist it:

1. Searches YouTube Music for the official album track (no music-video intros/outros) and checks it against the video's title, artist and length.
2. If no confident match exists, saves the video's own audio and flags it "Saved – check".
3. Converts to MP3, AAC, Opus, Ogg Vorbis, FLAC or WAV with ffmpeg, or with **Original** keeps YouTube's audio stream untouched (usually Opus, about 130–160 kbps, saved as `.opus`). Writes title/artist/album tags and cover art to every format except WAV (and the rare Original that isn't Opus, AAC, Vorbis or MP3, saved as `.mka`); the song list's Quality column shows each file's codec and bitrate.

Songify remembers every video it has processed, so a song is never downloaded twice and a restart resumes unfinished work.

## Run

Requires the .NET 8 SDK.

```powershell
dotnet run --project src/Songify.App
```

On first start Songify downloads its tools (about 150 MB) into `%LOCALAPPDATA%\Songify\tools`: [yt-dlp](https://github.com/yt-dlp/yt-dlp), [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) and [Deno](https://deno.com) (yt-dlp needs a JavaScript runtime for YouTube). yt-dlp updates itself once a day.

Settings and the track library live in `%APPDATA%\Songify` (`settings.json`, `library.json`, `songify.log`). Set `SONGIFY_DATA_DIR` to use a different folder, e.g. for testing.

## Tests

```powershell
dotnet test
```

The end-to-end test hits real YouTube and is skipped unless `SONGIFY_INTEGRATION=1`.

## Layout

- `src/Songify.Core` – everything but the UI: playlist polling and work queue (`Processing/`), title parsing and match scoring (`Matching/`), yt-dlp and YouTube Music clients (`YouTube/`), ffmpeg encoding and file naming (`Audio/`), tool downloads (`Tools/`).
- `src/Songify.App` – WPF window (MVVM via CommunityToolkit.Mvvm). The look comes from [WPF UI](https://github.com/lepoco/wpfui) (Windows 11 Fluent): use its theme brushes (`TextFillColorPrimaryBrush`, `SystemFillColorCriticalBrush`, …) via `DynamicResource` so colors follow the light/dark setting (Appearance → Theme; "Match Windows" by default). App-specific styles are in `App.xaml`.
- `tests/Songify.Tests` – xUnit tests.

## Notes

- Downloading from YouTube is against YouTube's Terms of Service. Keep the files for personal use.
- Planned: recording from the Spotify desktop app as a second fallback, and optionally removing saved songs from the YouTube playlist (needs YouTube Data API sign-in).
