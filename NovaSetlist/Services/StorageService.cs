using System.IO;
using System.Text.Json;
using NovaSetlist.Models;

namespace NovaSetlist.Services;

/// <summary>Persists the sheet cache, the current setlist, setlist backups and exported
/// setlist files. App data lives in %APPDATA%\NovaSetlist.</summary>
public sealed class StorageService
{
    public const string SetlistExtension = ".setlist.json";
    private const int BackupsToKeep = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist");

    private string CachePath => Path.Combine(_dir, "cache.json");
    private string CurrentPath => Path.Combine(_dir, "current.json");
    private string WindowPath => Path.Combine(_dir, "window.json");
    public string BackupsDir => Path.Combine(_dir, "backups");

    public CacheData? LoadCache() => Load<CacheData>(CachePath);
    public void SaveCache(CacheData cache) => Save(CachePath, cache);

    public ServiceSet? LoadCurrent() => Load<ServiceSet>(CurrentPath);
    public void SaveCurrent(ServiceSet set) => Save(CurrentPath, set);

    public WindowPlacement? LoadWindow() => Load<WindowPlacement>(WindowPath);
    public void SaveWindow(WindowPlacement placement) => Save(WindowPath, placement);

    /// <summary>Reads an exported setlist file; null if missing or unreadable.</summary>
    public ServiceSet? LoadFrom(string path) => Load<ServiceSet>(path);

    /// <summary>Writes a setlist to a user-chosen path. Returns false if the write failed.</summary>
    public bool SaveTo(string path, ServiceSet set) => Save(path, set);

    /// <summary>
    /// Copies a setlist into the backups folder before it's cleared or replaced, so a
    /// "New setlist" click can never lose a service. Keeps the newest 50. Returns the
    /// backup path, or null if nothing could be written.
    /// </summary>
    public string? Backup(ServiceSet set)
    {
        try
        {
            Directory.CreateDirectory(BackupsDir);
            var label = SafeFileName(set.Name.Length > 0 ? set.Name : "setlist");
            var path = Path.Combine(BackupsDir, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {label}{SetlistExtension}");
            if (!Save(path, set))
                return null;

            foreach (var old in new DirectoryInfo(BackupsDir)
                         .GetFiles("*" + SetlistExtension)
                         .OrderByDescending(f => f.Name)
                         .Skip(BackupsToKeep))
            {
                try { old.Delete(); } catch { /* best effort */ }
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Strips characters Windows won't take in a file name.</summary>
    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "setlist";
    }

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            // A hand-edited file can carry explicit nulls; the app treats every string as non-null.
            switch (value)
            {
                case ServiceSet set:
                    set.Name ??= "";
                    set.Items ??= new();
                    set.Items.RemoveAll(i => i is null);
                    foreach (var i in set.Items)
                    {
                        i.Name ??= ""; i.SelectedKey ??= ""; i.Leader ??= ""; i.Color ??= ""; i.Note ??= "";
                        i.Length ??= ""; i.Bpm ??= ""; i.KeyChangeKey ??= ""; i.KeyChangeAt ??= "";
                    }
                    break;
                case CacheData cache:
                    cache.Songs ??= new();
                    cache.Leaders ??= new();
                    cache.Songs.RemoveAll(s => s is null);
                    cache.Leaders.RemoveAll(l => l is null);
                    foreach (var s in cache.Songs)
                    {
                        s.Name ??= ""; s.DefaultKey ??= ""; s.Length ??= ""; s.Bpm ??= "";
                        s.KeyChangeKey ??= ""; s.KeyChangeAt ??= "";
                    }
                    break;
            }
            return value;
        }
        catch
        {
            return null; // corrupt/unreadable file — behave like a fresh start rather than crash
        }
    }

    private bool Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _dir);
            // Write-then-rename: a crash or power cut mid-write corrupts only the
            // temp file, never the live one (a corrupt current.json = lost setlist).
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false; // saving is best-effort; a failed write must never take the UI down
        }
    }
}
