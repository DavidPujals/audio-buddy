using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NovaSetlist;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.UpdateService.CleanupLeftovers();

        // This runs a live service: an unexpected exception in a button handler or a
        // dialog must be logged and swallowed, not take the whole app down mid-song.
        DispatcherUnhandledException += (_, args) =>
        {
            Log("UI thread", args.Exception);
            if (Windows.Count > 0)
            {
                args.Handled = true;
                return;
            }
            // Nothing on screen yet (main window failed to build): swallowing would leave an
            // invisible process — say so and quit instead.
            args.Handled = true;
            MessageBox.Show($"Audio Buddy couldn't start.\n\n{args.Exception.Message}\n\nDetails: {CrashLog}",
                "Audio Buddy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("background task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("fatal", args.ExceptionObject as Exception);
    }

    private static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            // Keep the log from growing without bound across months of services.
            if (File.Exists(CrashLog) && new FileInfo(CrashLog).Length > 512 * 1024)
                File.Delete(CrashLog);
            File.AppendAllText(CrashLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
