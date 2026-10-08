using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NovaSetlist.Services;

/// <summary>
/// Finds machines running MultiTracks Playback on the networks this PC is attached to.
/// Playback has no Bonjour advert, so this is a passive sweep: one TCP connect to port
/// 8080 per address, then — only for the few that answer — a WebSocket handshake with the
/// pr-protocol subprotocol and a wait for a real heartbeat. Anything else on 8080 (grandMA3's
/// web remote, a dev server…) fails that test and is ignored. Nothing is ever sent to Playback.
/// </summary>
public static class PlaybackDiscovery
{
    public sealed record Found(string Address, string Name, long SongId, bool Playing)
    {
        public string Label => Name.Length > 0 && Name != Address ? $"{Name}  ({Address})" : Address;
    }

    private const int MaxHostsPerNetwork = 1024; // bigger networks collapse to the /24 around our address
    private const int Parallelism = 128;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    public static async Task<List<Found>> ScanAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        var networks = LocalNetworks();
        var candidates = networks.SelectMany(n => n.Hosts).Distinct().ToList();
        progress?.Report(candidates.Count == 0
            ? "no local network found"
            : $"checking {candidates.Count} addresses on {string.Join(", ", networks.Select(n => n.Description))}…");

        var found = new List<Found>();
        var open = new List<IPAddress>();
        using var gate = new SemaphoreSlim(Parallelism);
        var tasks = candidates.Select(async ip =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                if (await PortOpenAsync(ip, cancel))
                    lock (open) open.Add(ip);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        progress?.Report(open.Count == 0
            ? "nothing answered on port 8080"
            : $"{open.Count} answered on 8080 — checking which are Playback…");

        foreach (var ip in open)
        {
            cancel.ThrowIfCancellationRequested();
            var hit = await ProbeAsync(ip, cancel);
            if (hit is not null)
                found.Add(hit);
        }
        return found.OrderBy(f => f.Address, StringComparer.Ordinal).ToList();
    }

    private static async Task<bool> PortOpenAsync(IPAddress ip, CancellationToken cancel)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(ip, PlaybackClient.Port, cts.Token);
            return true;
        }
        catch
        {
            return false; // refused, filtered, or timed out — not Playback
        }
    }

    /// <summary>Handshake + first heartbeat: the only proof that 8080 here is Playback.</summary>
    private static async Task<Found?> ProbeAsync(IPAddress ip, CancellationToken cancel)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("pr-protocol");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{ip}:{PlaybackClient.Port}/"), cts.Token);
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            while (!cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                    continue;
                var frame = text.ToString();
                text.Clear();
                if (TryParseHeartbeat(frame, out var songId, out var playing))
                {
                    var name = await NameOfAsync(ip);
                    return new Found(ip.ToString(), name, songId, playing);
                }
            }
        }
        catch
        {
            // not a WebSocket, wrong subprotocol, or silent — not Playback
        }
        return null;
    }

    private static bool TryParseHeartbeat(string frame, out long songId, out bool playing)
    {
        songId = -1;
        playing = false;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (!doc.RootElement.TryGetProperty("heartbeat", out var hb) ||
                !hb.TryGetProperty("stateData", out var state))
                return false;
            if (state.TryGetProperty("setlistSongID", out var sid) && sid.TryGetInt64(out var v))
                songId = v;
            playing = state.TryGetProperty("sequencerPlayState", out var ps) &&
                      ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("playing", out _);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> NameOfAsync(IPAddress ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) == lookup && lookup.Result.HostName is { Length: > 0 } host)
            {
                var dot = host.IndexOf('.');
                return dot > 0 ? host[..dot] : host; // "Davids-MacBook.local" → "Davids-MacBook"
            }
        }
        catch
        {
            // no reverse DNS on this LAN — the address alone will do
        }
        return "";
    }

    private sealed record Network(IPAddress Address, int Prefix, IEnumerable<IPAddress> Hosts, string Description);

    private static List<Network> LocalNetworks()
    {
        var list = new List<Network>();
        var seen = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) // link-local — APIPA, no real LAN
                    continue;
                var prefix = ua.PrefixLength;
                if (prefix is < 8 or > 30)
                    continue;
                var hostCount = (1 << (32 - prefix)) - 2;
                if (hostCount > MaxHostsPerNetwork)
                    prefix = 24;

                var addr = BitConverter.ToUInt32(bytes.Reverse().ToArray(), 0);
                var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                var network = addr & mask;
                var key = $"{network}/{prefix}";
                if (!seen.Add(key))
                    continue;
                var count = (1 << (32 - prefix)) - 2;
                var self = addr;
                var hosts = Enumerable.Range(1, count)
                    .Select(i => network + (uint)i)
                    .Where(a => a != self)
                    .Select(a => new IPAddress(BitConverter.GetBytes(a).Reverse().ToArray()));
                var netIp = new IPAddress(BitConverter.GetBytes(network).Reverse().ToArray());
                list.Add(new Network(ip, prefix, hosts, $"{netIp}/{prefix}"));
            }
        }
        return list;
    }
}
