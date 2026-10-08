using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NovaSetlist.Services;

/// <summary>
/// Read-only listener on MultiTracks Playback's remote-control WebSocket — the channel the
/// Playback Remote app uses: ws://host:8080/, subprotocol "pr-protocol", no authentication.
/// Playback sends a heartbeat once a second with the current song ID, playhead seconds and
/// transport state. Song NAMES are never on the wire (see PlaybackViewModel's name map).
/// This client never sends anything. Protocol per huntrw6/playback-api PROTOCOL.md
/// (observed on Playback 8.5.4); unofficial, so re-verify after Playback updates.
/// </summary>
public sealed class PlaybackClient : IDisposable
{
    public const int Port = 8080;
    private const string SubProtocol = "pr-protocol";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Socket open but nothing arriving: Playback has been seen to go silent for
    /// minutes with the socket up, so a long silence is treated as a dead connection.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(15);

    private static string SetlistDumpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "playback-last-setlist.json");

    private readonly string _host;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private int _connected;          // 1 while the socket is up
    private long _lastHeartbeatTick; // Environment.TickCount64 of the last heartbeat; 0 = none yet
    private long _songId = -1;       // -1 = no song (setlist empty or loading)
    private long _positionMilli;     // sequenceTime in ms at the last heartbeat
    private int _playing;
    private volatile string _error = "";
    private volatile string _setlistName = "";

    private readonly int _port = Port;

    /// <param name="host">Playback PC name or IP; "host:port" overrides the default 8080.</param>
    public PlaybackClient(string host)
    {
        host = host.Trim();
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var port) && port is > 0 and < 65536)
        {
            _port = port;
            host = host[..colon];
        }
        _host = host;
    }

    public string Host => _host;
    public bool Connected => Volatile.Read(ref _connected) == 1;
    public long LastHeartbeatTick => Volatile.Read(ref _lastHeartbeatTick);
    public long SongId => Volatile.Read(ref _songId);
    public double PositionSeconds => Volatile.Read(ref _positionMilli) / 1000.0;
    public bool Playing => Volatile.Read(ref _playing) == 1;
    /// <summary>Why the last connection attempt failed; "" while fine.</summary>
    public string Error => _error;
    /// <summary>Name of the setlist loaded in Playback, if a load was seen while connected.</summary>
    public string SetlistName => _setlistName;

    /// <summary>Raised on a background thread when a setlist-load message carries a song title
    /// alongside a song ID (not promised by the protocol — a bonus if present).</summary>
    public event Action<long, string>? TitleDiscovered;

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var backoff = 2;
        while (!_cts.IsCancellationRequested)
        {
            var sawTraffic = false;
            try
            {
                await SessionAsync(() => sawTraffic = true);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _error = Describe(ex);
            }
            finally
            {
                Volatile.Write(ref _connected, 0);
            }

            // A session that was actually receiving and then dropped comes back quickly;
            // a host that never answers is retried less and less often (2 s → 30 s).
            if (sawTraffic)
                backoff = 2;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), _cts.Token); }
            catch (OperationCanceledException) { return; }
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    private async Task SessionAsync(Action onTraffic)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(SubProtocol);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), connectCts.Token);
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                throw new TimeoutException($"no answer from {_host}:{_port}");
            }
        }
        Volatile.Write(ref _connected, 1);
        _error = "";

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                readCts.CancelAfter(SilenceTimeout);
                try
                {
                    result = await ws.ReceiveAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Playback went quiet for {SilenceTimeout.TotalSeconds:0} s — reconnecting");
                }
            }
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("Playback closed the connection");

            onTraffic();
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                if (message.Length > 16_000_000)
                    message.SetLength(0); // runaway frame — drop it rather than grow forever
                continue;
            }
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Handle(text);
        }
    }

    private void Handle(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return; } // not JSON — ignore
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (root.TryGetProperty("heartbeat", out var hb) &&
                hb.TryGetProperty("stateData", out var state))
            {
                // {"heartbeat":{"stateData":{"setlistSongID":91000001,"sequenceTime":12.4,
                //   "sequencerPlayState":{"playing":{}}, ...}}}  — setlistSongID is ABSENT
                // while the setlist is empty or still loading.
                var id = state.TryGetProperty("setlistSongID", out var sid) && sid.TryGetInt64(out var v) ? v : -1;
                var pos = state.TryGetProperty("sequenceTime", out var st) && st.TryGetDouble(out var p) ? p : 0;
                var playing = state.TryGetProperty("sequencerPlayState", out var ps) &&
                              ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("playing", out _);
                Volatile.Write(ref _songId, id);
                Volatile.Write(ref _positionMilli, (long)(pos * 1000));
                Volatile.Write(ref _playing, playing ? 1 : 0);
                Volatile.Write(ref _lastHeartbeatTick, Environment.TickCount64);
                return;
            }

            if (root.TryGetProperty("contentLoadSetlist", out var load))
            {
                if (load.TryGetProperty("setlistData", out var data) &&
                    data.TryGetProperty("setlistName", out var name) && name.ValueKind == JsonValueKind.String)
                    _setlistName = name.GetString() ?? "";
                // Keep the raw message: nobody has documented what liveData holds, and if song
                // titles are in there this file is how we'll find out.
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SetlistDumpPath)!);
                    File.WriteAllText(SetlistDumpPath, text);
                }
                catch { /* diagnostics only */ }
                ScanForTitles(load, 0);
            }
        }
    }

    private static readonly string[] IdKeys = { "setlistSongID", "setlistSongId", "songID", "songId", "id" };
    private static readonly string[] NameKeys = { "name", "title", "songName", "songTitle", "displayName" };

    /// <summary>Best-effort: any object carrying a song-sized integer ID next to a name string.</summary>
    private void ScanForTitles(JsonElement el, int depth)
    {
        if (depth > 24)
            return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                long id = -1;
                string? title = null;
                foreach (var prop in el.EnumerateObject())
                {
                    if (id < 0 && prop.Value.ValueKind == JsonValueKind.Number &&
                        Array.Exists(IdKeys, k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)) &&
                        prop.Value.TryGetInt64(out var n) && n >= 1_000_000)
                        id = n;
                    else if (title is null && prop.Value.ValueKind == JsonValueKind.String &&
                             Array.Exists(NameKeys, k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)))
                        title = prop.Value.GetString();
                }
                if (id >= 0 && !string.IsNullOrWhiteSpace(title))
                    TitleDiscovered?.Invoke(id, title.Trim());
                foreach (var prop in el.EnumerateObject())
                    ScanForTitles(prop.Value, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    ScanForTitles(item, depth + 1);
                break;
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        TimeoutException t => t.Message,
        WebSocketException or SocketException or HttpRequestException =>
            $"can't reach {_host}:{_port} — is Playback running with Allow Remote Connections on?",
        _ => ex.Message,
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch { /* cancelled */ }
        _cts.Dispose();
    }
}
