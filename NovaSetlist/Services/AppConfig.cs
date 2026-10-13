using System.IO;
using System.Text.Json;

namespace NovaSetlist.Services;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SpreadsheetId { get; set; } = "";
    public string SongsTab { get; set; } = "Songs";
    public string LeadersTab { get; set; } = "Leaders";

    /// <summary>OAuth client for Google sign-in; "" = use the client id built into the app.</summary>
    public string GoogleClientId { get; set; } = "";

    /// <summary>Installed-app OAuth "secret" (not confidential per Google); "" = use the built-in one.</summary>
    public string GoogleClientSecret { get; set; } = "";

    /// <summary>waveIn product name of the LTC monitor input; "" = off.</summary>
    public string TimecodeDevice { get; set; } = "";

    /// <summary>waveIn product name of the key-detection input; "" = off.</summary>
    public string KeyDetectDevice { get; set; } = "";

    /// <summary>waveIn product name of the SPL meter input; "" = off.</summary>
    public string SplDevice { get; set; } = "";

    /// <summary>Whether the SPL(A) meter section shows in the side panel.</summary>
    public bool SplEnabled { get; set; } = true;

    /// <summary>Calibration offset in dB added to the SPL input level.</summary>
    public double SplOffset { get; set; } = 100;

    /// <summary>SPL level where the readout turns yellow (green below).</summary>
    public double SplYellow { get; set; } = 85;

    /// <summary>SPL level where the readout turns red.</summary>
    public double SplRed { get; set; } = 95;

    /// <summary>Time weighting: true = Fast (125 ms), false = Slow (1 s).</summary>
    public bool SplFast { get; set; }

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>Loads appsettings.json from next to the exe; falls back to defaults if missing/invalid.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg is not null)
                {
                    cfg.SpreadsheetId = cfg.SpreadsheetId?.Trim() ?? "";
                    cfg.GoogleClientId = cfg.GoogleClientId?.Trim() ?? "";
                    cfg.GoogleClientSecret = cfg.GoogleClientSecret?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(cfg.SongsTab)) cfg.SongsTab = "Songs";
                    if (string.IsNullOrWhiteSpace(cfg.LeadersTab)) cfg.LeadersTab = "Leaders";
                    cfg.TimecodeDevice = cfg.TimecodeDevice?.Trim() ?? "";
                    cfg.KeyDetectDevice = cfg.KeyDetectDevice?.Trim() ?? "";
                    cfg.SplDevice = cfg.SplDevice?.Trim() ?? "";
                    return cfg;
                }
            }
        }
        catch
        {
            // Fall through to defaults — a bad config file must not stop the app from opening.
        }
        return new AppConfig();
    }

    /// <summary>Writes appsettings.json next to the exe. Returns false if the folder isn't writable.</summary>
    public bool Save()
    {
        try
        {
            // Write-then-rename so an interrupted write can't corrupt the live file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
