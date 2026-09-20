using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using NovaSetlist.Services;

namespace NovaSetlist;

public partial class AboutDialog : Window
{
    private bool _installed;

    public AboutDialog()
    {
        InitializeComponent();
        Ui.Dwm.UseDarkTitleBar(this);
        VersionText.Text = "Version " + UpdateService.Format(UpdateService.CurrentVersion);
        RepoLink.NavigateUri = new Uri(UpdateService.RepoUrl);
        RepoLinkText.Text = UpdateService.RepoUrl.Replace("https://", "");
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser association / blocked by policy — the URL is shown as text anyway.
        }
        e.Handled = true;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            // Swap already happened on disk — start the new exe and bow out.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Application.Current.Shutdown();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatus.Text = "You're up to date — this is the latest version.";
            }
            else
            {
                var ver = "v" + UpdateService.Format(info.Version);
                UpdateStatus.Text = $"Downloading {ver}…";
                var progress = new Progress<double>(p =>
                    UpdateStatus.Text = $"Downloading {ver}… {p:P0}");
                await UpdateService.DownloadAndInstallAsync(info, progress);
                _installed = true;
                UpdateStatus.Text = $"{ver} installed — restart to finish.";
                UpdateButton.Content = "Restart now";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Update failed: " + ex.Message;
        }
        UpdateButton.IsEnabled = true;
    }
}
