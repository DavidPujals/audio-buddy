using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaSetlist.Models;
using NovaSetlist.Music;
using NovaSetlist.Services;

namespace NovaSetlist.ViewModels;

/// <summary>
/// The live link to MultiTracks Playback: connection state, the song ID it is on and its
/// playhead. Playback only reports a number; the song behind it (name, key, BPM, play
/// length) is looked up in the sheet's Playback tab — the database every machine reads on
/// sync. The app only ever listens on this channel and only ever reads that tab.
/// </summary>
public partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private const int StaleMs = 5000;

    /// <summary>What the database knows about one Playback song ID. Duration is the play
    /// length (0 = unknown); Bpm 0 = unknown.</summary>
    public sealed record RefSong(long SetlistSongId, string Title, string Key, double Bpm, double Duration);

    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, RefSong> _songs = new();
    private PlaybackClient? _client;

    /// <summary>Raised on the UI thread after every poll — the main view model follows Playback from it.</summary>
    public event Action? Updated;

    /// <summary>Playhead position in seconds, interpolated between heartbeats while playing.</summary>
    public double PositionSeconds { get; private set; }

    /// <summary>A host is configured — the connection status shows in the NOW PLAYING header.</summary>
    [ObservableProperty]
    private bool isEnabled;

    /// <summary>"off", "connecting", "connected", "stale" (socket up, heartbeat old) or "error".</summary>
    [ObservableProperty]
    private string state = "off";

    /// <summary>Short connection detail for the header tooltip / empty panel: the host, the error, …</summary>
    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private bool isPlaying;

    /// <summary>The key the current song is in according to the database; "" = unknown or no song.</summary>
    [ObservableProperty]
    private string songKey = "";

    public string Host { get; private set; }

    public PlaybackViewModel(AppConfig config)
    {
        _config = config;
        Host = config.PlaybackHost;
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
            StatusText = "";
            SongKey = "";
            PositionSeconds = 0;
            IsPlaying = false;
            _timer.Stop();
            return;
        }
        _client = new PlaybackClient(Host);
        IsEnabled = true;
        State = "connecting";
        StatusText = $"connecting to {Host}…";
        _client.Start();
        _timer.Start();
    }

    private void Poll()
    {
        PollCore();
        Updated?.Invoke();
    }

    private void PollCore()
    {
        var c = _client;
        if (c is null)
            return;

        if (!c.Connected)
        {
            State = c.Error.Length > 0 ? "error" : "connecting";
            StatusText = c.Error.Length > 0 ? c.Error : $"connecting to {Host}…";
            SongKey = "";
            PositionSeconds = 0;
            IsPlaying = false;
            return;
        }

        var hb = c.LastHeartbeatTick;
        if (hb == 0)
        {
            State = "connecting";
            StatusText = "connected — waiting for Playback's heartbeat";
            return;
        }

        var age = Environment.TickCount64 - hb;
        var stale = age > StaleMs;
        State = stale ? "stale" : "connected";
        StatusText = stale ? $"no heartbeat for {age / 1000} s" : $"connected to {Host}";

        var id = c.SongId;
        SongKey = id >= 0 && !stale && _songs.TryGetValue(id, out var r) ? r.Key : "";

        // Heartbeats are 1 s apart: while playing, run the playhead on between them.
        var playing = c.Playing && !stale;
        PositionSeconds = c.PositionSeconds + (playing ? Math.Min(age, 1500) / 1000.0 : 0);
        IsPlaying = playing;
    }

    // ---------- the database (sheet's Playback tab) ----------

    /// <summary>What the database knows about a Playback song ID, or null when it isn't in the tab.</summary>
    public RefSong? SongFor(long id) => _songs.TryGetValue(id, out var r) ? r : null;

    /// <summary>Takes the sheet's Playback tab (a sync just read it) as the database.</summary>
    public void ApplySheetRows(IReadOnlyList<PlaybackRow> rows)
    {
        _songs.Clear();
        foreach (var r in rows)
        {
            if (r.SetlistSongId <= 0 || r.Title.Trim().Length == 0)
                continue;
            _songs[r.SetlistSongId] = new RefSong(r.SetlistSongId, r.Title.Trim(), r.Key,
                double.TryParse(r.Bpm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bpm) ? bpm : 0,
                SongLength.ParseSeconds(r.Length));
        }
        Poll();
    }

    public void Dispose()
    {
        _timer.Stop();
        _client?.Dispose();
        _client = null;
    }
}
