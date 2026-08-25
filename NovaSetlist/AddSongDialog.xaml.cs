using System.Windows;
using System.Windows.Controls;

namespace NovaSetlist;

public partial class AddSongDialog : Window
{
    public string SongName => NameBox.Text.Trim();
    public string SongKey => KeyBox.Text.Trim();

    public AddSongDialog(string prefillName = "")
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        if (prefillName.Length > 0)
        {
            NameBox.Text = prefillName;
            NameBox.CaretIndex = prefillName.Length;
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddButton.IsEnabled = NameBox.Text.Trim().Length > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
