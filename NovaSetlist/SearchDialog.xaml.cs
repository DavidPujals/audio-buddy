using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaSetlist.Models;
using NovaSetlist.ViewModels;

namespace NovaSetlist;

/// <summary>Compact search-to-add window used when the main top bar is folded away.
/// Stays open so several songs can be added in a row; Esc closes.</summary>
public partial class SearchDialog : Window
{
    private readonly MainViewModel _vm;

    public SearchDialog(MainViewModel vm)
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        _vm = vm;
        DataContext = vm;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (ResultList.SelectedItem is Song selected)
                    _vm.AddSong(selected);
                else if (!_vm.AddTopMatch() && _vm.SearchText.Trim().Length > 0)
                    AddManually_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void AddManually_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddSongDialog(_vm.SearchText.Trim()) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _vm.AddManualSong(dialog.SongName, dialog.SongKey);
        _vm.SearchText = "";
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is Song song)
            _vm.AddSong(song);
    }
}
