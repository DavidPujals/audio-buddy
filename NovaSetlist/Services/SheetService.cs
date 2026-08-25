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

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", await _auth.GetAccessTokenAsync());
        using var response = await Http.SendAsync(request);

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
            rows.Add(row.EnumerateArray()
                .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString())
                .ToArray());
        }
        return rows;
    }

    /// <summary>Appends a song row (name + default key) to the Songs tab. Requires Google sign-in.</summary>
    public async Task AppendSongAsync(AppConfig config, string name, string key)
    {
        var range = "'" + config.SongsTab.Replace("'", "''") + "'!A:D";
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(config.SpreadsheetId)}" +
                  $"/values/{Uri.EscapeDataString(range)}:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new("Bearer", await _auth.GetAccessTokenAsync());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { values = new[] { new[] { name, key } } }),
            Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"Google wouldn't allow the edit — check {_auth.Email} can edit the sheet, or sign out and back in to grant edit access.");
        response.EnsureSuccessStatusCode();
    }

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
