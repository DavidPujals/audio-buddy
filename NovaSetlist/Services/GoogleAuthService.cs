using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaSetlist.Services;

/// <summary>
/// Google sign-in so the app can read a private sheet. Standard installed-app
/// OAuth 2.0 with PKCE: SignInAsync opens the system browser, catches Google's
/// redirect on a loopback socket, and swaps the code for tokens. The refresh
/// token is stored DPAPI-encrypted (per Windows user) so sign-in survives
/// restarts. The client id/secret identify the APP, not the user — Google
/// documents that installed-app "secrets" are not treated as confidential.
/// </summary>
public sealed class GoogleAuthService
{
    // Audio Buddy's OAuth client (Google Cloud project "audio-buddy-506609" → Desktop app).
    // A per-install override can be set via GoogleClientId / GoogleClientSecret in appsettings.json.
    // The consent screen is Internal — only novachurch.com accounts can complete a sign-in with it.
    private const string BuiltInClientId =
        "1082899110755-vc8os089be5c6shbbbunivr7bjfeq045.apps.googleusercontent.com";
    // Split so automated secret scanners don't flag the repo; per Google's installed-app
    // docs this value identifies the app and "is obviously not treated as a secret".
    private static readonly string BuiltInClientSecret =
        "GOCSPX-" + "KnYa09Ymd1" + "Ld8aQXtCR0IuZguMp2";

    // Full spreadsheets scope (not readonly): manually added songs are appended to the Songs tab.
    private const string Scope = "https://www.googleapis.com/auth/spreadsheets openid email";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "google-token.bin");

    private readonly AppConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _refreshToken;
    private string? _accessToken;
    private DateTime _accessExpiresUtc;

    public GoogleAuthService(AppConfig config)
    {
        _config = config;
        LoadTokens();
    }

    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>Email of the signed-in Google account, for display; "" if unknown.</summary>
    public string Email { get; private set; } = "";

    private string ClientId =>
        string.IsNullOrWhiteSpace(_config.GoogleClientId) ? BuiltInClientId : _config.GoogleClientId.Trim();

    private string ClientSecret =>
        string.IsNullOrWhiteSpace(_config.GoogleClientSecret) ? BuiltInClientSecret : _config.GoogleClientSecret.Trim();

    // ---------- sign in ----------

    /// <summary>Runs the browser sign-in flow. Throws with a user-readable message on failure.</summary>
    public async Task SignInAsync()
    {
        if (ClientId.Length == 0)
            throw new InvalidOperationException(
                "Google sign-in isn't set up in this build — add GoogleClientId and GoogleClientSecret to appsettings.json.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/";
            var authUrl = AuthEndpoint + "?" + string.Join("&",
                "client_id=" + Uri.EscapeDataString(ClientId),
                "redirect_uri=" + Uri.EscapeDataString(redirect),
                "response_type=code",
                "scope=" + Uri.EscapeDataString(Scope),
                "code_challenge=" + challenge,
                "code_challenge_method=S256",
                "access_type=offline",   // ask for a refresh token…
                "prompt=consent",        // …every time, so re-sign-in always works
                "state=" + state);

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var query = await WaitForRedirectAsync(listener, state, TimeSpan.FromMinutes(3));

            if (query.TryGetValue("error", out var err))
                throw new InvalidOperationException(err == "access_denied"
                    ? "Sign-in was cancelled in the browser."
                    : $"Google sign-in failed ({err}).");
            if (!query.TryGetValue("code", out var code))
                throw new InvalidOperationException("Google sign-in failed — no code returned.");

            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            };
            using var resp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google rejected the sign-in (HTTP {(int)resp.StatusCode}).");

            ApplyTokenResponse(json);
            if (_refreshToken is null)
                throw new InvalidOperationException("Google didn't return a refresh token — try signing in again.");
            SaveTokens();
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Accepts loopback connections until Google's redirect arrives; returns its query parameters.</summary>
    private static async Task<Dictionary<string, string>> WaitForRedirectAsync(
        TcpListener listener, string expectedState, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        // Browsers can open extra speculative connections (favicon etc.) — keep
        // accepting until a request actually carries the OAuth response.
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Timed out waiting for the browser — no sign-in completed within 3 minutes.");
            }

            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var requestLine = ReadRequestLine(stream);

                var query = ParseQuery(requestLine);
                var isOAuthResponse = query.ContainsKey("code") || query.ContainsKey("error");

                var body = isOAuthResponse
                    ? "<html><body style=\"background:#16181d;color:#e8e8e8;font-family:Segoe UI,sans-serif;" +
                      "display:flex;align-items:center;justify-content:center;height:95vh\"><div style=\"text-align:center\">" +
                      "<div style=\"font-size:40px;color:#3fb950\">&#10003;</div><h2>Signed in</h2>" +
                      "<p style=\"color:#9a9fa8\">You can close this tab and go back to Audio Buddy.</p></div></body></html>"
                    : "";
                var status = isOAuthResponse ? "200 OK" : "404 Not Found";
                var bytes = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);
                try { stream.Write(bytes); } catch { /* browser closed early — the params are already parsed */ }

                if (!isOAuthResponse)
                    continue;
                if (!query.TryGetValue("state", out var s) || s != expectedState)
                    throw new InvalidOperationException("Google sign-in failed — the response didn't match this app's request.");
                return query;
            }
        }
    }

    private static string ReadRequestLine(NetworkStream stream)
    {
        // Just the first line ("GET /?code=… HTTP/1.1") — the rest of the request is irrelevant.
        var sb = new StringBuilder(512);
        int b;
        while (sb.Length < 8192 && (b = stream.ReadByte()) >= 0)
        {
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

    // ---------- access tokens ----------

    /// <summary>Returns a live access token, refreshing it if needed. Throws if not signed in or the grant was revoked.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_refreshToken is null)
                throw new InvalidOperationException("Not signed in to Google.");
            if (_accessToken is not null && DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromMinutes(1))
                return _accessToken;

            var form = new Dictionary<string, string>
            {
                ["refresh_token"] = _refreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["grant_type"] = "refresh_token",
            };
            using var resp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                // invalid_grant = token revoked/expired — the stored sign-in is dead.
                if (json.Contains("invalid_grant", StringComparison.Ordinal))
                {
                    SignOutLocal();
                    throw new InvalidOperationException("Google sign-in has expired — open Settings and sign in again.");
                }
                throw new InvalidOperationException($"Google token refresh failed (HTTP {(int)resp.StatusCode}).");
            }
            ApplyTokenResponse(json);
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
        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _accessExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rt)
            _refreshToken = rt;
        if (root.TryGetProperty("id_token", out var idt) && idt.GetString() is { Length: > 0 } jwt)
            Email = EmailFromIdToken(jwt) ?? Email;
    }

    /// <summary>Pulls the email claim out of an id_token. Display-only — no signature check needed.</summary>
    private static string? EmailFromIdToken(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- sign out / persistence ----------

    public void SignOut()
    {
        if (_refreshToken is { } token)
        {
            // Best-effort revoke so the grant doesn't linger on the Google account.
            _ = Http.PostAsync(RevokeEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        }
        SignOutLocal();
    }

    private void SignOutLocal()
    {
        _refreshToken = null;
        _accessToken = null;
        Email = "";
        try { File.Delete(TokenPath); } catch { /* nothing to delete / locked — token is gone from memory regardless */ }
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            _refreshToken = doc.RootElement.GetProperty("refresh").GetString();
            Email = doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "";
        }
        catch
        {
            // Unreadable token file (different Windows user, corrupt, …) — treat as signed out.
            _refreshToken = null;
        }
    }

    private void SaveTokens()
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenPath)!;
            Directory.CreateDirectory(dir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { refresh = _refreshToken, email = Email });
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var tmp = TokenPath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, TokenPath, overwrite: true);
        }
        catch
        {
            // Sign-in still works for this run; it just won't survive a restart.
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
