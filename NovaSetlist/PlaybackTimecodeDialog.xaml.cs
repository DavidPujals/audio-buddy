using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaSetlist.Services;
using NovaSetlist.ViewModels;

namespace NovaSetlist;

/// <summary>One setlist song in the Playback timecode dialog.</summary>
public partial class TimecodeRow : ObservableObject
{
    public int Order { get; init; }
    public long SetlistSongId { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";

    [ObservableProperty]
    private string status = "";

    /// <summary>"ok", "busy" or "error" — colours the status text.</summary>
    [ObservableProperty]
    private string statusState = "ok";
}

/// <summary>
/// Sets SMPTE timecode settings on the songs of a MultiTracks setlist through the MCP:
/// "start at 0:00" (the song's own SMPTE start = 0 s, timeline offset 0, output on) or
/// "follow default arrangement". The setlist list comes from the reference file the names
/// refresh writes; the writes need the MultiTracks sign-in.
/// </summary>
public partial class PlaybackTimecodeDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _closing = new();
    private bool _busy;

    public ObservableCollection<TimecodeRow> Rows { get; } = new();

    public PlaybackTimecodeDialog(MainViewModel vm)
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        _vm = vm;
        SongList.ItemsSource = Rows;
        LoadSetlists(preferCurrent: true);
        UpdateHint();
    }

    private void LoadSetlists(bool preferCurrent)
    {
        var lists = _vm.Playback.ReferenceSetlists;
        var keep = SetlistBox.SelectedItem as PlaybackViewModel.RefSetlist;
        SetlistBox.ItemsSource = lists;

        PlaybackViewModel.RefSetlist? pick = null;
        if (preferCurrent && _vm.Playback.ReferenceFor(_vm.Playback.CurrentSongId) is { } current)
            pick = lists.FirstOrDefault(l => l.Id == current.SetlistId);
        pick ??= keep is not null ? lists.FirstOrDefault(l => l.Id == keep.Id) : null;
        pick ??= lists.FirstOrDefault();
        SetlistBox.SelectedItem = pick;
        if (pick is null)
            FillRows(null);
    }

    private void SetlistBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FillRows(SetlistBox.SelectedItem as PlaybackViewModel.RefSetlist);

    private void FillRows(PlaybackViewModel.RefSetlist? setlist)
    {
        Rows.Clear();
        if (setlist is not null)
        {
            foreach (var s in setlist.Songs)
            {
                var detail = string.Join(" · ", new[]
                {
                    s.Key,
                    s.Duration > 0 ? Music.SongLength.Format(s.Duration) : "",
                    s.Bpm > 0 ? $"{s.Bpm:0.#} BPM" : "",
                }.Where(x => x.Length > 0));
                Rows.Add(new TimecodeRow { Order = s.Order, SetlistSongId = s.SetlistSongId, Title = s.Title, Detail = detail });
            }
        }
        EmptyText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = setlist is null
            ? "No setlists known yet — Reload from MultiTracks (or Refresh names in the PLAYBACK panel) fills this list."
            : "No songs in this setlist.";
    }

    private void UpdateHint()
    {
        var mt = _vm.MultiTracks;
        HintText.Text = !mt.IsConfigured
            ? "Changes need the MultiTracks sign-in — enter the client ID in Settings → Playback first."
            : !mt.IsSignedIn
                ? "Sign in to MultiTracks in Settings → Playback to make changes."
                : "Changes go straight to the MultiTracks setlist; Playback picks them up when it syncs that setlist.";
    }

    // ---------- actions ----------

    private async void Zero_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimecodeRow row })
            await ApplyAsync(new[] { row }, zero: true);
    }

    private async void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimecodeRow row })
            await ApplyAsync(new[] { row }, zero: false);
    }

    private async void AllZero_Click(object sender, RoutedEventArgs e) => await ApplyAsync(Rows.ToList(), zero: true);

    private async void AllFollow_Click(object sender, RoutedEventArgs e) => await ApplyAsync(Rows.ToList(), zero: false);

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        SetBusy(true);
        StatusText.Text = "Reloading setlists from MultiTracks…";
        try
        {
            await _vm.RefreshPlaybackNamesAsync();
            LoadSetlists(preferCurrent: SetlistBox.SelectedItem is null);
            StatusText.Text = _vm.StatusText;
        }
        finally
        {
            SetBusy(false);
            UpdateHint();
        }
    }

    /// <summary>Applies one setting to the given songs in order, tracking the setlist version
    /// across the writes and re-reading it once if MultiTracks reports a conflict.</summary>
    private async Task ApplyAsync(IReadOnlyList<TimecodeRow> rows, bool zero)
    {
        if (_busy || rows.Count == 0 || SetlistBox.SelectedItem is not PlaybackViewModel.RefSetlist setlist)
            return;
        var mt = _vm.MultiTracks;
        if (!mt.IsConfigured || !mt.IsSignedIn)
        {
            UpdateHint();
            StatusText.Text = HintText.Text;
            return;
        }

        SetBusy(true);
        var ok = 0;
        var cancel = _closing.Token;
        try
        {
            StatusText.Text = $"Reading '{setlist.Name}'…";
            var version = await mt.GetSetlistVersionAsync(setlist.Id, cancel);
            foreach (var row in rows)
            {
                row.StatusState = "busy";
                row.Status = "…";
                StatusText.Text = $"{(zero ? "Setting SMPTE start to 0:00" : "Setting follow default")} — {row.Title}";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (zero)
                        {
                            // The song's own values only apply when it isn't following its default.
                            version = await mt.SetSmpteFollowsDefaultAsync(setlist.Id, row.SetlistSongId, version, false, cancel);
                            version = await mt.SetSmpteAsync(setlist.Id, row.SetlistSongId, version,
                                EnableBox.IsChecked == true, startTime: 0, timelineLocation: 0, cancel);
                            row.Status = EnableBox.IsChecked == true ? "SMPTE on · start 0:00 ✓" : "start 0:00 ✓";
                        }
                        else
                        {
                            version = await mt.SetSmpteFollowsDefaultAsync(setlist.Id, row.SetlistSongId, version, true, cancel);
                            row.Status = "follows default ✓";
                        }
                        row.StatusState = "ok";
                        ok++;
                        break;
                    }
                    catch (MultiTracksToolException ex) when (ex.IsVersionConflict && attempt == 0)
                    {
                        version = await mt.GetSetlistVersionAsync(setlist.Id, cancel); // moved on — re-read, retry once
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        row.StatusState = "error";
                        row.Status = "failed — " + ex.Message;
                        break;
                    }
                }
            }
            StatusText.Text = ok == rows.Count
                ? $"Done — {ok} song{(ok == 1 ? "" : "s")} updated in '{setlist.Name}' (setlist version {version})"
                : $"{ok} of {rows.Count} updated — see the rows marked failed";
        }
        catch (OperationCanceledException)
        {
            // window closed mid-way
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't update the setlist — " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SongList.IsEnabled = !busy;
        SetlistBox.IsEnabled = !busy;
        ReloadButton.IsEnabled = !busy;
        AllZeroButton.IsEnabled = !busy;
        AllFollowButton.IsEnabled = !busy;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        base.OnClosed(e);
    }
}
