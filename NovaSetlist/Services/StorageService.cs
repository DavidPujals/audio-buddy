using System.IO;
using System.Text.Json;
using NovaSetlist.Models;

namespace NovaSetlist.Services;

/// <summary>Persists the sheet cache and the in-progress service to %APPDATA%\NovaSetlist.</summary>
public sealed class StorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSetlist");

    private string CachePath => Path.Combine(_dir, "cache.json");
    private string CurrentPath => Path.Combine(_dir, "current.json");
    private string WindowPath => Path.Combine(_dir, "window.json");

    public CacheData? LoadCache() => Load<CacheData>(CachePath);
    public void SaveCache(CacheData cache) => Save(CachePath, cache);

    public ServiceSet? LoadCurrent() => Load<ServiceSet>(CurrentPath);
    public void SaveCurrent(ServiceSet set) => Save(CurrentPath, set);

    public WindowPlacement? LoadWindow() => Load<WindowPlacement>(WindowPath);
    public void SaveWindow(WindowPlacement placement) => Save(WindowPath, placement);

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
                    set.Items ??= new();
                    foreach (var i in set.Items)
                    {
                        i.Name ??= ""; i.SelectedKey ??= ""; i.Leader ??= ""; i.Color ??= "";
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

    private void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // Write-then-rename: a crash or power cut mid-write corrupts only the
            // temp file, never the live one (a corrupt current.json = lost service).
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Saving is best-effort; a failed write must never take the UI down.
        }
    }
}
