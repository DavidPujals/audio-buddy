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
    /// <summary>The host to connect to: a scan result ("Name  (10.1.2.3)") collapses to its address.</summary>
    public string PlaybackHost
    {
        get
        {
            var text = PlaybackHostBox.Text.Trim();
            var found = _found.FirstOrDefault(f => f.Label == text);
            return found?.Address ?? text;
        }
    }

    private List<Services.PlaybackDiscovery.Found> _found = new();
    private System.Threading.CancellationTokenSource? _scan;

    private async void FindPlayback_Click(object sender, RoutedEventArgs e)
    {
        _scan?.Cancel();
        _scan = new System.Threading.CancellationTokenSource();
        var token = _scan.Token;
        FindPlaybackButton.IsEnabled = false;
        var progress = new Progress<string>(s => PlaybackFindText.Text = "Scanning — " + s);
        try
        {
            _found = await Services.PlaybackDiscovery.ScanAsync(progress, token);
            PlaybackHostBox.ItemsSource = _found.Select(f => f.Label).ToList();
            if (_found.Count == 0)
            {
                PlaybackFindText.Text = "No Playback found on this PC's networks. Is Playback open with Allow Remote Connections on, and on the same network?";
            }
            else
            {
                PlaybackHostBox.Text = _found[0].Label;
                PlaybackFindText.Text = _found.Count == 1
                    ? $"Found Playback at {_found[0].Label} — Save & sync to connect."
                    : $"Found {_found.Count} — pick one, then Save & sync to connect.";
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer scan or the window closed
        }
        catch (Exception ex)
        {
            PlaybackFindText.Text = "Scan failed — " + ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                FindPlaybackButton.IsEnabled = true;
        }
    }

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
        PlaybackHostBox.Text = vm.Playback.Host;

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
            await _vm.GoogleAuth.SignInAsync(_closing.Token);
            // Signed in: pull the sheet and push any edits that were waiting, whether or
            // not the user goes on to press Save & sync.
            _vm.RetrySheetWrites();
            _ = _vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException)
        {
            return; // window closed mid sign-in — nothing to report
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                MessageBox.Show(this, ex.Message, "Google sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            GoogleAuthButton.IsEnabled = true;
            UpdateGoogleUi();
        }
    }

    /// <summary>Cancels a sign-in still waiting on the browser when the window closes.</summary>
    private readonly System.Threading.CancellationTokenSource _closing = new();

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
        _closing.Cancel();
        _scan?.Cancel();
        if (DialogResult != true)
            _vm.Spl.Offset = _originalOffset; // cancelled — undo the live calibration trim
        base.OnClosed(e);
    }
}
