using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using NovaSetlist.Models;

namespace NovaSetlist.Services;

/// <summary>
/// Reads the master Songs and Leaders lists from a Google Sheet. Signed in to
/// Google → the official Sheets API v4 (works on private sheets); not signed
/// in → the public gviz CSV endpoint (sheet must be "Anyone with the link").
/// </summary>
public sealed class SheetService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly GoogleAuthService _auth;

    public SheetService(GoogleAuthService auth) => _auth = auth;

    public async Task<(List<Song> Songs, List<string> Leaders)> FetchAsync(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SpreadsheetId) || config.SpreadsheetId == "PUT_ID_HERE")
            throw new InvalidOperationException("No Spreadsheet ID set — open Settings and paste your sheet's ID or URL.");

        // Both tabs in flight at once — halves refresh latency. WhenAll observes
        // both outcomes, so one tab failing doesn't leave the other's exception dangling.
        var songsTask = FetchTabAsync(config.SpreadsheetId, config.SongsTab);
        var leadersTask = FetchTabAsync(config.SpreadsheetId, config.LeadersTab);
        await Task.WhenAll(songsTask, leadersTask);
        var songsRows = songsTask.Result;
        var leadersRows = leadersTask.Result;

        var songs = songsRows
            .Select(r => new Song
            {
                Name = Cell(r, 0),
                DefaultKey = Music.Keys.Normalize(Cell(r, 1)),
                Length = Cell(r, 2),
                Bpm = Cell(r, 3),
                Chromatic = IsTruthy(Cell(r, 4)),
                KeyChangeKey = Music.Keys.Normalize(Cell(r, 5)),
                KeyChangeAt = Cell(r, 6),
            })
            .Where(s => s.Name.Length > 0)
            .ToList();

        var leaders = leadersRows
            .Select(r => Cell(r, 0))
            .Where(n => n.Length > 0)
            .ToList();

        return (songs, leaders);
    }

    /// <summary>Fetches one tab as rows of trimmed cells, header row already skipped.</summary>
    private async Task<List<string[]>> FetchTabAsync(string spreadsheetId, string tab)
    {
        if (_auth.IsSignedIn)
            return await FetchTabApiAsync(spreadsheetId, tab);
        return ParseRows(await FetchTabCsvAsync(spreadsheetId, tab));
    }

    // ---------- signed in: Sheets API v4 ----------

    private async Task<List<string[]>> FetchTabApiAsync(string spreadsheetId, string tab)
    {
        // Whole tab as one range; single quotes in a tab name are escaped by doubling.
        var range = "'" + tab.Replace("'", "''") + "'";
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}" +
                  $"/values/{Uri.EscapeDataString(range)}";

        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url));

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"{_auth.Email} doesn't have access to this sheet — share the sheet with that Google account.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Spreadsheet not found — check the ID in Settings.");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new InvalidOperationException($"Tab '{tab}' wasn't found in the sheet — check the tab name in Settings.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = new List<string[]>();
        if (!doc.RootElement.TryGetProperty("values", out var values))
            return rows; // empty tab

        var first = true;
        foreach (var row in values.EnumerateArray())
        {
            if (first) { first = false; continue; } // header row
            rows.Add(row.EnumerateArray().Select(CellText).ToArray());
        }
        return rows;
    }

    /// <summary>
    /// Writes a song's Chromatic / Key Change / Key Change At cells (columns E:G),
    /// locating the row by name at write time so a reordered sheet can't be hit
    /// in the wrong place. A song missing from the sheet is appended in full.
    /// Requires Google sign-in.
    /// </summary>
    public async Task UpdateSongExtrasAsync(AppConfig config, string name, string key,
        bool chromatic, string keyChangeKey, string keyChangeAt)
    {
        var row = await FindRowAsync(config, name);

        // Both paths touch columns E:G, which a plain 4-column Songs tab doesn't have yet.
        await EnsureExtraColumnsAsync(config);

        if (row < 0)
        {
            await AppendRowAsync(config, new object[] { name, key, "", "", chromatic, keyChangeKey, keyChangeAt });
            return;
        }
        await PutValuesAsync(config, $"E{row}:G{row}", new object[] { chromatic, keyChangeKey, keyChangeAt });
    }

    /// <summary>
    /// Manual add: appends the song (name + key) if the sheet doesn't have it and returns null;
    /// if it's already there, returns its Chromatic / Key Change / At cells so the row can adopt
    /// them — another machine's details must never be overwritten by a plain add. Works on a
    /// sheet without the E:G columns (nothing beyond B is written).
    /// </summary>
    public async Task<(bool Chromatic, string KeyChangeKey, string KeyChangeAt)?> EnsureSongAsync(
        AppConfig config, string name, string key)
    {
        var row = await FindRowAsync(config, name);
        if (row < 0)
        {
            await AppendRowAsync(config, new object[] { name, key });
            return null;
        }

        string[] cells;
        try
        {
            var got = await GetRangeAsync(config, $"E{row}:G{row}");
            cells = got.Count > 0 ? got[0] : Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            cells = Array.Empty<string>(); // no E:G columns yet — nothing to adopt
        }
        return (IsTruthy(Cell(cells, 0)), Music.Keys.Normalize(Cell(cells, 1)), Cell(cells, 2));
    }

    /// <summary>1-based sheet row of the song, matched by trimmed name (case-insensitive); -1 if absent.</summary>
    private async Task<int> FindRowAsync(AppConfig config, string name)
    {
        var names = await GetColumnAsync(config, "A");
        for (var i = 1; i < names.Count; i++) // skip the header row
        {
            if (string.Equals(names[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return -1;
    }

    private static readonly string[] ExtraHeaders = { "Chromatic", "Key Change", "Key Change At" };
    private (string Id, string Tab)? _columnsCheckedFor;
    private Task? _columnsCheck;

    /// <summary>
    /// Once per run per sheet/tab: widens the Songs tab to at least 7 columns (Google refuses
    /// to read OR write a range past the grid edge — "exceeds grid limits") and labels E1:G1
    /// if they're blank. If E:G already hold something else, refuses rather than overwrite it.
    /// Concurrent callers share one in-flight check.
    /// </summary>
    private async Task EnsureExtraColumnsAsync(AppConfig config)
    {
        var key = (config.SpreadsheetId, config.SongsTab);
        if (_columnsCheckedFor == key)
            return;
        if (_columnsCheck is null || _columnsCheck.IsCompleted)
            _columnsCheck = CheckColumnsAsync(config, key);
        await _columnsCheck;
    }

    private async Task CheckColumnsAsync(AppConfig config, (string, string) key)
    {
        var (sheetId, columnCount) = await GetSheetGridAsync(config);
        if (columnCount < 7)
            await AppendColumnsAsync(config, sheetId, 7 - columnCount);

        var header = await GetRangeAsync(config, "E1:G1");
        var cells = header.Count > 0 ? header[0] : Array.Empty<string>();
        if (cells.All(c => c.Trim().Length == 0))
        {
            await PutValuesAsync(config, "E1:G1", ExtraHeaders.Cast<object>().ToArray());
        }
        else
        {
            for (var i = 0; i < ExtraHeaders.Length; i++)
            {
                var have = i < cells.Length ? cells[i].Trim() : "";
                if (have.Length > 0 && !string.Equals(have, ExtraHeaders[i], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Columns E–G of '{config.SongsTab}' are already used for '{have}' — key details can't be synced until they're free or renamed to Chromatic / Key Change / Key Change At.");
            }
        }
        _columnsCheckedFor = key;
    }

    /// <summary>Numeric id and current column count of the Songs tab (spreadsheet metadata).</summary>
    private async Task<(long SheetId, int ColumnCount)> GetSheetGridAsync(AppConfig config)
    {
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(config.SpreadsheetId)}" +
                  "?fields=sheets.properties(sheetId,title,gridProperties.columnCount)";
        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        await ThrowIfFailedAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var sheet in doc.RootElement.GetProperty("sheets").EnumerateArray())
        {
            var props = sheet.GetProperty("properties");
            if (!string.Equals(props.GetProperty("title").GetString(), config.SongsTab, StringComparison.OrdinalIgnoreCase))
                continue;
            var cols = props.TryGetProperty("gridProperties", out var grid) &&
                       grid.TryGetProperty("columnCount", out var cc) ? cc.GetInt32() : 0;
            return (props.GetProperty("sheetId").GetInt64(), cols);
        }
        throw new InvalidOperationException($"Tab '{config.SongsTab}' wasn't found in the sheet — check the tab name in Settings.");
    }

    private async Task AppendColumnsAsync(AppConfig config, long sheetId, int count)
    {
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(config.SpreadsheetId)}:batchUpdate";
        var body = JsonSerializer.Serialize(new
        {
            requests = new[]
            {
                new { appendDimension = new { sheetId, dimension = "COLUMNS", length = count } },
            },
        });
        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        await ThrowIfFailedAsync(response);
    }

    /// <summary>Sends with a bearer token; on 401 (token revoked server-side while still
    /// within its hour) drops the cached token and retries once with a fresh one.</summary>
    private async Task<HttpResponseMessage> SendAuthedAsync(Func<HttpRequestMessage> make)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = make();
            request.Headers.Authorization = new("Bearer", await _auth.GetAccessTokenAsync());
            var response = await Http.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;
            response.Dispose();
            _auth.InvalidateAccessToken();
        }
    }

    private async Task<List<string>> GetColumnAsync(AppConfig config, string column)
    {
        var rows = await GetRangeAsync(config, $"{column}:{column}");
        return rows.Select(r => r.Length > 0 ? r[0] : "").ToList();
    }

    private async Task<List<string[]>> GetRangeAsync(AppConfig config, string a1)
    {
        var url = ValuesUrl(config, a1);
        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        await ThrowIfFailedAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = new List<string[]>();
        if (!doc.RootElement.TryGetProperty("values", out var values))
            return rows;
        foreach (var row in values.EnumerateArray())
            rows.Add(row.EnumerateArray().Select(CellText).ToArray());
        return rows;
    }

    // RAW, not USER_ENTERED: typed-in "1:45" would become a Sheets time value (formatted
    // "1:45:00 AM" on read-back) and a name starting with "=" would become a formula. Booleans
    // are sent as JSON true/false, which RAW stores as real checkbox-compatible booleans.
    private async Task PutValuesAsync(AppConfig config, string a1, object[] cells)
    {
        var url = ValuesUrl(config, a1) + "?valueInputOption=RAW";
        var body = JsonSerializer.Serialize(new { values = new[] { cells } });
        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        await ThrowIfFailedAsync(response);
    }

    private async Task AppendRowAsync(AppConfig config, object[] cells)
    {
        var url = ValuesUrl(config, "A:G") + ":append?valueInputOption=RAW&insertDataOption=INSERT_ROWS";
        var body = JsonSerializer.Serialize(new { values = new[] { cells } });
        using var response = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        await ThrowIfFailedAsync(response);
    }

    /// <summary>…/values/'Songs'!{a1} — the tab name quoted, single quotes doubled.</summary>
    private static string ValuesUrl(AppConfig config, string a1)
    {
        var range = "'" + config.SongsTab.Replace("'", "''") + "'!" + a1;
        return $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(config.SpreadsheetId)}" +
               $"/values/{Uri.EscapeDataString(range)}";
    }

    /// <summary>Turns a failed Sheets response into a readable error: a friendly line for
    /// "no edit access", otherwise Google's own message with the status code.</summary>
    private async Task ThrowIfFailedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"Google refused (403) — check {_auth.Email} can edit the sheet, or sign out and back in to grant edit access.");

        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                detail = msg.GetString() ?? "";
        }
        catch
        {
            // Not JSON — the status code alone will have to do.
        }
        throw new InvalidOperationException(
            $"Google Sheets said {(int)response.StatusCode}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    private static string CellText(JsonElement c) =>
        c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString();

    /// <summary>Sheet-style truthiness for the Chromatic column: TRUE / yes / y / 1 / x / ✓.</summary>
    private static bool IsTruthy(string cell) =>
        cell.Trim().ToUpperInvariant() is "TRUE" or "YES" or "Y" or "1" or "X" or "✓" or "CHROMATIC";

    // ---------- not signed in: public gviz CSV ----------

    private static async Task<string> FetchTabCsvAsync(string spreadsheetId, string tab)
    {
        // headers=1: without it Google GUESSES how many rows are headers, and a
        // mostly-empty column (e.g. a fresh BPM column) can make it swallow the
        // whole sheet as one giant multi-row header, returning almost no songs.
        var url = $"https://docs.google.com/spreadsheets/d/{Uri.EscapeDataString(spreadsheetId)}" +
                  $"/gviz/tq?tqx=out:csv&headers=1&sheet={Uri.EscapeDataString(tab)}";
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();

        // If the sheet isn't shared "Anyone with the link", Google returns an HTML sign-in page.
        if (text.TrimStart().StartsWith("<", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Tab '{tab}' returned a web page, not CSV — the sheet is probably private. " +
                "Sign in with Google in Settings, or share it \"Anyone with the link: Viewer\".");

        return text;
    }

    /// <summary>Parses CSV into rows of trimmed cells, skipping the header row.</summary>
    private static List<string[]> ParseRows(string csv)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
        };

        var rows = new List<string[]>();
        using var reader = new CsvReader(new StringReader(csv), config);
        var first = true;
        while (reader.Read())
        {
            if (first) { first = false; continue; } // header row
            rows.Add(reader.Parser.Record ?? Array.Empty<string>());
        }
        return rows;
    }

    private static string Cell(string[] row, int index) =>
        index < row.Length ? (row[index] ?? "").Trim() : "";
}
