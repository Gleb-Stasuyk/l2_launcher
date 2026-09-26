namespace L2Launcher.Core;

/// <summary>
/// EN/RU without the Korea slot: the client always runs in the English slot with its own localization.ini
/// (chat-fix Cyrillic font lines) which the launcher never touches. Text files that differ between languages
/// (Russian versions carry the English *-e.dat names) are shipped as lang/en/... and lang/ru/...; the selected
/// set is copied into system\ before launch. The active language is tracked in lang/active.txt and confirmed
/// by hash, so a copy happens only when something differs.
/// </summary>
public static class LanguageService
{
    public const string StateFile = "lang/active.txt";

    public static string Code(GameLanguage l) => l == GameLanguage.Russian ? "ru" : "en";
    public static string SetPath(string code, string target) => $"lang/{code}/{target.Replace('\\', '/')}";

    public static bool HasLanguages(Manifest m) => m.Languages != null && m.Languages.Files.Count > 0;

    /// <summary>Expected sha256/size for a target file in the given language (from the lang/&lt;code&gt;/ entry).</summary>
    public static ManifestFile? Expected(Manifest m, string code, string target)
    {
        var want = PathSafety.Normalize(SetPath(code, target));
        return m.Files.FirstOrDefault(f => PathSafety.Normalize(f.Path) == want);
    }

    public static string? ReadActive(string gamePath)
    {
        try
        {
            var p = PathSafety.ResolveSafePath(gamePath, StateFile);
            return File.Exists(p) ? File.ReadAllText(p).Trim().ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    public static void WriteActive(string gamePath, string code)
    {
        var p = PathSafety.ResolveSafePath(gamePath, StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
    }

    /// <summary>Target files in system\ whose content does not match the selected language (missing counts too).</summary>
    public static List<string> Mismatched(string gamePath, Manifest m, GameLanguage lang, HashCache cache, bool full)
    {
        var res = new List<string>();
        if (!HasLanguages(m)) return res;
        var code = Code(lang);
        foreach (var t in m.Languages!.Files)
        {
            var exp = Expected(m, code, t) ?? throw new LauncherException("bad_manifest", $"Language set '{code}' has no {t}");
            var dst = PathSafety.ResolveSafePath(gamePath, t);
            if (!File.Exists(dst) || new FileInfo(dst).Length != exp.Size ||
                !string.Equals(cache.GetHash(dst, PathSafety.Normalize(t), full), exp.Sha256, StringComparison.OrdinalIgnoreCase))
                res.Add(t);
        }
        return res;
    }

    /// <summary>
    /// Makes system\ match the selected language: copies differing files from lang/&lt;code&gt;/ (after checking the
    /// source hash), verifies the result, writes lang/active.txt. Returns the number of files copied.
    /// Throws LauncherException("lang_set_corrupt") if a source file is missing/damaged (run "Check files").
    /// </summary>
    public static int Apply(string gamePath, Manifest m, GameLanguage lang, HashCache cache, Action<string>? log = null, bool full = false)
    {
        if (!HasLanguages(m)) return 0;
        var code = Code(lang);
        var todo = ReadActive(gamePath) == code ? Mismatched(gamePath, m, lang, cache, full) : m.Languages!.Files.ToList();
        int copied = 0;
        foreach (var t in todo)
        {
            var exp = Expected(m, code, t) ?? throw new LauncherException("bad_manifest", $"Language set '{code}' has no {t}");
            var src = PathSafety.ResolveSafePath(gamePath, SetPath(code, t));
            var dst = PathSafety.ResolveSafePath(gamePath, t);
            if (!File.Exists(src) || new FileInfo(src).Length != exp.Size ||
                !string.Equals(cache.GetHash(src, PathSafety.Normalize(SetPath(code, t)), full), exp.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("lang_set_corrupt", $"Language file missing or damaged: {SetPath(code, t)}");
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
                throw new LauncherException("lang_set_corrupt", $"Copy of {t} does not match the manifest");
            }
            if (File.Exists(dst))
            {
                var a = File.GetAttributes(dst);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, a & ~FileAttributes.ReadOnly);
            }
            File.Move(part, dst, overwrite: true);
            cache.Remember(dst, PathSafety.Normalize(t), exp.Sha256.ToLowerInvariant());
            log?.Invoke($"lang {code}: {t}");
            copied++;
        }
        WriteActive(gamePath, code);
        cache.Save();
        return copied;
    }

    /// <summary>Deletes files listed in manifest.obsolete (only those; never protected, managed or language files).</summary>
    public static int DeleteObsolete(string gamePath, Manifest m, Action<string>? log = null)
    {
        var managed = new HashSet<string>(m.Files.Select(f => PathSafety.Normalize(f.Path)));
        if (m.Languages != null) managed.UnionWith(m.Languages.Files.Select(PathSafety.Normalize));
        int n = 0;
        foreach (var o in m.Obsolete)
        {
            var key = PathSafety.Normalize(o);
            if (managed.Contains(key) || UserConfigFiles.IsProtected(o, m.UserConfig) || key == PathSafety.Normalize(StateFile)) continue;
            var p = PathSafety.ResolveSafePath(gamePath, o);
            if (!File.Exists(p)) continue;
            try { File.Delete(p); n++; log?.Invoke($"deleted obsolete {o}"); }
            catch (Exception ex) { log?.Invoke($"cannot delete {o}: {ex.Message}"); }
        }
        return n;
    }
}
