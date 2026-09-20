using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSetlist.Audio;
using NovaSetlist.Services;

namespace NovaSetlist.ViewModels;

/// <summary>
/// The live SPL(A) meter panel: pick a measurement-mic input in Settings, see the
/// A-weighted level. The calibration offset turns the digital level into real
/// dB SPL (trim it until the readout matches a reference meter); yellow/red
/// thresholds recolour the number as the room gets louder (green below yellow).
/// </summary>
public partial class SplViewModel : ObservableObject, IDisposable
{
    public const string OffDevice = "No input — off";

    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private SplMeter? _meter;
    private bool _loading = true; // suppress config writes while restoring saved state

    public ObservableCollection<string> Devices { get; } = new();

    [ObservableProperty]
    private string selectedDevice = OffDevice;

    /// <summary>Whether the SPL section shows in the side panel at all.</summary>
    [ObservableProperty]
    private bool isEnabled;

    /// <summary>Calibration: dB added to the input level to match a reference meter.</summary>
    [ObservableProperty]
    private double offset = 100;

    [ObservableProperty]
    private double yellowFrom = 85;

    [ObservableProperty]
    private double redFrom = 95;

    /// <summary>Time weighting: false = Slow (1 s), true = Fast (125 ms).</summary>
    [ObservableProperty]
    private bool fastResponse;

    [ObservableProperty]
    private string splText = "—";

    /// <summary>"off", "green", "yellow" or "red" — drives the readout colour.</summary>
    [ObservableProperty]
    private string splState = "off";

    [ObservableProperty]
    private string splSub = "dB(A)";

    public SplViewModel(AppConfig config)
    {
        _config = config;
        RefreshDevices();

        Offset = Math.Clamp(double.IsFinite(config.SplOffset) ? config.SplOffset : 100, -200, 200);
        YellowFrom = Math.Clamp(double.IsFinite(config.SplYellow) ? config.SplYellow : 85, 0, 140);
        RedFrom = Math.Clamp(double.IsFinite(config.SplRed) ? config.SplRed : 95, 0, 140);
        FastResponse = config.SplFast;
        if (config.SplDevice.Length > 0 && Devices.Contains(config.SplDevice))
            SelectedDevice = config.SplDevice;
        IsEnabled = config.SplEnabled;
        _loading = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Poll();
        RestartMeter();
    }

    public void RefreshDevices()
    {
        var names = new List<string> { OffDevice };
        names.AddRange(WdmInput.DeviceNames());

        // A device that's unplugged right now stays listed and selected — dropping it here
        // would persist "" and silently forget the choice the moment Settings opens.
        var keep = SelectedDevice;
        var present = names.Contains(keep);
        if (keep != OffDevice && !present)
            names.Add(keep);

        if (!names.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var n in names)
                Devices.Add(n);
            SelectedDevice = keep;
        }
        // Re-plugged since the input died? Same name, so no change event — restart it here.
        if (keep != OffDevice && present && _meter is null && IsEnabled && !_loading)
            RestartMeter();
    }

    [RelayCommand]
    private void OffsetUp() => Offset = Math.Clamp(Math.Round(Offset + 1, 1), -200, 200);

    [RelayCommand]
    private void OffsetDown() => Offset = Math.Clamp(Math.Round(Offset - 1, 1), -200, 200);

    // ---------- change handling ----------

    partial void OnSelectedDeviceChanged(string value)
    {
        Persist(c => c.SplDevice = value == OffDevice ? "" : value);
        RestartMeter();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Persist(c => c.SplEnabled = value);
        RestartMeter();
    }

    partial void OnOffsetChanged(double value) => Persist(c => c.SplOffset = value);
    partial void OnYellowFromChanged(double value) => Persist(c => c.SplYellow = value);
    partial void OnRedFromChanged(double value) => Persist(c => c.SplRed = value);

    partial void OnFastResponseChanged(bool value)
    {
        Persist(c => c.SplFast = value);
        _meter?.SetFastResponse(value);
        UpdateSub();
    }

    private DispatcherTimer? _configSaveTimer;

    /// <summary>Applies the change now, writes appsettings.json a moment later — the offset box
    /// and ±1 dB buttons fire per keystroke/click, and each save is a file write + rename.</summary>
    private void Persist(Action<AppConfig> apply)
    {
        if (_loading)
            return;
        apply(_config);
        _configSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _configSaveTimer.Tick -= OnConfigSaveTick;
        _configSaveTimer.Tick += OnConfigSaveTick;
        _configSaveTimer.Stop();
        _configSaveTimer.Start();
    }

    private void OnConfigSaveTick(object? sender, EventArgs e)
    {
        _configSaveTimer!.Stop();
        _config.Save();
    }

    /// <summary>Writes any pending appsettings.json change now (window close).</summary>
    public void FlushConfig()
    {
        if (_configSaveTimer is { IsEnabled: true })
        {
            _configSaveTimer.Stop();
            _config.Save();
        }
    }

    // ---------- metering ----------

    private void RestartMeter()
    {
        if (_loading)
            return;
        _meter?.Dispose();
        _meter = null;
        SplText = "—";
        SplState = "off";
        UpdateSub();

        if (!IsEnabled || string.IsNullOrEmpty(SelectedDevice) || SelectedDevice == OffDevice)
        {
            _timer?.Stop();
            return;
        }

        try
        {
            var index = WdmInput.FindDevice(SelectedDevice);
            if (index < 0)
            {
                SplSub = "input not found";
                return;
            }
            _meter = new SplMeter(index, FastResponse);
            _meter.Start();
            _timer?.Start();
        }
        catch (Exception)
        {
            _meter?.Dispose();
            _meter = null;
            SplSub = "couldn't open the input";
        }
    }

    private void Poll()
    {
        var m = _meter;
        if (m is null)
        {
            _timer.Stop();
            return;
        }

        if (m.Error is { } error)
        {
            _meter = null;
            m.Dispose();
            _timer.Stop();
            SplText = "—";
            SplState = "off";
            SplSub = error;
            return;
        }

        var dbfs = m.LevelDbfs;
        if (dbfs < -90) // digital silence / nothing metered yet
        {
            SplText = "—";
            SplState = "off";
            return;
        }

        var spl = Math.Clamp(dbfs + Offset, 0, 199.9);
        SplText = spl.ToString("0.0");
        SplState = spl >= RedFrom ? "red" : spl >= YellowFrom ? "yellow" : "green";
    }

    private void UpdateSub() => SplSub = FastResponse ? "dB(A) · fast" : "dB(A) · slow";

    public void Dispose()
    {
        _timer?.Stop();
        _meter?.Dispose();
        _meter = null;
    }
}
