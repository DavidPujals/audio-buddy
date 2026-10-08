using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaSetlist.Music;
using NovaSetlist.Services;

namespace NovaSetlist.ViewModels;

/// <summary>
/// The PLAYBACK side-panel section: what MultiTracks Playback is on and where its playhead is.
/// Playback only reports a numeric song ID, so names come from a per-machine map that the
/// operator fills by linking a setlist row to whatever Playback is currently on.
/// </summary>
public partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private const int StaleMs = 5000;

    private static string MapPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "playback-map.json");

    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, string> _titles = new();
    private PlaybackClient? _client;

    /// <summary>A host is configured — the PLAYBACK section shows.</summary>
    [ObservableProperty]
    private bool isEnabled;

    /// <summary>"off", "connecting", "connected", "stale" (socket up, heartbeat old) or "error".</summary>
    [ObservableProperty]
    private string state = "off";

    [ObservableProperty]
    private string songText = "";

    [ObservableProperty]
    private string positionText = "—";

    [ObservableProperty]
    private string subText = "";

    [ObservableProperty]
    private bool isPlaying;

    public string Host { get; private set; }

    public PlaybackViewModel(AppConfig config)
    {
        _config = config;
        Host = config.PlaybackHost;
        LoadMap();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Poll();
        Restart();
    }

    /// <summary>Song ID Playback is currently on, or -1 when there's no live, fresh heartbeat with a song.</summary>
    public long CurrentSongId
    {
        get
        {
            var c = _client;
            if (c is null || !c.Connected || c.LastHeartbeatTick == 0 ||
                Environment.TickCount64 - c.LastHeartbeatTick > StaleMs)
                return -1;
            return c.SongId;
        }
    }

    /// <summary>Applies a new host from Settings; "" turns the listener off.</summary>
    public void Apply(string host)
    {
        host = host.Trim();
        if (host == Host)
            return;
        Host = host;
        _config.PlaybackHost = host;
        _config.Save();
        Restart();
    }

    private void Restart()
    {
        _client?.Dispose();
        _client = null;
        if (Host.Length == 0)
        {
            IsEnabled = false;
            State = "off";
            SongText = "";
            PositionText = "—";
            SubText = "";
            IsPlaying = false;
            _timer.Stop();
            return;
        }
        var client = new PlaybackClient(Host);
        client.TitleDiscovered += (id, title) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Learn(id, title, announce: false));
        _client = client;
        IsEnabled = true;
        State = "connecting";
        SongText = "";
        PositionText = "—";
        SubText = $"connecting to {Host}…";
        client.Start();
        _timer.Start();
    }

    private void Poll()
    {
        var c = _client;
        if (c is null)
            return;

        if (!c.Connected)
        {
            State = c.Error.Length > 0 ? "error" : "connecting";
            SongText = "";
            PositionText = "—";
            SubText = c.Error.Length > 0 ? c.Error : $"connecting to {Host}…";
            IsPlaying = false;
            return;
        }

        var hb = c.LastHeartbeatTick;
        if (hb == 0)
        {
            State = "connecting";
            SubText = "connected — waiting for Playback's heartbeat";
            return;
        }

        var age = Environment.TickCount64 - hb;
        var stale = age > StaleMs;
        State = stale ? "stale" : "connected";

        var id = c.SongId;
        SongText = id < 0 ? "no song loaded"
                 : _titles.TryGetValue(id, out var title) ? title
                 : $"Song {id} — not linked yet";

        // Heartbeats are 1 s apart: while playing, run the playhead on between them.
        var playing = c.Playing && !stale;
        var pos = c.PositionSeconds + (playing ? Math.Min(age, 1500) / 1000.0 : 0);
        PositionText = SongLength.Format(pos);
        IsPlaying = playing;
        SubText = stale ? $"no heartbeat for {age / 1000} s"
                : (c.Playing ? "playing" : "stopped") +
                  (c.SetlistName.Length > 0 ? $" · {c.SetlistName}" : "");
    }

    // ---------- match by order (setlist walk) ----------

    /// <summary>
    /// Learns Playback's song order by stepping it through its setlist — Previous to the
    /// start, Next to the end, then back to where it was — using the same commands the
    /// Playback Remote app sends. Playback accepts these only while STOPPED. Returns the
    /// ordered song IDs, or throws with a user-readable reason.
    /// </summary>
    public async Task<List<long>> WalkSetlistAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        var c = _client ?? throw new InvalidOperationException("Playback isn't set up — pick the Playback computer in Settings first.");
        if (CurrentSongId < 0)
            throw new InvalidOperationException("Playback isn't connected, or has no setlist loaded.");
        if (c.Playing)
            throw new InvalidOperationException("Playback is playing — stop it first (the walk only works while stopped).");

        var start = c.SongId;
        var ids = new List<long>();
        try
        {
            // Rewind to the first song.
            progress?.Report("rewinding to the first song…");
            for (var guard = 0; guard < 200; guard++)
            {
                var before = c.SongId;
                if (!await c.SendAsync("{\"transportPreviousSong\":{}}", cancel))
                    throw new InvalidOperationException("Lost the connection to Playback.");
                if (!await WaitForChangeAsync(c, before, cancel))
                    break; // no change = already at the start
            }

            ids.Add(c.SongId);
            progress?.Report($"reading the order… 1");
            for (var guard = 0; guard < 200; guard++)
            {
                var before = c.SongId;
                if (!await c.SendAsync("{\"transportNextSong\":{}}", cancel))
                    throw new InvalidOperationException("Lost the connection to Playback.");
                if (!await WaitForChangeAsync(c, before, cancel))
                    break; // no change = last song
                ids.Add(c.SongId);
                progress?.Report($"reading the order… {ids.Count}");
            }
        }
        finally
        {
            // Put Playback back on the song it was on, whatever happened.
            if (start >= 0 && c.SongId != start)
                await c.SendAsync($"{{\"setlistSelectSong\":{{\"setlistSongID\":{start}}}}}", CancellationToken.None);
        }
        return ids;
    }

    /// <summary>Waits up to ~2.5 s for the heartbeat song ID to move off <paramref name="before"/>.</summary>
    private static async Task<bool> WaitForChangeAsync(PlaybackClient c, long before, CancellationToken cancel)
    {
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(100, cancel);
            if (c.SongId != before && c.SongId >= 0)
            {
                await Task.Delay(150, cancel); // let the heartbeat settle before the next command
                return true;
            }
        }
        return false;
    }

    // ---------- name map ----------

    /// <summary>Remembers that Playback song <paramref name="id"/> is <paramref name="title"/> (per machine).</summary>
    public void Learn(long id, string title, bool announce = true)
    {
        title = title.Trim();
        if (id < 0 || title.Length == 0)
            return;
        if (_titles.TryGetValue(id, out var have) && have == title)
            return;
        _titles[id] = title;
        SaveMap();
        if (announce)
            Poll();
    }

    public string? TitleFor(long id) => _titles.TryGetValue(id, out var t) ? t : null;

    private void LoadMap()
    {
        try
        {
            if (!File.Exists(MapPath))
                return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapPath));
            if (map is null)
                return;
            foreach (var (k, v) in map)
            {
                if (long.TryParse(k, out var id) && !string.IsNullOrWhiteSpace(v))
                    _titles[id] = v.Trim();
            }
        }
        catch
        {
            // Unreadable map — start empty; links get re-learned.
        }
    }

    private void SaveMap()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath)!);
            var map = _titles.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString(), p => p.Value);
            var tmp = MapPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, MapPath, overwrite: true);
        }
        catch
        {
            // Best effort — the link still works for this run.
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _client?.Dispose();
        _client = null;
    }
}
