namespace NovaSetlist.Models;

/// <summary>One row of the sheet's Playback tab: a MultiTracks setlist song (the ID Playback
/// reports on the wire) with its title, key, tempo, play length and setlist. The tab is the
/// shared database that names Playback's songs on every machine.</summary>
public sealed class PlaybackRow
{
    public long SetlistSongId { get; set; }
    public string Title { get; set; } = "";
    public string Key { get; set; } = "";
    public string Bpm { get; set; } = "";

    /// <summary>Play length as text ("2:37"); "" = unknown.</summary>
    public string Length { get; set; } = "";

    public string Setlist { get; set; } = "";
    public long SetlistId { get; set; }
    public string Date { get; set; } = "";
    public int Order { get; set; }
}
