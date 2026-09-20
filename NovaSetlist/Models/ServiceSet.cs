namespace NovaSetlist.Models;

public sealed class SetItemDto
{
    public string Name { get; set; } = "";
    public string SelectedKey { get; set; } = "";
    public string Leader { get; set; } = "";

    /// <summary>Row colour-coding token from RowColors.Tokens; "" = none.</summary>
    public string Color { get; set; } = "";

    /// <summary>Song length from the sheet; "" = unknown.</summary>
    public string Length { get; set; } = "";

    /// <summary>Tempo from the sheet ("72"); "" = unknown.</summary>
    public string Bpm { get; set; } = "";

    /// <summary>True once the song has been played this service (row greys out).</summary>
    public bool Completed { get; set; }

    /// <summary>The song has no fixed key (sheet column E).</summary>
    public bool Chromatic { get; set; }

    /// <summary>The song changes key mid-way (editor toggle).</summary>
    public bool HasKeyChange { get; set; }

    /// <summary>Key it changes to (sheet column F); kept locally while HasKeyChange is off until the next sync.</summary>
    public string KeyChangeKey { get; set; } = "";

    /// <summary>Timecode position of the change, e.g. "1:45" (sheet column G).</summary>
    public string KeyChangeAt { get; set; } = "";

    /// <summary>True if a Chromatic / key-change edit is still waiting to reach the sheet.</summary>
    public bool SheetDirty { get; set; }

    /// <summary>The pending write is a plain manual add (append if missing), not a key-detail edit.</summary>
    public bool SheetAddOnly { get; set; }
}

public sealed class ServiceSet
{
    public List<SetItemDto> Items { get; set; } = new();
    public DateTime? ServiceDate { get; set; }
}
