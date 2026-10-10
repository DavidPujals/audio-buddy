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
    private static string ReferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "multitracks-setlists.json");

    /// <summary>One song of a MultiTracks setlist as the reference file records it. Duration is
    /// Playback's own play length for this setlist entry (0 = unknown).</summary>
    public sealed record RefSong(long SetlistSongId, long SetlistId, int Order, string Title, string Key, double Bpm, double Duration);
    public sealed record RefSetlist(long Id, string Name, string Date, int Version, List<RefSong> Songs)
    {
        public string Label => Date.Length > 0 ? $"{Name}  ·  {Date}" : Name;
        public override string ToString() => Label; // the ComboBox's selection box shows this
    }

    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, string> _titles = new();
    private readonly Dictionary<long, RefSong> _refSongs = new();
    private PlaybackClient? _client;

    /// <summary>MultiTracks setlists from the reference file, newest date first.</summary>
    public List<RefSetlist> ReferenceSetlists { get; private set; } = new();

    /// <summary>Raised on the UI thread after every poll — the main view model follows Playback from it.</summary>
    public event Action? Updated;

    /// <summary>Playhead position in seconds, interpolated between heartbeats while playing.</summary>
    public double PositionSeconds { get; private set; }

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

    /// <summary>The key MultiTracks has the current song in (from the setlist); "" = unknown or no song.</summary>
    [ObservableProperty]
    private string songKey = "";

    public string Host { get; private set; }

    public PlaybackViewModel(AppConfig config)
    {
        _config = config;
        Host = config.PlaybackHost;
        LoadMap();
        LoadReference();
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
            SongKey = "";
            PositionText = "—";
            PositionSeconds = 0;
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

    private DateTime _mapStamp, _refStamp;
    private long _mapCheckTick;

    /// <summary>The map and reference files can be refreshed from outside (a MultiTracks export);
    /// pick that up without a restart. Checked every few seconds — two stat calls, nothing more.</summary>
    private void ReloadFilesIfChanged()
    {
        var now = Environment.TickCount64;
        if (now - _mapCheckTick < 3000)
            return;
        _mapCheckTick = now;
        try
        {
            var stamp = File.Exists(MapPath) ? File.GetLastWriteTimeUtc(MapPath) : DateTime.MinValue;
            if (stamp != _mapStamp)
            {
                _mapStamp = stamp;
                _titles.Clear();
                LoadMap();
            }
            var refStamp = File.Exists(ReferencePath) ? File.GetLastWriteTimeUtc(ReferencePath) : DateTime.MinValue;
            if (refStamp != _refStamp)
            {
                _refStamp = refStamp;
                LoadReference();
            }
        }
        catch
        {
            // transient file access issue — try again on the next check
        }
    }

    private void Poll()
    {
        PollCore();
        Updated?.Invoke();
    }

    private void PollCore()
    {
        ReloadFilesIfChanged();
        var c = _client;
        if (c is null)
            return;

        if (!c.Connected)
        {
            State = c.Error.Length > 0 ? "error" : "connecting";
            SongText = "";
            SongKey = "";
            PositionText = "—";
            PositionSeconds = 0;
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
        SongText = id < 0 ? "no song loaded" : TitleFor(id) ?? $"Song {id} — not linked yet";
        SongKey = id >= 0 && !stale && _refSongs.TryGetValue(id, out var r) ? r.Key : "";

        // Heartbeats are 1 s apart: while playing, run the playhead on between them.
        var playing = c.Playing && !stale;
        var pos = c.PositionSeconds + (playing ? Math.Min(age, 1500) / 1000.0 : 0);
        PositionSeconds = pos;
        PositionText = SongLength.Format(pos);
        IsPlaying = playing;
        SubText = stale ? $"no heartbeat for {age / 1000} s"
                : (c.Playing ? "playing" : "stopped") +
                  (c.SetlistName.Length > 0 ? $" · {c.SetlistName}" : "");
    }

    /// <summary>What the reference file knows about a Playback song (key, duration, setlist), or null.</summary>
    public RefSong? ReferenceFor(long id) => _refSongs.TryGetValue(id, out var r) ? r : null;

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

    /// <summary>Name for a Playback song: a learned link first, else the reference file's title.</summary>
    public string? TitleFor(long id) =>
        _titles.TryGetValue(id, out var t) ? t
        : _refSongs.TryGetValue(id, out var r) && r.Title.Length > 0 ? r.Title
        : null;

    /// <summary>Takes a batch of names (a MultiTracks refresh) into the map in one save.</summary>
    public int MergeTitles(IReadOnlyDictionary<long, string> titles)
    {
        var changed = 0;
        foreach (var (id, title) in titles)
        {
            var t = title.Trim();
            if (id < 0 || t.Length == 0)
                continue;
            if (!_titles.TryGetValue(id, out var have) || have != t)
            {
                _titles[id] = t;
                changed++;
            }
        }
        if (changed > 0)
            SaveMap();
        LoadReference(); // the refresh rewrote the reference file too
        try { _refStamp = File.Exists(ReferencePath) ? File.GetLastWriteTimeUtc(ReferencePath) : DateTime.MinValue; } catch { }
        Poll();
        return changed;
    }

    /// <summary>Reads multitracks-setlists.json (written by the MultiTracks refresh, or by hand):
    /// per-song keys and Playback durations, plus the setlists for the timecode dialog.</summary>
    private void LoadReference()
    {
        _refSongs.Clear();
        var lists = new List<RefSetlist>();
        try
        {
            if (File.Exists(ReferencePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ReferencePath));
                if (doc.RootElement.TryGetProperty("setlists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sl in arr.EnumerateArray())
                    {
                        var id = Long(sl, "setlistID");
                        var songs = new List<RefSong>();
                        if (sl.TryGetProperty("songs", out var sa) && sa.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in sa.EnumerateArray())
                            {
                                var song = new RefSong(Long(s, "setlistSongID"), id, (int)Long(s, "order"),
                                    Str(s, "title"), Str(s, "key"), Dbl(s, "bpm"), Dbl(s, "durationSeconds"));
                                songs.Add(song);
                                if (song.SetlistSongId > 0)
                                    _refSongs[song.SetlistSongId] = song;
                            }
                        }
                        lists.Add(new RefSetlist(id, Str(sl, "name"), Str(sl, "date"), (int)Long(sl, "version"),
                            songs.OrderBy(x => x.Order).ToList()));
                    }
                }
            }
        }
        catch
        {
            // Unreadable reference — keep whatever parsed; the map still names songs.
        }
        ReferenceSetlists = lists.OrderByDescending(l => l.Date, StringComparer.Ordinal).ThenBy(l => l.Name).ToList();
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? n : 0;
    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
            _mapStamp = File.GetLastWriteTimeUtc(MapPath); // our own write — no reload needed
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
