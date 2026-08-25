using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NovaSetlist.ViewModels;

namespace NovaSetlist;

public partial class SettingsDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly double _originalOffset;

    public string SpreadsheetId => IdBox.Text;
    public string SongsTabName => SongsTabBox.Text;
    public string LeadersTabName => LeadersTabBox.Text;
    public string TimecodeDevice => TimecodeDeviceBox.SelectedItem as string ?? TimecodeViewModel.OffDevice;
    public string KeyDetectDevice => KeyDeviceBox.SelectedItem as string ?? KeyDetectViewModel.OffDevice;

    public bool SplEnabled => SplEnabledBox.IsChecked == true;
    public string SplDevice => SplDeviceBox.SelectedItem as string ?? SplViewModel.OffDevice;
    public bool SplFast => SplResponseBox.SelectedIndex == 1;
    public double SplYellowLevel => ParseLevel(SplYellowBox.Text, _vm.Spl.YellowFrom);
    public double SplRedLevel => ParseLevel(SplRedBox.Text, _vm.Spl.RedFrom);

    public SettingsDialog(MainViewModel vm)
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        _vm = vm;

        IdBox.Text = vm.Config.SpreadsheetId == "PUT_ID_HERE" ? "" : vm.Config.SpreadsheetId;
        SongsTabBox.Text = vm.Config.SongsTab;
        LeadersTabBox.Text = vm.Config.LeadersTab;

        vm.Timecode.RefreshDevices();
        vm.KeyDetect.RefreshDevices();
        vm.Spl.RefreshDevices();
        TimecodeDeviceBox.ItemsSource = vm.Timecode.Devices;
        TimecodeDeviceBox.SelectedItem = vm.Timecode.SelectedDevice;
        KeyDeviceBox.ItemsSource = vm.KeyDetect.Devices;
        KeyDeviceBox.SelectedItem = vm.KeyDetect.SelectedDevice;

        SplEnabledBox.IsChecked = vm.Spl.IsEnabled;
        SplDeviceBox.ItemsSource = vm.Spl.Devices;
        SplDeviceBox.SelectedItem = vm.Spl.SelectedDevice;
        SplResponseBox.SelectedIndex = vm.Spl.FastResponse ? 1 : 0;
        SplYellowBox.Text = vm.Spl.YellowFrom.ToString("0.#", CultureInfo.CurrentCulture);
        SplRedBox.Text = vm.Spl.RedFrom.ToString("0.#", CultureInfo.CurrentCulture);

        // The offset applies LIVE so the meter can be calibrated against a reference
        // while this dialog is open; Cancel puts the original value back.
        _originalOffset = vm.Spl.Offset;
        SplOffsetBox.Text = vm.Spl.Offset.ToString("0.#", CultureInfo.CurrentCulture);

        UpdateGoogleUi();
    }

    private void UpdateGoogleUi()
    {
        if (_vm.GoogleAuth.IsSignedIn)
        {
            var email = _vm.GoogleAuth.Email;
            GoogleStatusText.Text = email.Length > 0 ? $"Signed in as {email}" : "Signed in to Google";
            GoogleAuthButton.Content = "Sign out";
        }
        else
        {
            GoogleStatusText.Text = "Private sheet? Sign in to read it with your Google account.";
            GoogleAuthButton.Content = "Sign in with Google";
        }
    }

    private async void GoogleAuth_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.GoogleAuth.IsSignedIn)
        {
            _vm.GoogleAuth.SignOut();
            UpdateGoogleUi();
            return;
        }

        GoogleAuthButton.IsEnabled = false;
        GoogleStatusText.Text = "Waiting for the browser — approve access there…";
        try
        {
            await _vm.GoogleAuth.SignInAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Google sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            GoogleAuthButton.IsEnabled = true;
            UpdateGoogleUi();
        }
    }

    private static double ParseLevel(string text, double fallback)
    {
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            double.IsFinite(v))
            return Math.Clamp(v, 0, 140);
        return fallback;
    }

    private void SplOffsetUp_Click(object sender, RoutedEventArgs e)
    {
        _vm.Spl.OffsetUpCommand.Execute(null);
        SplOffsetBox.Text = _vm.Spl.Offset.ToString("0.#", CultureInfo.CurrentCulture);
    }

    private void SplOffsetDown_Click(object sender, RoutedEventArgs e)
    {
        _vm.Spl.OffsetDownCommand.Execute(null);
        SplOffsetBox.Text = _vm.Spl.Offset.ToString("0.#", CultureInfo.CurrentCulture);
    }

    private void SplOffset_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (double.TryParse(SplOffsetBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            double.IsFinite(v))
            _vm.Spl.Offset = Math.Clamp(v, -200, 200);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            _vm.Spl.Offset = _originalOffset; // cancelled — undo the live calibration trim
        base.OnClosed(e);
    }
}
