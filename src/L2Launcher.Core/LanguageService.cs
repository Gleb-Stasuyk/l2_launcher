namespace L2Launcher.Core;

/// <summary>
/// EN/RU without the Korea slot: the client always runs in the English slot with its own localization.ini
/// (chat-fix Cyrillic font lines) which the launcher never touches. Text files that differ between languages
/// (Russian versions carry the English *-e.dat names) are shipped as lang/en/... and lang/ru/...; the selected
/// set is copied into system\ before launch (see VariantService). State: lang/active.txt, confirmed by hash.
/// </summary>
public static class LanguageService
{
    public const string StateFile = "lang/active.txt";

    public static string Code(GameLanguage l) => l == GameLanguage.Russian ? "ru" : "en";
    public static string SetPath(string code, string target) => $"lang/{code}/{target.Replace('\\', '/')}";

    public static bool HasLanguages(Manifest m) => m.Languages != null && m.Languages.Files.Count > 0;

    public static VariantGroup Group(Manifest m) =>
        new("lang", StateFile, m.Languages?.Sets ?? new List<string>(), m.Languages?.Files ?? new List<string>(), "lang_set_corrupt");

    public static ManifestFile? Expected(Manifest m, string code, string target) => VariantService.Expected(m, Group(m), code, target);
    public static string? ReadActive(string gamePath) => VariantService.ReadActive(gamePath, StateFile);
    public static void WriteActive(string gamePath, string code) => VariantService.WriteActive(gamePath, StateFile, code);

    public static List<string> Mismatched(string gamePath, Manifest m, GameLanguage lang, HashCache cache, bool full) =>
        HasLanguages(m) ? VariantService.Mismatched(gamePath, m, Group(m), Code(lang), cache, full) : new List<string>();

    public static int Apply(string gamePath, Manifest m, GameLanguage lang, HashCache cache, Action<string>? log = null, bool full = false) =>
        HasLanguages(m) ? VariantService.Apply(gamePath, m, Group(m), Code(lang), cache, log, full) : 0;

    /// <summary>Deletes files listed in manifest.obsolete (only those; never protected, managed, language or server files).</summary>
    public static int DeleteObsolete(string gamePath, Manifest m, Action<string>? log = null)
    {
        var managed = new HashSet<string>(m.Files.Select(f => PathSafety.Normalize(f.Path)));
        if (m.Languages != null) managed.UnionWith(m.Languages.Files.Select(PathSafety.Normalize));
        if (m.Servers != null) managed.UnionWith(m.Servers.Files.Select(PathSafety.Normalize));
        managed.Add(PathSafety.Normalize(StateFile));
        managed.Add(PathSafety.Normalize(ServerService.StateFile));
        int n = 0;
        foreach (var o in m.Obsolete)
        {
            var key = PathSafety.Normalize(o);
            if (managed.Contains(key) || UserConfigFiles.IsProtected(o, m.UserConfig)) continue;
            var p = PathSafety.ResolveSafePath(gamePath, o);
            if (!File.Exists(p)) continue;
            try { File.Delete(p); n++; log?.Invoke($"deleted obsolete {o}"); }
            catch (Exception ex) { log?.Invoke($"cannot delete {o}: {ex.Message}"); }
        }
        return n;
    }
}
