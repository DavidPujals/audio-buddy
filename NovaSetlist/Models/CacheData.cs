namespace NovaSetlist.Models;

public sealed class CacheData
{
    public List<Song> Songs { get; set; } = new();
    public List<string> Leaders { get; set; } = new();
    public DateTime LastSynced { get; set; }
}
