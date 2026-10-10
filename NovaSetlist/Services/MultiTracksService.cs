using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaSetlist.Services;

/// <summary>
/// Reads Playback song names from the church's MultiTracks account through the official
/// MultiTracks MCP server (https://mcp.multitracks.com/mcp). Playback's on-the-wire song ID
/// is MultiTracks' setlistSongID, which the MCP returns together with the song's title —
/// catalogue songs and the church's own cloud uploads alike.
///
/// Sign-in is standard OAuth 2.0 (authorization code + PKCE, loopback redirect) against
/// account.multitracks.com. MultiTracks issues the OAuth client ID; this app has none built
/// in, so nothing here works until one is entered in Settings. Tokens are stored DPAPI-
/// encrypted per Windows user. All MCP calls made here are read-only tools.
/// </summary>
public sealed class MultiTracksService
{
    public const string DefaultAuthority = "https://account.multitracks.com/";
    public const string DefaultMcpUrl = "https://mcp.multitracks.com/mcp";
    private const string Scope = "mcp offline_access";
    private const string McpProtocolVersion = "2025-03-26";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "multitracks-token.bin");
    private static string ReferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "multitracks-setlists.json");

    private readonly AppConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _refreshToken;
    private string? _accessToken;
    private DateTime _accessExpiresUtc;
    private string? _sessionId;
    private int _rpcId;

    public MultiTracksService(AppConfig config)
    {
        _config = config;
        LoadTokens();
    }

    private string ClientId => _config.MultiTracksClientId.Trim();
    private string ClientSecret => _config.MultiTracksClientSecret.Trim();
    private string Authority
    {
        get
        {
            var a = _config.MultiTracksAuthority.Trim();
            if (a.Length == 0) a = DefaultAuthority;
            return a.EndsWith('/') ? a : a + "/";
        }
    }
    private string McpUrl => _config.MultiTracksMcpUrl.Trim().Length > 0 ? _config.MultiTracksMcpUrl.Trim() : DefaultMcpUrl;

    /// <summary>A client ID has been entered — sign-in is possible.</summary>
    public bool IsConfigured => ClientId.Length > 0;

    /// <summary>A usable token is stored (a refresh token, or an access token that hasn't expired).</summary>
    public bool IsSignedIn => _refreshToken is not null ||
                              (_accessToken is not null && DateTime.UtcNow < _accessExpiresUtc);

    // ---------- sign in ----------

    public async Task SignInAsync(CancellationToken cancel = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Enter the MultiTracks OAuth client ID first (Settings → MultiTracks account).");

        var (authorizeEndpoint, tokenEndpoint) = await DiscoverAsync(cancel);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/";
            var url = authorizeEndpoint + (authorizeEndpoint.Contains('?') ? "&" : "?") + string.Join("&",
                "client_id=" + Uri.EscapeDataString(ClientId),
                "redirect_uri=" + Uri.EscapeDataString(redirect),
                "response_type=code",
                "scope=" + Uri.EscapeDataString(Scope),
                "code_challenge=" + challenge,
                "code_challenge_method=S256",
                "state=" + state);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var query = await OAuthLoopback.WaitForRedirectAsync(listener, state, TimeSpan.FromMinutes(3), cancel,
                "Signed in to MultiTracks", "You can close this tab and go back to Audio Buddy.");
            if (query.TryGetValue("error", out var err))
                throw new InvalidOperationException(err == "access_denied"
                    ? "Sign-in was cancelled in the browser."
                    : $"MultiTracks sign-in failed ({err}).");
            if (!query.TryGetValue("code", out var code))
                throw new InvalidOperationException("MultiTracks sign-in failed — no code returned.");

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
            };
            if (ClientSecret.Length > 0)
                form["client_secret"] = ClientSecret;
            using var resp = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancel);
            var json = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"MultiTracks rejected the sign-in (HTTP {(int)resp.StatusCode}): {ErrorText(json)}");
            ApplyTokenResponse(json);
            SaveTokens();
        }
        finally
        {
            listener.Stop();
        }
    }

    public void SignOut()
    {
        _refreshToken = null;
        _accessToken = null;
        _sessionId = null;
        try { File.Delete(TokenPath); } catch { /* nothing to delete */ }
    }

    /// <summary>Authorization-server metadata (RFC 8414); falls back to the IdentityServer defaults.</summary>
    private async Task<(string Authorize, string Token)> DiscoverAsync(CancellationToken cancel)
    {
        try
        {
            using var resp = await Http.GetAsync(Authority + ".well-known/oauth-authorization-server", cancel);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancel));
                var a = doc.RootElement.TryGetProperty("authorization_endpoint", out var ae) ? ae.GetString() : null;
                var t = doc.RootElement.TryGetProperty("token_endpoint", out var te) ? te.GetString() : null;
                if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(t))
                    return (a, t);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // fall through to the conventional endpoints
        }
        return (Authority + "connect/authorize", Authority + "connect/token");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromMinutes(1))
                return _accessToken;
            if (_refreshToken is null)
                throw new InvalidOperationException("MultiTracks sign-in has expired — sign in again in Settings.");

            var (_, tokenEndpoint) = await DiscoverAsync(cancel);
            var usedToken = _refreshToken;
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = usedToken,
                ["client_id"] = ClientId,
            };
            if (ClientSecret.Length > 0)
                form["client_secret"] = ClientSecret;
            using var resp = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancel);
            var json = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_grant", StringComparison.Ordinal))
                {
                    if (ReferenceEquals(_refreshToken, usedToken))
                        SignOut();
                    throw new InvalidOperationException("MultiTracks sign-in has expired — sign in again in Settings.");
                }
                throw new InvalidOperationException($"MultiTracks token refresh failed (HTTP {(int)resp.StatusCode}).");
            }
            ApplyTokenResponse(json);
            SaveTokens();
            return _accessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } token)
            throw new InvalidOperationException("MultiTracks' reply didn't include an access token — try again.");
        _accessToken = token;
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;
        _accessExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rt)
            _refreshToken = rt;
        _sessionId = null; // a new token means a new MCP session
    }

    private static string ErrorText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_description", out var d)) return d.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? "";
        }
        catch (JsonException) { }
        return json.Length > 160 ? json[..160] : json;
    }

    // ---------- MCP (Streamable HTTP JSON-RPC) ----------

    private async Task EnsureSessionAsync(CancellationToken cancel)
    {
        if (_sessionId is not null)
            return;
        var init = new
        {
            jsonrpc = "2.0",
            id = ++_rpcId,
            method = "initialize",
            @params = new
            {
                protocolVersion = McpProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "Audio Buddy", version = UpdateService.Format(UpdateService.CurrentVersion) },
            },
        };
        var (result, session) = await PostRpcAsync(init, _rpcId, cancel);
        _sessionId = session ?? "";
        if (result.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The MultiTracks MCP server didn't complete the handshake.");
        await PostRpcAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, null, cancel);
    }

    /// <summary>Calls one MCP tool and returns the JSON its text content carries.</summary>
    public async Task<JsonDocument> CallToolAsync(string tool, object arguments, CancellationToken cancel)
    {
        await EnsureSessionAsync(cancel);
        var id = ++_rpcId;
        var req = new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments } };
        var (result, _) = await PostRpcAsync(req, id, cancel);

        if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
        {
            var msg = FirstText(result);
            throw new MultiTracksToolException(ExtractCode(msg), $"MultiTracks tool {tool} failed: {msg}");
        }
        var text = FirstText(result);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"MultiTracks tool {tool} returned something that isn't JSON.");
        }
    }

    private static string FirstText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
            }
        }
        if (result.TryGetProperty("structuredContent", out var sc))
            return sc.GetRawText();
        return "";
    }

    /// <summary>POSTs one JSON-RPC message; returns the matching result (or Undefined for a
    /// notification) and any Mcp-Session-Id the server assigned. Retries once after a 401
    /// (fresh token) or a 404 on a stale session.</summary>
    private async Task<(JsonElement Result, string? Session)> PostRpcAsync(object message, int? expectId, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancel));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpProtocolVersion);
            if (!string.IsNullOrEmpty(_sessionId))
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            request.Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _accessToken = null; // expired or revoked server-side — refresh and retry once
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound && _sessionId is not null && attempt == 0)
            {
                _sessionId = null; // server forgot the session — start a new one
                await EnsureSessionAsync(cancel);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"MultiTracks MCP said HTTP {(int)response.StatusCode}.");

            var session = response.Headers.TryGetValues("Mcp-Session-Id", out var vals) ? vals.FirstOrDefault() : null;
            if (expectId is null)
                return (default, session); // notification: 202 Accepted, no body to read

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            var body = await response.Content.ReadAsStringAsync(cancel);
            var payload = mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
                ? ExtractSseMessage(body, expectId.Value)
                : body;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : err.GetRawText();
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : ExtractCode(msg);
                throw new MultiTracksToolException(code, $"MultiTracks MCP error: {msg}");
            }
            return (root.TryGetProperty("result", out var res) ? res.Clone() : default, session);
        }
    }

    /// <summary>Tool errors come back as text ("... -32009 ..."); pull the JSON-RPC code out if present.</summary>
    private static int ExtractCode(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"-32\d{3}");
        return m.Success && int.TryParse(m.Value, out var code) ? code : 0;
    }

    /// <summary>Picks the JSON-RPC response with the given id out of a text/event-stream body.</summary>
    private static string ExtractSseMessage(string body, int id)
    {
        string? last = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            last = data;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt32(out var v) && v == id)
                    return data;
            }
            catch (JsonException) { }
        }
        return last ?? throw new InvalidOperationException("MultiTracks MCP sent an empty reply.");
    }

    // ---------- the names fetch ----------

    public sealed record SetlistSong(int Order, long SetlistSongId, string Title, string Key, double Bpm, int ContentType, long ContentId, double Duration = 0);
    public sealed record Setlist(long SetlistId, string Name, int Version, string Date, List<SetlistSong> Songs);
    public sealed record NamesResult(Dictionary<long, string> Titles, List<Setlist> Setlists, int Unresolved);

    private static readonly Dictionary<int, string> KeyNames = new()
    {
        [1] = "A", [2] = "Am", [3] = "Ab", [6] = "B", [7] = "Bm", [8] = "Bb", [9] = "Bbm", [10] = "C", [11] = "Cm",
        [13] = "C#", [14] = "C#m", [15] = "D", [16] = "Dm", [17] = "Db", [20] = "E", [21] = "Em", [22] = "Eb",
        [23] = "Ebm", [24] = "F", [25] = "Fm", [27] = "F#m", [28] = "G", [29] = "Gm", [30] = "Gb", [32] = "G#m",
    };

    /// <summary>
    /// Recent and upcoming setlists → every song's Playback ID with its title. Four read-only
    /// tools: setlistsList, setlistGetBatch (≤20 per call), songGetBatch (≤25 per call) for
    /// catalogue songs, cloudSongsList for the church's uploads (contentType 18).
    /// </summary>
    public async Task<NamesResult> FetchNamesAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        if (!IsSignedIn)
            throw new InvalidOperationException("Sign in to MultiTracks in Settings first.");

        progress?.Report("listing setlists…");
        var today = DateTime.Today;
        var listed = new List<(long Id, string Name, int Version, string Date)>();
        using (var doc = await CallToolAsync("setlistsList", new
        {
            fromDate = today.AddDays(-90).ToString("yyyy-MM-dd"),
            toDate = today.AddDays(180).ToString("yyyy-MM-dd"),
            pageSize = 200,
            pageNumber = 1,
        }, cancel))
        {
            foreach (var s in Items(doc.RootElement))
                listed.Add((Long(s, "setlistID"), Str(s, "name"), Int(s, "version"), Str(s, "date")));
        }

        var setlists = new List<Setlist>();
        var catalogIds = new HashSet<long>();
        for (var i = 0; i < listed.Count; i += 20)
        {
            progress?.Report($"reading setlists {i + 1}–{Math.Min(i + 20, listed.Count)} of {listed.Count}…");
            var chunk = listed.Skip(i).Take(20).ToList();
            using var doc = await CallToolAsync("setlistGetBatch", new { setlistIDs = chunk.Select(c => c.Id).ToArray() }, cancel);
            foreach (var sl in Items(doc.RootElement))
            {
                var id = Long(sl, "setlistID");
                var meta = chunk.FirstOrDefault(c => c.Id == id);
                var songs = new List<SetlistSong>();
                if (sl.TryGetProperty("songs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        var ct = Int(s, "contentType");
                        var cid = Long(s, "contentID");
                        if (ct != 18)
                            catalogIds.Add(cid);
                        songs.Add(new SetlistSong(Int(s, "order"), Long(s, "setlistSongID"), "",
                            KeyNames.TryGetValue(Int(s, "keyID"), out var k) ? k : "", Dbl(s, "bpm"), ct, cid));
                    }
                }
                setlists.Add(new Setlist(id, Str(sl, "name", meta.Name), Int(sl, "version"), Str(sl, "date", meta.Date),
                    songs.OrderBy(x => x.Order).ToList()));
            }
        }

        var catalogTitles = new Dictionary<long, string>();
        var ids = catalogIds.ToList();
        for (var i = 0; i < ids.Count; i += 25)
        {
            progress?.Report($"looking up catalogue songs {i + 1}–{Math.Min(i + 25, ids.Count)} of {ids.Count}…");
            using var doc = await CallToolAsync("songGetBatch", new { mtIDs = ids.Skip(i).Take(25).ToArray() }, cancel);
            foreach (var s in Items(doc.RootElement))
                catalogTitles[Long(s, "mtID")] = Str(s, "title");
        }

        var cloudTitles = new Dictionary<long, string>();
        for (var page = 1; page < 50; page++)
        {
            progress?.Report($"listing cloud songs (page {page})…");
            using var doc = await CallToolAsync("cloudSongsList", new { pageSize = 200, pageNumber = page, query = "" }, cancel);
            var count = 0;
            foreach (var s in Items(doc.RootElement))
            {
                count++;
                cloudTitles[Long(s, "cloudID")] = Str(s, "title");
            }
            if (count < 200)
                break;
        }

        // Playback's real play length per setlist entry (trims included) — one call per song,
        // so only for setlists around now. These drive the Now Playing countdown.
        var durations = new Dictionary<long, double>();
        var near = setlists.Where(sl => DateTime.TryParse(sl.Date, out var d) && d >= today.AddDays(-DurationDaysBack) && d <= today.AddDays(DurationDaysAhead))
                           .SelectMany(sl => sl.Songs.Select(s => (sl.SetlistId, s.SetlistSongId)))
                           .Take(MaxDurationCalls).ToList();
        for (var i = 0; i < near.Count; i++)
        {
            progress?.Report($"reading song lengths {i + 1} of {near.Count}…");
            try
            {
                using var doc = await CallToolAsync("setlistSongDurationGet", new { setlistID = near[i].SetlistId, setlistSongID = near[i].SetlistSongId }, cancel);
                var secs = Dbl(doc.RootElement, "durationSeconds");
                if (secs > 0)
                    durations[near[i].SetlistSongId] = secs;
            }
            catch (InvalidOperationException)
            {
                // unknown length for this one — the sheet's Length column covers it
            }
        }

        var titles = new Dictionary<long, string>();
        var unresolved = 0;
        var resolved = new List<Setlist>();
        foreach (var sl in setlists)
        {
            var songs = new List<SetlistSong>();
            foreach (var s in sl.Songs)
            {
                string? title = s.ContentType == 18
                    ? (cloudTitles.TryGetValue(s.ContentId, out var ct) ? ct : null)
                    : (catalogTitles.TryGetValue(s.ContentId, out var mt) ? mt : null);
                if (string.IsNullOrWhiteSpace(title))
                {
                    unresolved++;
                    title = $"Song {s.ContentId}";
                }
                else
                {
                    titles[s.SetlistSongId] = title.Trim();
                }
                songs.Add(s with { Title = title, Duration = durations.TryGetValue(s.SetlistSongId, out var dur) ? dur : 0 });
            }
            resolved.Add(sl with { Songs = songs });
        }

        try
        {
            var reference = new
            {
                generatedFrom = "MultiTracks MCP (Audio Buddy)",
                generated = DateTime.Now.ToString("s"),
                setlists = resolved.Select(sl => new
                {
                    setlistID = sl.SetlistId, name = sl.Name, version = sl.Version, date = sl.Date,
                    songs = sl.Songs.Select(s => new { order = s.Order, setlistSongID = s.SetlistSongId, title = s.Title, key = s.Key, bpm = s.Bpm, durationSeconds = s.Duration, contentType = s.ContentType, contentID = s.ContentId }),
                }),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ReferencePath)!);
            File.WriteAllText(ReferencePath, JsonSerializer.Serialize(reference, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // the reference file is a convenience; the map is what matters
        }
        return new NamesResult(titles, resolved, unresolved);
    }

    private const int DurationDaysBack = 7, DurationDaysAhead = 21, MaxDurationCalls = 80;

    // ---------- SMPTE settings on a setlist (writes) ----------

    /// <summary>Current version of a setlist — every write needs it and bumps it.</summary>
    public async Task<int> GetSetlistVersionAsync(long setlistId, CancellationToken cancel)
    {
        using var doc = await CallToolAsync("setlistGet", new { setlistIDOrName = setlistId.ToString() }, cancel);
        var v = Int(doc.RootElement, "version");
        if (v <= 0)
            throw new InvalidOperationException($"MultiTracks didn't return a version for setlist {setlistId}.");
        return v;
    }

    /// <summary>Sets a setlist song's own SMPTE settings: output on/off, start position in
    /// seconds and timeline offset in samples (−1 leaves either unchanged). Returns the new
    /// setlist version. Throws <see cref="MultiTracksToolException"/> (code −32009) if the
    /// setlist moved on since <paramref name="expectedVersion"/> — re-read and retry.</summary>
    public Task<int> SetSmpteAsync(long setlistId, long setlistSongId, int expectedVersion,
        bool enabled, double startTime, long timelineLocation, CancellationToken cancel) =>
        WriteAsync("setlistSongSetSMPTE", new
        {
            setlistID = setlistId, setlistSongID = setlistSongId, expectedVersion,
            enabled, startTime, timelineLocation,
        }, setlistId, expectedVersion, cancel);

    /// <summary>True = the song's SMPTE settings come from its default arrangement (per-setlist
    /// overrides are bypassed); false = this setlist entry's own values apply.</summary>
    public Task<int> SetSmpteFollowsDefaultAsync(long setlistId, long setlistSongId, int expectedVersion,
        bool follows, CancellationToken cancel) =>
        WriteAsync("setlistSongSetSMPTEFollowsDefault", new
        {
            setlistID = setlistId, setlistSongID = setlistSongId, expectedVersion, follows,
        }, setlistId, expectedVersion, cancel);

    private async Task<int> WriteAsync(string tool, object args, long setlistId, int expectedVersion, CancellationToken cancel)
    {
        using var doc = await CallToolAsync(tool, args, cancel);
        var root = doc.RootElement;
        // The reply normally carries the new version; if not, read it back.
        var v = root.ValueKind == JsonValueKind.Object ? Int(root, "version") : 0;
        if (v <= 0 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("setlist", out var sl))
            v = Int(sl, "version");
        return v > expectedVersion ? v : await GetSetlistVersionAsync(setlistId, cancel);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? n : 0;
    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    // ---------- token store ----------

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            _refreshToken = doc.RootElement.TryGetProperty("refresh", out var r) ? r.GetString() : null;
            _accessToken = doc.RootElement.TryGetProperty("access", out var a) ? a.GetString() : null;
            _accessExpiresUtc = doc.RootElement.TryGetProperty("expiresUtc", out var x) && x.TryGetDateTime(out var dt) ? dt : DateTime.MinValue;
        }
        catch
        {
            _refreshToken = null;
            _accessToken = null;
        }
    }

    private void SaveTokens()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { refresh = _refreshToken, access = _accessToken, expiresUtc = _accessExpiresUtc });
            var tmp = TokenPath + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(tmp, TokenPath, overwrite: true);
        }
        catch
        {
            // sign-in still works for this run
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>An MCP tool or JSON-RPC error, with the server's code when it gave one
/// (−32009 = the setlist changed since it was read; −32002 = not yours / not found).</summary>
public sealed class MultiTracksToolException : InvalidOperationException
{
    public int Code { get; }
    public MultiTracksToolException(int code, string message) : base(message) => Code = code;
    public bool IsVersionConflict => Code == -32009 || Message.Contains("modified since", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loopback-redirect helper shared by the OAuth sign-ins: accepts connections on a
/// local TcpListener until one carries the authorization response, answers it with a small
/// page, and returns the query parameters. Idle or reset connections are tolerated.</summary>
internal static class OAuthLoopback
{
    public static async Task<Dictionary<string, string>> WaitForRedirectAsync(
        TcpListener listener, string expectedState, TimeSpan timeout, CancellationToken cancel,
        string heading, string line)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Timed out waiting for the browser — no sign-in completed within 3 minutes.");
            }

            using (client)
            {
                var stream = client.GetStream();
                string requestLine;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    requestLine = await ReadRequestLineAsync(stream, readCts.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !cts.IsCancellationRequested)
                {
                    continue;
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Timed out waiting for the browser — no sign-in completed within 3 minutes.");
                }

                var query = ParseQuery(requestLine);
                var isResponse = query.ContainsKey("code") || query.ContainsKey("error");
                var body = isResponse
                    ? "<html><body style=\"background:#16181d;color:#e8e8e8;font-family:Segoe UI,sans-serif;" +
                      "display:flex;align-items:center;justify-content:center;height:95vh\"><div style=\"text-align:center\">" +
                      $"<div style=\"font-size:40px;color:#3fb950\">&#10003;</div><h2>{heading}</h2>" +
                      $"<p style=\"color:#9a9fa8\">{line}</p></div></body></html>"
                    : "";
                var status = isResponse ? "200 OK" : "404 Not Found";
                var bytes = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);
                try { stream.Write(bytes); } catch { /* browser closed early */ }

                if (!isResponse)
                    continue;
                if (!query.TryGetValue("state", out var s) || s != expectedState)
                    throw new InvalidOperationException("Sign-in failed — the response didn't match this app's request.");
                return query;
            }
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancel)
    {
        var sb = new StringBuilder(512);
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(one, cancel);
            if (n <= 0) break;
            var b = one[0];
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string requestLine)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return result;
        var q = parts[1].IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }
}
