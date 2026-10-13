namespace NovaSetlist.Models;

/// <summary>One row of the sheet's Playback tab: the song ID Playback reports on the wire with
/// its title, key, tempo and play length. The tab is the shared database that names Playback's
/// songs on every machine.</summary>
public sealed class PlaybackRow
{
    public long SetlistSongId { get; set; }
    public string Title { get; set; } = "";
    public string Key { get; set; } = "";
    public string Bpm { get; set; } = "";

    /// <summary>Play length as text ("2:37"); "" = unknown.</summary>
    public string Length { get; set; } = "";
}
