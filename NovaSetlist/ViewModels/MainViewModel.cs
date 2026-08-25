using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSetlist.Models;
using NovaSetlist.Services;

namespace NovaSetlist.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly SheetService _sheets;
    private readonly StorageService _storage = new();

    private List<Song> _allSongs = new();
    private DateTime? _lastSynced;
    private bool _loadingCurrent; // suppress auto-save while restoring current.json

    public AppConfig Config => _config;
    public GoogleAuthService GoogleAuth { get; }
    public TimecodeViewModel Timecode { get; }
    public KeyDetectViewModel KeyDetect { get; }
    public SplViewModel Spl { get; }

    public ObservableCollection<SetItemViewModel> Items { get; } = new();
    public ObservableCollection<Song> SearchResults { get; } = new();
    public ObservableCollection<string> Leaders { get; } = new();

    public IReadOnlyList<string> StandardKeys => Music.Keys.StandardKeys;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string statusText = "Loading…";

    /// <summary>Sync-state for the status dot: "idle", "ok", "cached" or "error".</summary>
    [ObservableProperty]
    private string statusState = "idle";

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private string footerText = "0 songs";

    /// <summary>Window is below the comfortable width — rows go compact (right-click menu).</summary>
    [ObservableProperty]
    private bool isNarrow;

    /// <summary>Window is below the comfortable height — the top bar folds into the ☰ menu.</summary>
    [ObservableProperty]
    private bool isShort;

    private readonly DispatcherTimer _saveTimer;

    public MainViewModel()
    {
        Items.CollectionChanged += OnItemsChanged;
        GoogleAuth = new GoogleAuthService(_config);
        _sheets = new SheetService(GoogleAuth);
        Timecode = new TimecodeViewModel(_config);
        KeyDetect = new KeyDetectViewModel(_config);
        Spl = new SplViewModel(_config);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveCurrentNow();
        };
    }

    /// <summary>Applies sheet settings from the Settings dialog: saves appsettings.json and re-syncs.</summary>
    public async Task ApplySheetSettingsAsync(string spreadsheetId, string songsTab, string leadersTab)
    {
        _config.SpreadsheetId = SheetUrl.ExtractSpreadsheetId(spreadsheetId);
        _config.SongsTab = string.IsNullOrWhiteSpace(songsTab) ? "Songs" : songsTab.Trim();
        _config.LeadersTab = string.IsNullOrWhiteSpace(leadersTab) ? "Leaders" : leadersTab.Trim();

        if (!_config.Save())
        {
            StatusText = "Couldn't write appsettings.json — settings not saved";
            StatusState = "error";
            return;
        }
        // If a refresh is already in flight it's using the old settings — let it
        // finish, then fetch again so the new spreadsheet actually loads.
        while (IsRefreshing)
            await Task.Delay(100);
        await RefreshAsync();
    }

    // ---------- startup ----------

    public async Task InitializeAsync()
    {
        RestoreCurrentService();

        var cache = _storage.LoadCache();
        if (cache is not null)
        {
            ApplyMasterData(cache.Songs, cache.Leaders);
            _lastSynced = cache.LastSynced;
            StatusText = $"Using cached list — last synced {cache.LastSynced:g}";
            StatusState = "cached";
        }

        await RefreshAsync();
    }

    private void RestoreCurrentService()
    {
        _loadingCurrent = true;
        try
        {
            var saved = _storage.LoadCurrent();
            if (saved is null)
                return;
            foreach (var dto in saved.Items)
            {
                AttachItem(new SetItemViewModel
                {
                    Name = dto.Name,
                    SelectedKey = dto.SelectedKey,
                    Leader = dto.Leader,
                    Color = dto.Color,
                    Length = dto.Length,
                    Bpm = dto.Bpm,
                    IsCompleted = dto.Completed,
                });
                var leader = dto.Leader.Trim();
                if (leader.Length > 0 && !Leaders.Contains(leader, StringComparer.OrdinalIgnoreCase))
                    Leaders.Add(leader);
            }
        }
        finally
        {
            _loadingCurrent = false;
        }
        Renumber();
    }

    // ---------- master data / refresh ----------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        try
        {
            var (songs, leaders) = await _sheets.FetchAsync(_config);
            ApplyMasterData(songs, leaders);
            _lastSynced = DateTime.Now;
            _storage.SaveCache(new CacheData { Songs = songs, Leaders = leaders, LastSynced = _lastSynced.Value });
            StatusText = $"Synced {_lastSynced:g} — {songs.Count} songs, {leaders.Count} leaders";
            StatusState = "ok";
        }
        catch (Exception ex)
        {
            StatusText = _lastSynced is { } t
                ? $"Using cached list — last synced {t:g}"
                : $"Couldn't load the sheet ({Brief(ex)})";
            StatusState = _lastSynced is null ? "error" : "cached";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static string Brief(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : "no internet, or bad Spreadsheet ID";

    private void ApplyMasterData(List<Song> songs, List<string> leaders)
    {
        _allSongs = songs;

        // Keep any session-typed leaders that aren't in the sheet.
        var extras = Leaders.Where(l => !leaders.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        Leaders.Clear();
        foreach (var l in leaders.Concat(extras))
            Leaders.Add(l);

        // Sheet edits flow into the current service: every sync refreshes each
        // row's Length and BPM from its master song, so a corrected timestamp
        // doesn't require re-adding the song. An EMPTY sheet cell never wipes a
        // value someone typed by hand; keys/leaders are per-service and untouched.
        foreach (var item in Items)
        {
            var song = songs.FirstOrDefault(s =>
                string.Equals(s.Name.Trim(), item.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (song is null)
                continue; // manual song, or removed from the sheet
            if (song.Length.Length > 0)
                item.Length = song.Length;
            if (song.Bpm.Length > 0)
                item.Bpm = song.Bpm;
        }

        UpdateSearchResults();
    }

    // ---------- search / add ----------

    partial void OnSearchTextChanged(string value) => UpdateSearchResults();

    private void UpdateSearchResults()
    {
        SearchResults.Clear();
        var query = SearchText.Trim();
        if (query.Length == 0)
            return;

        var matches = _allSongs
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10);
        foreach (var s in matches)
            SearchResults.Add(s);
    }

    /// <summary>Adds a song from the master list (autocomplete pick), filling its default key.</summary>
    public void AddSong(Song song)
    {
        AddItem(song.Name, song.DefaultKey, song.Length, song.Bpm);
        SearchText = "";
    }

    /// <summary>Adds the top autocomplete match, if any. Returns true if something was added.</summary>
    public bool AddTopMatch()
    {
        if (SearchResults.Count == 0)
            return false;
        AddSong(SearchResults[0]);
        return true;
    }

    /// <summary>Adds a manually entered song. It joins the service immediately; if signed in
    /// to Google and the song isn't in the master list, it's also appended to the Songs tab.</summary>
    public void AddManualSong(string name, string key)
    {
        name = name.Trim();
        key = Music.Keys.Normalize(key);
        if (name.Length == 0)
            return;
        AddItem(name, key, "", "");
        _ = AppendToSheetAsync(name, key);
    }

    private async Task AppendToSheetAsync(string name, string key)
    {
        if (!GoogleAuth.IsSignedIn)
            return; // the public CSV endpoint can't write — sheet stays as-is
        if (string.IsNullOrWhiteSpace(_config.SpreadsheetId) || _config.SpreadsheetId == "PUT_ID_HERE")
            return;
        if (_allSongs.Any(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return; // already in the sheet
        try
        {
            await _sheets.AppendSongAsync(_config, name, key);
            // Into the master list too, so search finds it and a repeat add doesn't duplicate it.
            _allSongs.Add(new Song { Name = name, DefaultKey = key, Length = "", Bpm = "" });
            StatusText = $"Added '{name}' to the sheet";
        }
        catch (Exception ex)
        {
            StatusText = $"'{name}' wasn't added to the sheet ({Brief(ex)})";
        }
    }

    private void AddItem(string name, string key, string length, string bpm)
    {
        // OnItemsChanged renumbers and queues the save.
        AttachItem(new SetItemViewModel { Name = name, SelectedKey = key, Leader = "", Length = length, Bpm = bpm });
    }

    private void AttachItem(SetItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
    }

    // ---------- row actions ----------

    /// <summary>Opens a row's inline editor, closing any other open one first. Edits apply
    /// live through bindings, so closing an editor IS saving it — nothing is lost.</summary>
    public void ToggleEdit(SetItemViewModel item)
    {
        var opening = !item.IsEditing;
        if (opening)
        {
            foreach (var other in Items)
                other.IsEditing = false;
        }
        item.IsEditing = opening;
    }

    [RelayCommand]
    private void Remove(SetItemViewModel item)
    {
        if (item.IsPlaying)
            Timecode.StopCountdown();
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
    }

    /// <summary>▶ click cycle: cue (waits for timecode) → start manually → stop.</summary>
    [RelayCommand]
    private void Play(SetItemViewModel item)
    {
        if (item.IsPlaying)
        {
            if (Timecode.PlayState == "cued")
            {
                Timecode.StartManual();
                return;
            }
            item.IsPlaying = false;
            Timecode.StopCountdown();
            return;
        }
        foreach (var other in Items)
            other.IsPlaying = false;
        item.IsPlaying = true;
        Timecode.Cue(item.Name, Music.SongLength.ParseSeconds(item.Length), item.Bpm);
    }

    // ---------- service-level actions ----------

    [RelayCommand]
    private void NewService()
    {
        if (Items.Count > 0)
        {
            var answer = MessageBox.Show(
                "Clear the current service order?", "New service",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
        }
        Timecode.StopCountdown();
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
    }

    [RelayCommand]
    private void CopyAsText()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            sb.Append($"{i + 1}. {item.Name}");
            if (!string.IsNullOrWhiteSpace(item.SelectedKey))
                sb.Append($" — Key {item.SelectedKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Leader))
                sb.Append($" — Leader: {item.Leader.Trim()}");
            sb.AppendLine();
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = $"Copied {Items.Count} song{(Items.Count == 1 ? "" : "s")} to clipboard";
        }
        catch
        {
            StatusText = "Couldn't access the clipboard — try again";
        }
    }

    // ---------- persistence ----------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        SaveCurrent();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SetItemViewModel.Index)
            or nameof(SetItemViewModel.IsEditing)
            or nameof(SetItemViewModel.IsPlaying))
            return;

        // A leader typed in by hand joins the dropdown for the rest of the session.
        if (e.PropertyName == nameof(SetItemViewModel.Leader) &&
            sender is SetItemViewModel item &&
            !string.IsNullOrWhiteSpace(item.Leader) &&
            !Leaders.Contains(item.Leader.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Leaders.Add(item.Leader.Trim());
        }

        SaveCurrent();
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
            Items[i].Index = i + 1;
        FooterText = $"{Items.Count} song{(Items.Count == 1 ? "" : "s")}";
    }

    /// <summary>Queues a debounced save — typing and drag-reordering coalesce into one disk write.</summary>
    private void SaveCurrent()
    {
        if (_loadingCurrent)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes any pending save immediately. Call on window close.</summary>
    public void FlushPendingSave()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        SaveCurrentNow();
    }

    private void SaveCurrentNow()
    {
        _storage.SaveCurrent(new ServiceSet
        {
            Items = Items.Select(i => new SetItemDto
            {
                Name = i.Name,
                SelectedKey = i.SelectedKey,
                Leader = i.Leader,
                Color = i.Color,
                Length = i.Length,
                Bpm = i.Bpm,
                Completed = i.IsCompleted,
            }).ToList(),
        });
    }
}
