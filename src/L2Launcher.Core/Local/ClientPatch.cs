using System.IO.Compression;
using System.Security.Cryptography;

namespace L2Launcher.Core.Local;

/// <summary>
/// Client patch for the local server: l2start-client-patch.zip next to the launcher (Release asset). Entries are client-relative paths
/// (system/l2.ini -> 127.0.0.1, system files such as the return-skill icon); entries under lang/en/ and lang/ru/ go to the same
/// path for the chosen game language. Originals are backed up once into &lt;client&gt;\l2start_backup\.
/// </summary>
public static class ClientPatch
{
    public const string FileName = "l2start-client-patch.zip";
    public const string BackupDir = "l2start_backup";

    /// <summary>(target relative path, zip entry) for the language "en"/"ru".</summary>
    public static List<(string target, ZipArchiveEntry entry)> Plan(ZipArchive zip, string lang)
    {
        var list = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/")))
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.EndsWith("/")) continue;
            if (n.StartsWith("lang/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = n.Split('/', 3);
                if (parts.Length == 3 && parts[1].Equals(lang, StringComparison.OrdinalIgnoreCase)) list[parts[2]] = e;
            }
            else if (!list.ContainsKey(n)) list[n] = e;
        }
        return list.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    static string Sha(Stream s) => Convert.ToHexString(SHA256.HashData(s));

    /// <summary>Files that differ from the patch.</summary>
    public static List<string> Mismatched(string gamePath, string zipPath, string lang)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var bad = new List<string>();
        foreach (var (t, e) in Plan(zip, lang))
        {
            var p = PathSafety.ResolveSafePath(gamePath, t);
            if (!File.Exists(p)) { bad.Add(t); continue; }
            using var a = File.OpenRead(p);
            using var b = e.Open();
            if (Sha(a) != Sha(b)) bad.Add(t);
        }
        return bad;
    }

    /// <summary>Copies changed files; returns the count. Backs up an original once.</summary>
    public static int Apply(string gamePath, string zipPath, string lang, Action<string>? log = null)
    {
        var todo = Mismatched(gamePath, zipPath, lang);
        if (todo.Count == 0) return 0;
        using var zip = ZipFile.OpenRead(zipPath);
        var plan = Plan(zip, lang).ToDictionary(x => x.target, x => x.entry, StringComparer.OrdinalIgnoreCase);
        foreach (var t in todo)
        {
            var dst = PathSafety.ResolveSafePath(gamePath, t);
            var bak = PathSafety.ResolveSafePath(Path.Combine(gamePath, BackupDir), t);
            if (File.Exists(dst) && !File.Exists(bak))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Copy(dst, bak);
                log?.Invoke($"backup {t}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var tmp = dst + ".part";
            using (var src = plan[t].Open()) using (var fs = File.Create(tmp)) src.CopyTo(fs);
            if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
            File.Move(tmp, dst, overwrite: true);
            log?.Invoke($"patched {t}");
        }
        return todo.Count;
    }

    /// <summary>Puts the backed-up originals back (e.g. to play on another server).</summary>
    public static int Restore(string gamePath)
    {
        var root = Path.Combine(gamePath, BackupDir);
        if (!Directory.Exists(root)) return 0;
        int n = 0;
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            var dst = PathSafety.ResolveSafePath(gamePath, rel);
            File.Copy(f, dst, overwrite: true);
            n++;
        }
        return n;
    }
}
