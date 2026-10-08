# Audio Buddy

A small Windows desktop app for building a service (setlist) order. Add songs, pick the key each one is being sung in, and assign who's leading it. The master song list, default keys, and leader names come from an online Google Sheet.

## Setting up the Google Sheet

1. Create a Google Sheet with **two tabs**:

   **Tab `Songs`** — header row, then one song per row. The Length column (m:ss) is optional and drives the now-playing countdown; the BPM column (column D) is optional and shows in search results, the row editor and the now-playing panel. Columns E–G hold the key details edited in the app (see below) — the app adds them, with headers, the first time it writes one:

   | Song Name | Default Key | Length | BPM | Chromatic | Key Change | Key Change At |
   |-----------|-------------|--------|-----|-----------|------------|---------------|
   | Great Are You Lord | G | 5:30 | 72 | | | |
   | Oceans | D | 8:55 | 64 | | E | 5:10 |
   | Drum Intro | | 1:00 | | TRUE | | |

   **Tab `Leaders`** — header row, then one name per row:

   | Leader Name |
   |-------------|
   | Sarah |
   | Mike |

2. Give the app access, either way works:
   - **Public sheet**: Share → General access → "Anyone with the link" → Viewer. No sign-in needed.
   - **Private sheet**: keep it restricted and use **Settings → Sign in with Google** in the app. Sign-in is per machine and per Windows user, survives restarts, and reads the sheet through the official Sheets API. It also lets the app *add* songs to the sheet (see manual add below). **Sign out** from the same spot.

3. Copy the **Spreadsheet ID** from the sheet's URL — it's the long string between `/d/` and `/edit`:

   ```
   https://docs.google.com/spreadsheets/d/  THIS_PART_IS_THE_ID  /edit#gid=0
   ```

4. In the app, click **Settings** and paste the ID (or simply the whole sheet URL — the app pulls the ID out of it), then **Save & sync**. If you named the tabs something else, change the tab names there too.

   Settings are stored in `appsettings.json` next to `Audio Buddy.exe`, so you can also edit that file directly:

   ```json
   {
     "SpreadsheetId": "PUT_ID_HERE",
     "SongsTab": "Songs",
     "LeadersTab": "Leaders",
     "TimecodeDevice": ""
   }
   ```

## Running

Double-click `Audio Buddy.exe` (with `appsettings.json` next to it). On launch it pulls both tabs; **Refresh** re-pulls on demand. Every sync also updates the Length and BPM of songs already in the service from the sheet — no need to re-add a song after fixing its timestamp. If there's no internet, it keeps working from the last successful pull and shows *"Using cached list — last synced …"* in the status line.

### Using the app

- **Search box** — type part of a song name; Enter adds the top match (or pick from the list) with its default key filled in. If nothing matches, the list offers **+ Add "…" manually** (Enter takes it too) with the name prefilled.
- **+ Add manually** — for a song that isn't in the sheet. It joins the service immediately; when signed in with Google, the song (name + key) is also appended to the Songs tab so every machine picks it up on the next sync.
- **Rows at a glance** — each row shows the song, its key (with the enharmonic spelling, e.g. `F# (= Gb)`) and the leader as plain text.
- **✎ (pencil)** — opens the row's inline editor: key dropdown (majors and minors — `Gm`, `F#m`, …), leader dropdown (pick from the sheet or type a new name), a BPM box, and a colour strip to colour-code the song. Click ✓ to close — or just open another row's editor; only one is open at a time and edits apply live, so nothing is lost.
- **Chromatic / key change** (in the editor) — *Chromatic* is a flag next to the key (the row shows `A CHROMATIC`; the key stays set). *Key change* reveals **TO** (the new key) and **AT** (where in the song's timecode it happens, `m:ss`); the row then shows `C → D`, and while the song is playing the Now Playing panel shows `C → D at 1:45`, turning green — "now in D" — once the timeline passes that point. The whole window **flashes red through the last 10 seconds** before the change, goes **green for a moment** as it lands, then returns to normal. Both are song details, not service details: when signed in with Google they're written to the sheet (columns E–G) and every machine gets them on the next sync. Editing them on a song that isn't in the sheet adds the song to the sheet.
- **Drag a song's name** to reorder; click the name to grey it out as completed. **✕** removes a row, or right-click any row for edit/remove.
- **Small windows** — the layout adapts: below ~760 px wide the rows go compact (name + key, actions via right-click); below ~450 px tall the top bar folds into a ☰ menu.
- **▶** — marks a song as now playing. When timecode is locked, the countdown is **synced to the timeline**: remaining = song Length − the timecode position (mm:ss:ff; the hour is ignored, so hour-per-song layouts work). Without timecode it counts on the wall clock from the second ▶ click, and timecode takes over whenever it arrives. The number turns amber at 30 s left and red at 10 s or in overtime. Click again to stop.
- The window reopens at the size and position you left it (per machine, stored in `%APPDATA%\NovaSetlist\window.json`).
- **Notes** — the editor has a NOTE box for anything worth remembering about this song in this setlist ("Sarah on harmonies"); it shows under the song name and goes into *Copy as text*. Notes belong to the setlist, not the sheet.
- **Setlist ▾ menu** — *New setlist…* (names it, and **backs the current one up first** to `%APPDATA%\NovaSetlist\backups`, newest 50 kept), *Rename…*, *Open…* / *Save as…* (`.setlist.json` files — prep a setlist at home, open it on the Sunday PC), and *Copy as text* (`Setlist name` then `1. Song Name — Key G — Leader: Sarah — Note: …`). The name shows above the songs.
- **Settings** — set the spreadsheet ID / tab names from inside the app.
- **Settings → About** — software details, version number, and a **Check for updates** button.

### Timecode viewer

The panel on the right decodes **SMPTE LTC** from any WDM audio input and shows it live (same decoder as the Timecode Bridge — framerate autodetect incl. 29.97 drop-frame, works from very low line levels). Pick the input in **Settings → Audio inputs**; the choice is remembered. SIGNAL lights green when LTC cadence is detected, LOCK when two consecutive frames have decoded cleanly. View-only — nothing is retransmitted.

### Live key detection

Give it a second WDM input carrying the band / music feed (Settings → Audio inputs → Key detection) and the panel shows the key being played, updated as the music moves. It listens to a few seconds of harmonic context, so give it a moment after a key change; it shows "listening…" instead of guessing when it isn't confident. A feed with bass in it works best — the bass carries the tonality.

### MultiTracks Playback

In **Settings → MultiTracks Playback** click **Find Playback**: the app sweeps the networks this PC is on (port 8080, then checks that whatever answers really speaks Playback's remote protocol) and lists what it finds — pick one and **Save & sync**. You can also type a name or IP (`host:port` if the port is forwarded). Playback itself needs *Allow Remote Connections* on, which doesn't always survive a Playback relaunch. The PLAYBACK section then shows what Playback is on and its playhead, green while playing, amber when the heartbeat goes stale (5 s) and red when the PC can't be reached. The channel is the one the Playback Remote iPad app uses; the app only listens — the one exception is the walk below, which sends the same Previous/Next the Remote app would.

Playback reports songs as numbers, not names, and its protocol has no way to ask for them. Two ways to get names:

- **Match songs by order** (button in the PLAYBACK section, Playback must be stopped): the app steps Playback through its setlist — Previous to the start, Next to the end, then back to the song it was on — and names Playback song N after row N here. One click per setlist, about two seconds per song. If the two lists aren't in the same order, fix individual songs with the next option.
- **Link one song**: select the song in Playback, then right-click its row here and choose **Link to the song playing in Playback**.

Links are remembered per machine (`%APPDATA%\NovaSetlist\playback-map.json`). Until linked, the panel shows `Song 28727477 — not linked yet`. Every frame Playback sends is also logged to `%APPDATA%\NovaSetlist\playback-capture.jsonl` (rolling, ~3 MB) — the protocol is unofficial, and that file is the evidence when something looks wrong after a Playback update.

### SPL meter

The bottom of the side panel shows a live **SPL(A)** reading (IEC A-weighting, Slow 1 s or Fast 125 ms response) from a measurement-mic input picked in **Settings → SPL meter**. Calibrate it against a reference SPL meter with the **calibration offset** — type a value or trim with the −1 dB / +1 dB buttons; the offset applies live while the Settings window is open, so you can dial it in as you watch both meters. **Colour zones** recolour the number as the room gets louder: green until the yellow level, then yellow, then red. Untick "Show a live SPL(A) meter" to hide the section entirely.

The current service auto-saves on every change (`%APPDATA%\NovaSetlist\current.json`) and is restored when the app reopens. Key-detail edits that couldn't reach the sheet (offline, signed out) are kept and retried — on a timer, after a sign-in, or on the next launch — and the status line says so. Unexpected errors are logged to `%APPDATA%\NovaSetlist\crash.log` rather than closing the app mid-service.

## Updates

New versions are published as [GitHub releases](https://github.com/DavidPujals/audio-buddy/releases). Inside the app, **Settings → About → Check for updates** compares the running version against the latest release; if there's a newer one it downloads it and swaps the exe in place — click **Restart now** to finish. Your `appsettings.json` and saved service are untouched by updates.

### Cutting a release (maintainers)

1. Bump `<Version>` in `NovaSetlist/NovaSetlist.csproj` (e.g. `1.5.0`).
2. Publish (command below), then upload the exe under the asset name `AudioBuddy.exe`:

   ```
   gh release create v1.5.0 "AudioBuddy.exe" --title "v1.5.0" --notes "What changed"
   ```

   The tag (`v1.5.0`) must match the csproj version — the in-app updater compares them. Keep exactly one `.exe` asset per release: installs older than the rename look for the previous asset name and fall back to the first `.exe` they find.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build          # build
dotnet run --project NovaSetlist   # run from source
```

To publish a self-contained single-file exe for Windows x64 (no .NET install needed on the target machine):

```
dotnet publish NovaSetlist\NovaSetlist.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist
```

The result is `dist\Audio Buddy.exe`. Settings live in `appsettings.json` next to the exe — the app creates it when you save Settings, and republishing never overwrites an existing one.
