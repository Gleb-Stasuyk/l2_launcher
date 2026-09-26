namespace L2Launcher.Core;

/// <summary>
/// A group of interchangeable file sets ("variants"): e.g. languages (lang/en, lang/ru) or servers
/// (server/main, server/local). Each variant ships its files as normal manifest entries under
/// "&lt;prefix&gt;/&lt;id&gt;/&lt;target&gt;"; the selected variant is copied onto the target paths (e.g. system/l2.ini).
/// The active id is tracked in a small state file and confirmed by hash, so copies happen only when needed.
/// </summary>
public sealed record VariantGroup(string Prefix, string StateFile, IReadOnlyList<string> Ids, IReadOnlyList<string> Targets, string CorruptCode)
{
    public string SetPath(string id, string target) => $"{Prefix}/{id}/{target.Replace('\\', '/')}";
}

public static class VariantService
{
    public static ManifestFile? Expected(Manifest m, VariantGroup g, string id, string target)
    {
        var want = PathSafety.Normalize(g.SetPath(id, target));
        return m.Files.FirstOrDefault(f => PathSafety.Normalize(f.Path) == want);
    }

    public static string? ReadActive(string gamePath, string stateFile)
    {
        try
        {
            var p = PathSafety.ResolveSafePath(gamePath, stateFile);
            return File.Exists(p) ? File.ReadAllText(p).Trim().ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    public static void WriteActive(string gamePath, string stateFile, string id)
    {
        var p = PathSafety.ResolveSafePath(gamePath, stateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, id);
    }

    private static ManifestFile Need(Manifest m, VariantGroup g, string id, string t) =>
        Expected(m, g, id, t) ?? throw new LauncherException("bad_manifest", $"Set '{g.Prefix}/{id}' has no {t}");

    /// <summary>Target files whose content does not match the selected variant (missing counts too).</summary>
    public static List<string> Mismatched(string gamePath, Manifest m, VariantGroup g, string id, HashCache cache, bool full)
    {
        var res = new List<string>();
        foreach (var t in g.Targets)
        {
            var exp = Need(m, g, id, t);
            var dst = PathSafety.ResolveSafePath(gamePath, t);
            if (!File.Exists(dst) || new FileInfo(dst).Length != exp.Size ||
                !string.Equals(cache.GetHash(dst, PathSafety.Normalize(t), full), exp.Sha256, StringComparison.OrdinalIgnoreCase))
                res.Add(t);
        }
        return res;
    }

    /// <summary>
    /// Makes the targets match variant <paramref name="id"/>: copies differing files from the set folder (after checking
    /// the source hash) via "*.part", verifies, then writes the state file. Returns the number of files copied.
    /// Throws LauncherException(g.CorruptCode) if a source file is missing/damaged (run "Check files").
    /// </summary>
    public static int Apply(string gamePath, Manifest m, VariantGroup g, string id, HashCache cache, Action<string>? log = null, bool full = false)
    {
        if (g.Targets.Count == 0) return 0;
        if (!g.Ids.Contains(id)) throw new LauncherException("bad_manifest", $"Unknown set '{g.Prefix}/{id}'");
        var todo = ReadActive(gamePath, g.StateFile) == id ? Mismatched(gamePath, m, g, id, cache, full) : g.Targets.ToList();
        int copied = 0;
        foreach (var t in todo)
        {
            var exp = Need(m, g, id, t);
            var srcRel = g.SetPath(id, t);
            var src = PathSafety.ResolveSafePath(gamePath, srcRel);
            var dst = PathSafety.ResolveSafePath(gamePath, t);
            if (!File.Exists(src) || new FileInfo(src).Length != exp.Size ||
                !string.Equals(cache.GetHash(src, PathSafety.Normalize(srcRel), full), exp.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException(g.CorruptCode, $"File missing or damaged: {srcRel}");
            if (File.Exists(dst) && new FileInfo(dst).Length == exp.Size &&
                string.Equals(FileIntegrity.Sha256(dst), exp.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                cache.Remember(dst, PathSafety.Normalize(t), exp.Sha256.ToLowerInvariant());
                continue; // already correct
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var part = dst + ".part";
            File.Copy(src, part, overwrite: true);
            if (!string.Equals(FileIntegrity.Sha256(part), exp.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                UpdateService.TryDelete(part);
                throw new LauncherException(g.CorruptCode, $"Copy of {t} does not match the manifest");
            }
            if (File.Exists(dst))
            {
                var a = File.GetAttributes(dst);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, a & ~FileAttributes.ReadOnly);
            }
            File.Move(part, dst, overwrite: true);
            cache.Remember(dst, PathSafety.Normalize(t), exp.Sha256.ToLowerInvariant());
            log?.Invoke($"{g.Prefix} {id}: {t}");
            copied++;
        }
        WriteActive(gamePath, g.StateFile, id);
        cache.Save();
        return copied;
    }
}
