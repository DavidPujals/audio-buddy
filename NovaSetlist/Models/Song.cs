using System.Text.Json.Serialization;

namespace NovaSetlist.Models;

public sealed class Song
{
    public string Name { get; set; } = "";
    public string DefaultKey { get; set; } = "";

    /// <summary>Song length as written in the sheet ("3:45", "1:02:30", "0:03:45:12"); "" = unknown.</summary>
    public string Length { get; set; } = "";

    /// <summary>Tempo as written in the sheet ("72"); "" = unknown.</summary>
    public string Bpm { get; set; } = "";

    /// <summary>Sheet column E: the song has no fixed key.</summary>
    public bool Chromatic { get; set; }

    /// <summary>Sheet column F: key the song changes to mid-song; "" = no key change.</summary>
    public string KeyChangeKey { get; set; } = "";

    /// <summary>Sheet column G: timecode position of the key change ("1:45"); "" = unknown.</summary>
    public string KeyChangeAt { get; set; } = "";

    /// <summary>Key as shown in search results: "C→D", "A·Chr" or the default key.</summary>
    [JsonIgnore]
    public string KeyLabel =>
        KeyChangeKey.Length > 0 ? $"{DefaultKey}→{KeyChangeKey}"
        : Chromatic ? $"{DefaultKey}·Chr"
        : DefaultKey;
}
