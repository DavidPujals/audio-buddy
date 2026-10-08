using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaSetlist.ViewModels;

/// <summary>One row of the current service order.</summary>
public partial class SetItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int index;

    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EquivalentText))]
    private string selectedKey = "";

    [ObservableProperty]
    private string leader = "";

    /// <summary>Colour-coding token from RowColors.Tokens; "" = none.</summary>
    [ObservableProperty]
    private string color = "";

    /// <summary>Free-text note for this setlist only, e.g. who's on harmonies; "" = none.</summary>
    [ObservableProperty]
    private string note = "";

    /// <summary>Song length from the sheet ("3:45"); "" = unknown.</summary>
    [ObservableProperty]
    private string length = "";

    /// <summary>Tempo from the sheet ("72"); "" = unknown.</summary>
    [ObservableProperty]
    private string bpm = "";

    /// <summary>True while the row shows its inline editor (pencil toggled).</summary>
    [ObservableProperty]
    private bool isEditing;

    /// <summary>True while this row is the "now playing" song (session-only).</summary>
    [ObservableProperty]
    private bool isPlaying;

    /// <summary>True once the song has been played this service — the row greys out.
    /// Toggled by clicking the row number; persisted so it survives a restart.</summary>
    [ObservableProperty]
    private bool isCompleted;

    /// <summary>Chromatic flag — shown next to the key, which stays set (sheet column E).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EquivalentText), nameof(ChromaticText), nameof(KeyShort))]
    private bool isChromatic;

    /// <summary>"Song has a key change" toggle — reveals the TO / AT fields in the editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyChangeText), nameof(KeyShort))]
    private bool hasKeyChange;

    /// <summary>Key the song changes to mid-song (sheet column F).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyChangeText), nameof(KeyShort))]
    private string keyChangeKey = "";

    /// <summary>Timecode position of the key change, e.g. "1:45" (sheet column G).</summary>
    [ObservableProperty]
    private string keyChangeAt = "";

    /// <summary>A Chromatic / key-change edit hasn't reached the sheet yet (persisted, so a
    /// restart re-queues it instead of letting the next sync overwrite it).</summary>
    [ObservableProperty]
    private bool sheetDirty;

    /// <summary>The pending sheet write is a plain manual add (append if missing, adopt existing
    /// cells) rather than a key-detail edit; cleared the moment key details are edited.</summary>
    [ObservableProperty]
    private bool sheetAddOnly;

    /// <summary>Key change is on AND a target key is filled in.</summary>
    public bool KeyChangeActive => HasKeyChange && KeyChangeKey.Trim().Length > 0;

    /// <summary>Key cell text — the selected key, or "" when nothing is set.</summary>
    public string KeyText => SelectedKey;

    /// <summary>Enharmonic spelling, e.g. " (= Gb)" (leading space — it's an inline run); "" for naturals,
    /// and dropped when Chromatic is on so the cell stays short.</summary>
    public string EquivalentText =>
        !IsChromatic && Music.Keys.EquivalentOf(SelectedKey) is { } eq ? $" (= {eq})" : "";

    /// <summary>" → D" when a key change is set, else "".</summary>
    public string KeyChangeText => KeyChangeActive ? $" → {KeyChangeKey.Trim()}" : "";

    /// <summary>" CHROMATIC" tag after the key when the flag is on, else "".</summary>
    public string ChromaticText => IsChromatic ? " CHROMATIC" : "";

    /// <summary>Compact key for the narrow-row play button: "C→D", "A·Chr" or the key.</summary>
    public string KeyShort =>
        KeyChangeActive ? $"{SelectedKey}→{KeyChangeKey.Trim()}"
        : IsChromatic ? $"{SelectedKey}·Chr"
        : SelectedKey;

    partial void OnSelectedKeyChanged(string value)
    {
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(KeyShort));
    }
}
