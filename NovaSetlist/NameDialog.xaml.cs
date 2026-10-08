using System.Windows;

namespace NovaSetlist;

/// <summary>Single-field prompt used to name or rename a setlist.</summary>
public partial class NameDialog : Window
{
    public string Value => NameBox.Text.Trim();

    public NameDialog(string title, string prompt, string initial = "", string hint = "", string okText = "OK")
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        Title = title;
        PromptText.Text = prompt;
        HintText.Text = hint;
        HintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = okText;
        NameBox.Text = initial;
        NameBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
