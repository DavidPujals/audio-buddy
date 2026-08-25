using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NovaSetlist.Models;
using NovaSetlist.ViewModels;

namespace NovaSetlist;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Services.StorageService _windowStore = new();

    public MainWindow()
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        RestoreWindowPlacement();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        SizeChanged += (_, _) =>
        {
            // Breakpoints = the previous fixed minimums: below them the layout adapts.
            _vm.IsNarrow = ActualWidth < 758;
            _vm.IsShort = ActualHeight < 448;
        };
    }

    /// <summary>Reopens the window where it was last closed, if that spot still makes sense.</summary>
    private void RestoreWindowPlacement()
    {
        var p = _windowStore.LoadWindow();
        if (p is null || double.IsNaN(p.Left) || double.IsNaN(p.Top) || p.Width < 100 || p.Height < 80)
            return;

        var width = Math.Max(MinWidth, p.Width);
        var height = Math.Max(MinHeight, p.Height);

        // The title bar must land on today's monitor layout (screens may have
        // been unplugged or rearranged since the last run) — else start fresh.
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (p.Left + width < left + 60 || p.Left > right - 60 || p.Top < top - 8 || p.Top > bottom - 40)
            return;

        Left = p.Left;
        Top = p.Top;
        Width = width;
        Height = height;
        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        // Maximized/minimized: remember the normal-state bounds it would restore to.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 80)
            return;
        _windowStore.SaveWindow(new Models.WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        });
    }

    // ---------- search autocomplete ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Any non-empty query opens the popup: matches if there are any, and
        // always the "+ Add … manually" row for songs the sheet doesn't have.
        SearchPopup.IsOpen = _vm.SearchText.Trim().Length > 0 && SearchBox.IsKeyboardFocusWithin;
        if (SearchPopup.IsOpen)
            SearchList.SelectedIndex = 0;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when SearchPopup.IsOpen:
                SearchList.SelectedIndex = Math.Min(SearchList.SelectedIndex + 1, SearchList.Items.Count - 1);
                SearchList.ScrollIntoView(SearchList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up when SearchPopup.IsOpen:
                SearchList.SelectedIndex = Math.Max(SearchList.SelectedIndex - 1, 0);
                SearchList.ScrollIntoView(SearchList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
                if (SearchPopup.IsOpen && SearchList.SelectedItem is Song selected)
                    _vm.AddSong(selected);
                else if (!_vm.AddTopMatch() && _vm.SearchText.Trim().Length > 0)
                {
                    // No match at all — offer to add it manually, name prefilled.
                    SearchPopup.IsOpen = false;
                    AddManuallyPrefilled(_vm.SearchText.Trim());
                }
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;

            case Key.Escape:
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Don't close while the click is landing inside the popup's list.
        if (!SearchPopup.IsKeyboardFocusWithin)
            SearchPopup.IsOpen = false;
    }

    private void SearchList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song song })
        {
            _vm.AddSong(song);
            SearchPopup.IsOpen = false;
            SearchBox.Focus();
        }
    }

    // ---------- manual add ----------

    private void AddManually_Click(object sender, RoutedEventArgs e) => AddManuallyPrefilled("");

    private void SearchAddManually_Click(object sender, RoutedEventArgs e)
    {
        var name = _vm.SearchText.Trim();
        SearchPopup.IsOpen = false;
        AddManuallyPrefilled(name);
    }

    private void AddManuallyPrefilled(string name)
    {
        var dialog = new AddSongDialog(name) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _vm.AddManualSong(dialog.SongName, dialog.SongKey);
        _vm.SearchText = "";
    }

    // ---------- row editing ----------

    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetItemViewModel item })
            _vm.ToggleEdit(item);
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string token } swatch &&
            FindRowItem(swatch) is { } item)
            item.Color = token;
    }

    /// <summary>Walks up the visual tree to the row's SetItemViewModel.</summary>
    private static SetItemViewModel? FindRowItem(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: SetItemViewModel item })
                return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------- drag-to-reorder via the row number ----------

    private SetItemViewModel? _dragItem;
    private bool _dragActive;
    private Point _dragStart;

    private void RowGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetItemViewModel item } grip)
        {
            _dragItem = item;
            _dragActive = false;
            _dragStart = e.GetPosition(SetlistItems);
            grip.CaptureMouse();
            e.Handled = true;
        }
    }

    private void RowGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(SetlistItems);
        if (!_dragActive && Math.Abs(pos.Y - _dragStart.Y) < 4)
            return; // ignore accidental jiggles until a real drag starts
        _dragActive = true;

        var target = RowIndexAt(pos.Y);
        var current = _vm.Items.IndexOf(_dragItem);
        if (target >= 0 && current >= 0 && target != current)
            _vm.Items.Move(current, target);
    }

    private void RowGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A press that never crossed the drag threshold is a click: toggle "completed".
        if (!_dragActive && _dragItem is { } item)
            item.IsCompleted = !item.IsCompleted;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        _dragItem = null;
        _dragActive = false;
    }

    private void RowGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragItem = null;
        _dragActive = false;
    }

    /// <summary>The item index whose row contains the given y (relative to the list), or -1.</summary>
    private int RowIndexAt(double y)
    {
        for (var i = 0; i < SetlistItems.Items.Count; i++)
        {
            if (SetlistItems.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row)
            {
                var top = row.TranslatePoint(default, SetlistItems).Y;
                if (y >= top && y < top + row.ActualHeight)
                    return i;
            }
        }
        return -1;
    }

    // ---------- compact-mode menu + row context menu ----------

    private SearchDialog? _searchDialog;

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void MenuAddSongs_Click(object sender, RoutedEventArgs e)
    {
        if (_searchDialog is { IsVisible: true })
        {
            _searchDialog.Activate();
            return;
        }
        _searchDialog = new SearchDialog(_vm) { Owner = this };
        // Drop the reference on close so the window (and its visual tree) can be collected.
        _searchDialog.Closed += (_, _) => _searchDialog = null;
        _searchDialog.Show();
    }

    private void RowMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetItemViewModel item })
            _vm.ToggleEdit(item);
    }

    private void RowMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetItemViewModel item })
            _vm.RemoveCommand.Execute(item);
    }

    // ---------- settings ----------

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_vm) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _vm.Timecode.SelectedDevice = dialog.TimecodeDevice;
        _vm.KeyDetect.SelectedDevice = dialog.KeyDetectDevice;
        _vm.Spl.FastResponse = dialog.SplFast;
        _vm.Spl.YellowFrom = dialog.SplYellowLevel;
        _vm.Spl.RedFrom = dialog.SplRedLevel;
        _vm.Spl.SelectedDevice = dialog.SplDevice;
        _vm.Spl.IsEnabled = dialog.SplEnabled;
        await _vm.ApplySheetSettingsAsync(dialog.SpreadsheetId, dialog.SongsTabName, dialog.LeadersTabName);
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPlacement();
        _vm.FlushPendingSave();
        _vm.Timecode.Dispose();
        _vm.KeyDetect.Dispose();
        _vm.Spl.Dispose();
        base.OnClosed(e);
    }
}
