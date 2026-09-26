using Newtonsoft.Json;

namespace L2Launcher.Core;

/// <summary>
/// Quick-check cache: path -> (size, mtime, sha256). A normal start only re-hashes files whose size or
/// mtime changed; "Full check" ignores the cache. Stored per game folder in %LOCALAPPDATA%\L2Launcher.
/// (Not a security boundary: any client-side check can be bypassed; see docs/SECURITY.md.)
/// </summary>
public sealed class HashCache
{
    public sealed record Entry(long Size, long MtimeTicks, string Sha256);
    private readonly Dictionary<string, Entry> _map;
    private readonly string? _file;

    private HashCache(string? file, Dictionary<string, Entry> map) { _file = file; _map = map; }

    public static HashCache Load(string? file)
    {
        try
        {
            if (file != null && File.Exists(file))
                return new HashCache(file, JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new());
        }
        catch { }
        return new HashCache(file, new());
    }

    public static string DefaultFileFor(string gamePath)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(gamePath).ToLowerInvariant())))[..16];
        return Path.Combine(Settings.LocalDataDir, $"hashcache-{id}.json");
    }

    public string GetHash(string fullPath, string key, bool force)
    {
        var fi = new FileInfo(fullPath);
        long mt = fi.LastWriteTimeUtc.Ticks;
        if (!force && _map.TryGetValue(key, out var e) && e.Size == fi.Length && e.MtimeTicks == mt) return e.Sha256;
        var h = FileIntegrity.Sha256(fullPath);
        _map[key] = new Entry(fi.Length, mt, h);
        return h;
    }

    public void Remember(string fullPath, string key, string sha)
    {
        var fi = new FileInfo(fullPath);
        _map[key] = new Entry(fi.Length, fi.LastWriteTimeUtc.Ticks, sha);
    }

    public void Save()
    {
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonConvert.SerializeObject(_map));
        }
        catch { }
    }
}
