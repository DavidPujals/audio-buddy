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
    public PlaybackViewModel Playback { get; }
    public MultiTracksService MultiTracks { get; }

    public ObservableCollection<SetItemViewModel> Items { get; } = new();
    public ObservableCollection<Song> SearchResults { get; } = new();
    public ObservableCollection<string> Leaders { get; } = new();

    public IReadOnlyList<string> StandardKeys => Music.Keys.StandardKeys;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string statusText = "Loading…";

    /// <summary>Display name of the current setlist; "" = unnamed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetlistHeader))]
    private string setlistName = "";

    /// <summary>Header above the rows: "SETLIST" or "SETLIST · Sunday 12 Oct".</summary>
    public string SetlistHeader => SetlistName.Length > 0 ? $"SETLIST · {SetlistName}" : "SETLIST";

    partial void OnSetlistNameChanged(string value) => SaveCurrent();

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
        Playback = new PlaybackViewModel(_config);
        MultiTracks = new MultiTracksService(_config);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveCurrentNow();
        };
        _sheetWriteTimer = new DispatcherTimer { Interval = SheetWriteDebounce };
        _sheetWriteTimer.Tick += OnSheetWriteTick;
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
            ApplyMasterData(cache.Songs, cache.Leaders, Environment.TickCount64);
            _lastSynced = cache.LastSynced;
            StatusText = $"Using cached list — last synced {cache.LastSynced:g}";
            StatusState = "cached";
        }

        await RefreshAsync();
    }

    private void RestoreCurrentService()
    {
        var saved = _storage.LoadCurrent();
        if (saved is not null)
            LoadSet(saved);
    }

    /// <summary>Populates the rows from a saved setlist (current.json, an opened file).</summary>
    private void LoadSet(ServiceSet set)
    {
        var dirty = new List<SetItemViewModel>();
        _loadingCurrent = true;
        try
        {
            SetlistName = set.Name;
            foreach (var dto in set.Items)
            {
                var item = new SetItemViewModel
                {
                    Name = dto.Name,
                    SelectedKey = dto.SelectedKey,
                    Leader = dto.Leader,
                    Color = dto.Color,
                    Note = dto.Note,
                    Length = dto.Length,
                    Bpm = dto.Bpm,
                    IsCompleted = dto.Completed,
                    IsChromatic = dto.Chromatic,
                    HasKeyChange = dto.HasKeyChange,
                    KeyChangeKey = dto.KeyChangeKey,
                    KeyChangeAt = dto.KeyChangeAt,
                    SheetDirty = dto.SheetDirty,
                    SheetAddOnly = dto.SheetAddOnly,
                };
                AttachItem(item);
                if (dto.SheetDirty)
                    dirty.Add(item);
                AddLeader(dto.Leader);
            }
        }
        finally
        {
            _loadingCurrent = false;
        }
        Renumber();

        // Edits that never reached the sheet (closed too soon, offline) go back in the queue
        // BEFORE the startup sync, so the sync leaves them alone instead of reverting them.
        foreach (var item in dirty)
            QueueSheetWrite(item);
    }

    // ---------- setlist files ----------

    /// <summary>Snapshot of the rows in the saved-file shape.</summary>
    private ServiceSet BuildSet() => new()
    {
        Name = SetlistName,
        Items = Items.Select(i => new SetItemDto
        {
            Name = i.Name,
            SelectedKey = i.SelectedKey,
            Leader = i.Leader,
            Color = i.Color,
            Note = i.Note,
            Length = i.Length,
            Bpm = i.Bpm,
            Completed = i.IsCompleted,
            Chromatic = i.IsChromatic,
            HasKeyChange = i.HasKeyChange,
            KeyChangeKey = i.KeyChangeKey,
            KeyChangeAt = i.KeyChangeAt,
            SheetDirty = i.SheetDirty,
            SheetAddOnly = i.SheetAddOnly,
        }).ToList(),
    };

    /// <summary>Writes the current setlist to the backups folder (no-op when empty). Returns the path.</summary>
    public string? BackupCurrent()
    {
        if (Items.Count == 0)
            return null;
        var path = _storage.Backup(BuildSet());
        StatusText = path is null
            ? "Couldn't write a backup of the current setlist"
            : $"Backed up '{(SetlistName.Length > 0 ? SetlistName : "setlist")}' to {System.IO.Path.GetFileName(path)}";
        return path;
    }

    /// <summary>Empties the rows and starts a fresh (optionally named) setlist. Back up first.</summary>
    public void ClearSetlist(string name)
    {
        Timecode.StopCountdown();
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        SetlistName = name.Trim();
    }

    public bool ExportSetlist(string path)
    {
        var ok = _storage.SaveTo(path, BuildSet());
        StatusText = ok ? $"Saved setlist to {System.IO.Path.GetFileName(path)}" : "Couldn't save the setlist file";
        return ok;
    }

    /// <summary>Replaces the rows with a saved file's. Back up first. Returns false if unreadable.</summary>
    public bool ImportSetlist(string path)
    {
        var set = _storage.LoadFrom(path);
        if (set is null)
        {
            StatusText = $"Couldn't read {System.IO.Path.GetFileName(path)} — not a setlist file?";
            return false;
        }
        if (set.Name.Length == 0)
        {
            var file = System.IO.Path.GetFileName(path);
            set.Name = file.EndsWith(StorageService.SetlistExtension, StringComparison.OrdinalIgnoreCase)
                ? file[..^StorageService.SetlistExtension.Length]
                : System.IO.Path.GetFileNameWithoutExtension(path);
        }
        ClearSetlist("");
        LoadSet(set);
        SaveCurrent();
        StatusText = $"Opened '{set.Name}' — {Items.Count} song{(Items.Count == 1 ? "" : "s")}";
        return true;
    }

    /// <summary>
    /// Names every Playback song in one go by assuming Playback's setlist is in the same
    /// order as this one: walks Playback's setlist, then links song N to row N.
    /// </summary>
    private bool _refreshingNames;

    /// <summary>Pulls every recent/upcoming setlist from the MultiTracks account and names
    /// Playback's songs from it — no walking, no linking.</summary>
    public async Task RefreshPlaybackNamesAsync()
    {
        if (_refreshingNames)
            return;
        if (!MultiTracks.IsConfigured)
        {
            StatusText = "Enter the MultiTracks client ID in Settings → MultiTracks account first";
            return;
        }
        if (!MultiTracks.IsSignedIn)
        {
            StatusText = "Sign in to MultiTracks in Settings first";
            return;
        }
        _refreshingNames = true;
        try
        {
            var progress = new Progress<string>(s => StatusText = "MultiTracks — " + s);
            var result = await MultiTracks.FetchNamesAsync(progress, CancellationToken.None);
            var changed = Playback.MergeTitles(result.Titles);
            var songs = result.Setlists.Sum(s => s.Songs.Count);
            StatusText = $"Playback names refreshed — {songs} songs across {result.Setlists.Count} setlists" +
                         (changed > 0 ? $", {changed} new or changed" : ", nothing new") +
                         (result.Unresolved > 0 ? $"; {result.Unresolved} couldn't be named" : "");
        }
        catch (Exception ex)
        {
            StatusText = "MultiTracks refresh failed — " + (ex is InvalidOperationException ? ex.Message : "no internet?");
        }
        finally
        {
            _refreshingNames = false;
        }
    }

    public async Task MatchPlaybackByOrderAsync()
    {
        if (Items.Count == 0)
        {
            StatusText = "Add the songs to this setlist first — the match links Playback's songs to these rows in order";
            return;
        }
        List<long> ids;
        try
        {
            var progress = new Progress<string>(s => StatusText = "Matching with Playback — " + s);
            ids = await Playback.WalkSetlistAsync(progress, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
            return;
        }

        var n = Math.Min(ids.Count, Items.Count);
        for (var i = 0; i < n; i++)
            Playback.Learn(ids[i], Items[i].Name.Trim(), announce: false);
        Playback.Learn(-1, "", announce: true); // refresh the panel text

        StatusText = ids.Count == Items.Count
            ? $"Matched {n} Playback songs to this setlist by order"
            : $"Playback has {ids.Count} songs, this setlist has {Items.Count} — matched the first {n} by order; right-click a row → Link to fix any";
    }

    /// <summary>Remembers that the song Playback is currently on is this row's song.</summary>
    public void LinkToPlayback(SetItemViewModel item)
    {
        var id = Playback.CurrentSongId;
        if (id < 0)
        {
            StatusText = Playback.IsEnabled
                ? "Playback isn't reporting a song right now — select the song in Playback, then try again"
                : "Set the Playback host in Settings first";
            return;
        }
        Playback.Learn(id, item.Name.Trim());
        StatusText = $"Linked '{item.Name.Trim()}' to Playback song {id}";
    }

    /// <summary>Adds a leader name to the dropdown if it's new (case-insensitive).</summary>
    private void AddLeader(string? name)
    {
        var leader = (name ?? "").Trim();
        if (leader.Length > 0 && !Leaders.Contains(leader, StringComparer.OrdinalIgnoreCase))
            Leaders.Add(leader);
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
            var fetchStarted = Environment.TickCount64;
            var (songs, leaders) = await _sheets.FetchAsync(_config);
            ApplyMasterData(songs, leaders, fetchStarted);
            _lastSynced = DateTime.Now;
            _storage.SaveCache(new CacheData { Songs = songs, Leaders = leaders, LastSynced = _lastSynced.Value });
            StatusText = $"Synced {_lastSynced:g} — {songs.Count} songs, {leaders.Count} leaders";
            StatusState = "ok";
            // Edits parked while signed out / offline can go up now.
            if (_pendingSheetWrites.Count > 0 && GoogleAuth.IsSignedIn)
                RearmSheetWrite(SheetWriteDebounce);
        }
        catch (Exception ex)
        {
            // A sheet-level reason (sign-in expired, private sheet, bad tab) is worth
            // showing even when the cache keeps the app usable; network noise isn't.
            StatusText = _lastSynced is { } t
                ? ex is InvalidOperationException
                    ? $"Using cached list — {ex.Message}"
                    : $"Using cached list — last synced {t:g}"
                : $"Couldn't load the sheet ({Brief(ex)})";
            StatusState = _lastSynced is null ? "error" : "cached";
        }
        finally
        {
            IsRefreshing = false;
        }
        if (_refreshAgain)
        {
            _refreshAgain = false;
            await RefreshAsync(); // a write-back landed mid-fetch; that fetch was already stale
        }
    }

    private static string Brief(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : "no internet, or bad Spreadsheet ID";

    /// <param name="fetchStarted">Tick when this data was requested — sheet values older than
    /// our last write-back are not allowed to overwrite the rows.</param>
    private void ApplyMasterData(List<Song> songs, List<string> leaders, long fetchStarted)
    {
        _allSongs = songs;

        // Add-only: a Clear/re-add fires a Reset that blanks the Leader of any row whose
        // editor is open (the editable ComboBox pushes "" through the binding). Names typed
        // this session stay for the session either way.
        foreach (var l in leaders)
            AddLeader(l);

        var staleForExtras = fetchStarted < _lastSheetWriteTick;

        // Sheet edits flow into the current service: every sync refreshes each
        // row's Length and BPM from its master song, so a corrected timestamp
        // doesn't require re-adding the song. An EMPTY sheet cell never wipes a
        // value someone typed by hand; keys/leaders are per-service and untouched.
        //
        // Chromatic / key change are two-way: edits here are written to the
        // sheet, so when signed in the sheet is authoritative (a cleared cell
        // clears the row). Signed out there's no write path, so local edits
        // are kept unless the sheet actually has a value.
        var sheetIsAuthority = GoogleAuth.IsSignedIn;
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

            if (staleForExtras || item.SheetDirty || _pendingSheetWrites.Contains(item))
                continue; // an edit is on its way up (or this fetch predates one) — don't clobber it
            _applyingSheet = true;
            try
            {
                if (sheetIsAuthority || song.Chromatic)
                    item.IsChromatic = song.Chromatic;
                if (sheetIsAuthority || song.KeyChangeKey.Length > 0)
                {
                    // A toggle left on with the TO key still blank isn't "cleared by the sheet".
                    item.HasKeyChange = song.KeyChangeKey.Length > 0 ||
                                        (item.HasKeyChange && item.KeyChangeKey.Trim().Length == 0);
                    item.KeyChangeKey = song.KeyChangeKey;
                }
                if (sheetIsAuthority || song.KeyChangeAt.Length > 0)
                    item.KeyChangeAt = song.KeyChangeAt;
            }
            finally
            {
                _applyingSheet = false;
            }
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
        AddItem(song.Name, song.DefaultKey, song.Length, song.Bpm, song);
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

    /// <summary>Adds a manually entered song. It joins the service immediately; if it isn't in
    /// the master list it also goes through the sheet queue, which appends it to the Songs tab
    /// (same serialized path as key-detail edits, so a quick follow-up edit can't double it up).</summary>
    public void AddManualSong(string name, string key)
    {
        name = name.Trim();
        key = Music.Keys.Normalize(key);
        if (name.Length == 0)
            return;
        var master = _allSongs.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        var item = AddItem(name, key, master?.Length ?? "", master?.Bpm ?? "", master);
        if (master is null)
        {
            item.SheetAddOnly = true;
            QueueSheetWrite(item);
        }
    }

    private SetItemViewModel AddItem(string name, string key, string length, string bpm, Song? master = null)
    {
        // OnItemsChanged renumbers and queues the save.
        var item = new SetItemViewModel
        {
            Name = name,
            SelectedKey = key,
            Leader = "",
            Length = length,
            Bpm = bpm,
            IsChromatic = master?.Chromatic ?? false,
            HasKeyChange = master is { KeyChangeKey.Length: > 0 },
            KeyChangeKey = master?.KeyChangeKey ?? "",
            KeyChangeAt = master?.KeyChangeAt ?? "",
        };
        AttachItem(item);
        return item;
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
        Timecode.Cue(item.Name, Music.SongLength.ParseSeconds(item.Length), item.Bpm,
            item.IsChromatic, item.SelectedKey,
            item.KeyChangeActive ? item.KeyChangeKey.Trim() : "",
            item.KeyChangeActive ? Music.SongLength.ParseSeconds(item.KeyChangeAt) : 0);
    }

    // ---------- setlist-level actions ----------

    [RelayCommand]
    private void CopyAsText()
    {
        var sb = new StringBuilder();
        if (SetlistName.Length > 0)
            sb.AppendLine(SetlistName);
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            sb.Append($"{i + 1}. {item.Name}");
            if (!string.IsNullOrWhiteSpace(item.SelectedKey) || item.IsChromatic)
            {
                sb.Append(" — Key");
                if (!string.IsNullOrWhiteSpace(item.SelectedKey))
                    sb.Append($" {item.SelectedKey.Trim()}");
                if (item.IsChromatic)
                    sb.Append(" (chromatic)");
                if (item.KeyChangeActive)
                {
                    sb.Append($" → {item.KeyChangeKey.Trim()}");
                    if (!string.IsNullOrWhiteSpace(item.KeyChangeAt))
                        sb.Append($" at {item.KeyChangeAt.Trim()}");
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Leader))
                sb.Append($" — Leader: {item.Leader.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Note))
                sb.Append($" — Note: {item.Note.Trim()}");
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
        if (sender is not SetItemViewModel item)
            return;

        if (e.PropertyName == nameof(SetItemViewModel.IsEditing))
        {
            // A hand-typed leader joins the dropdown when the editor CLOSES — the editable
            // ComboBox pushes every keystroke, so adding on change fills the list with "S", "Sa", "Sar"…
            if (!item.IsEditing)
                AddLeader(item.Leader);
            return;
        }
        if (e.PropertyName is nameof(SetItemViewModel.Index) or nameof(SetItemViewModel.IsPlaying))
            return;

        // Chromatic / key change edits are master-song data — push them to the sheet.
        if (!_applyingSheet &&
            e.PropertyName is nameof(SetItemViewModel.IsChromatic)
                or nameof(SetItemViewModel.HasKeyChange)
                or nameof(SetItemViewModel.KeyChangeKey)
                or nameof(SetItemViewModel.KeyChangeAt))
        {
            item.SheetAddOnly = false; // a real edit supersedes a pending plain add
            QueueSheetWrite(item);
        }

        SaveCurrent();
    }

    // ---------- sheet write-back (chromatic / key change / manual adds) ----------
    //
    // One serialized worker: edits are marked dirty (persisted) and queued; a pass writes
    // them one at a time, never overlapping with itself. Failures keep the item queued and
    // back off (10 s → 2 min); signed-out edits wait for a sign-in; anything still dirty at
    // shutdown is re-queued on the next launch. So the sheet always converges on what's shown.

    private static readonly TimeSpan SheetWriteDebounce = TimeSpan.FromMilliseconds(1500);
    private readonly HashSet<SetItemViewModel> _pendingSheetWrites = new();
    private readonly DispatcherTimer _sheetWriteTimer;
    private bool _sheetWriteBusy;
    private int _sheetWriteFailures;
    private bool _signedOutNagShown;    // the "sign in to sync" line once per session, not per keystroke
    private bool _refreshAgain;         // a write landed while a fetch was in flight — fetch once more
    private long _lastSheetWriteTick;   // 0 until the first successful write (every fetch is newer than that)
    private bool _applyingSheet;        // true while a sync is pushing sheet values INTO rows

    private void QueueSheetWrite(SetItemViewModel item)
    {
        if (_loadingCurrent)
            return;
        item.SheetDirty = true;
        _pendingSheetWrites.Add(item);
        RearmSheetWrite(SheetWriteDebounce);
    }

    private void RearmSheetWrite(TimeSpan delay)
    {
        _sheetWriteTimer.Stop();
        _sheetWriteTimer.Interval = delay;
        _sheetWriteTimer.Start();
    }

    /// <summary>Called after a sign-in so edits parked while signed out (or refused earlier) go up.</summary>
    public void RetrySheetWrites()
    {
        _sheetWriteFailures = 0;
        foreach (var item in Items)
        {
            if (item.SheetDirty)
                _pendingSheetWrites.Add(item);
        }
        if (_pendingSheetWrites.Count > 0)
            RearmSheetWrite(SheetWriteDebounce);
    }

    private static (string Name, string Key, bool Chromatic, string ChangeKey, string ChangeAt) SheetSnapshot(SetItemViewModel item) =>
        (item.Name.Trim(),
         Music.Keys.Normalize(item.SelectedKey),
         item.IsChromatic,
         item.HasKeyChange ? Music.Keys.Normalize(item.KeyChangeKey) : "",
         item.HasKeyChange ? item.KeyChangeAt.Trim() : "");

    private async void OnSheetWriteTick(object? sender, EventArgs e)
    {
        _sheetWriteTimer.Stop();
        if (_sheetWriteBusy)
            return; // the running pass re-arms itself when it finishes

        if (!GoogleAuth.IsSignedIn)
        {
            if (!_signedOutNagShown)
            {
                _signedOutNagShown = true;
                StatusText = "Song details kept locally — sign in with Google (Settings) to sync them to the sheet";
            }
            return; // stays queued and dirty; RetrySheetWrites/RefreshAsync pick it up later
        }
        if (string.IsNullOrWhiteSpace(_config.SpreadsheetId) || _config.SpreadsheetId == "PUT_ID_HERE")
            return;

        _sheetWriteBusy = true;
        var transientFailure = false;
        try
        {
            // Rows removed from the service still get written: these are song details, not
            // service details, and the user saw them applied.
            foreach (var item in _pendingSheetWrites.ToList())
            {
                var snap = SheetSnapshot(item);
                var addOnly = item.SheetAddOnly;
                try
                {
                    if (addOnly)
                    {
                        var existing = await _sheets.EnsureSongAsync(_config, snap.Name, snap.Key);
                        if (existing is { } cells)
                            AdoptSheetExtras(item, cells.Chromatic, cells.KeyChangeKey, cells.KeyChangeAt);
                        snap = SheetSnapshot(item);
                    }
                    else
                    {
                        await _sheets.UpdateSongExtrasAsync(_config, snap.Name, snap.Key, snap.Chromatic, snap.ChangeKey, snap.ChangeAt);
                    }
                    _lastSheetWriteTick = Environment.TickCount64;
                    _sheetWriteFailures = 0;
                    if (IsRefreshing)
                        _refreshAgain = true; // that fetch predates this write — take one more

                    var master = _allSongs.FirstOrDefault(s =>
                        string.Equals(s.Name.Trim(), snap.Name, StringComparison.OrdinalIgnoreCase));
                    if (master is null)
                    {
                        master = new Song { Name = snap.Name, DefaultKey = snap.Key };
                        _allSongs.Add(master);
                    }
                    master.Chromatic = snap.Chromatic;
                    master.KeyChangeKey = snap.ChangeKey;
                    master.KeyChangeAt = snap.ChangeAt;

                    // Edited again while the write was in flight? Then it needs another pass.
                    if (SheetSnapshot(item) == snap && item.SheetAddOnly == addOnly)
                    {
                        _pendingSheetWrites.Remove(item);
                        item.SheetDirty = false;
                        item.SheetAddOnly = false;
                    }
                    StatusText = addOnly ? $"Added '{snap.Name}' to the sheet" : $"Sheet updated — '{snap.Name}'";
                }
                catch (InvalidOperationException ex)
                {
                    // Google said no (no edit rights, columns in use, tab missing): retrying won't
                    // change that. Park it — still dirty, so a sign-in or the next launch retries.
                    _pendingSheetWrites.Remove(item);
                    StatusText = $"'{snap.Name}' not synced to the sheet — {ex.Message}";
                }
                catch (Exception ex)
                {
                    _sheetWriteFailures++;
                    StatusText = $"'{snap.Name}' not synced to the sheet yet — will retry ({Brief(ex)})";
                    transientFailure = true;
                    break; // back off instead of hammering a dead connection with the rest
                }
            }
        }
        finally
        {
            _sheetWriteBusy = false;
        }

        if (_pendingSheetWrites.Count > 0)
        {
            var backoff = transientFailure
                ? TimeSpan.FromSeconds(Math.Min(120, 10 * Math.Pow(2, _sheetWriteFailures - 1)))
                : SheetWriteDebounce;
            RearmSheetWrite(backoff);
        }
        if (_refreshAgain && !IsRefreshing)
        {
            _refreshAgain = false;
            _ = RefreshAsync();
        }
    }

    /// <summary>Pushes sheet-side key details into a row without triggering a write-back.</summary>
    private void AdoptSheetExtras(SetItemViewModel item, bool chromatic, string keyChangeKey, string keyChangeAt)
    {
        _applyingSheet = true;
        try
        {
            item.IsChromatic = chromatic;
            item.HasKeyChange = keyChangeKey.Length > 0;
            item.KeyChangeKey = keyChangeKey;
            item.KeyChangeAt = keyChangeAt;
        }
        finally
        {
            _applyingSheet = false;
        }
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

    private void SaveCurrentNow() => _storage.SaveCurrent(BuildSet());
}
