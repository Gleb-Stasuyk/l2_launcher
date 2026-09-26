using System.Text.RegularExpressions;

namespace L2Launcher.Core;

/// <summary>
/// Server selection (data-driven from manifest.servers). Each server ships its own copies of the target files
/// (e.g. system/l2.ini with the login address) under server/&lt;id&gt;/...; the chosen one is copied onto the targets
/// before launch, only when changed (state file server/active.txt + hash check).
/// </summary>
public static class ServerService
{
    public const string StateFile = "server/active.txt";
    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

    public static bool IsValidId(string id) => IdRx.IsMatch(id ?? "");
    public static bool HasServers(Manifest m) => m.Servers != null && m.Servers.List.Count > 0 && m.Servers.Files.Count > 0;
    public static string SetPath(string id, string target) => $"server/{id}/{target.Replace('\\', '/')}";

    public static VariantGroup Group(Manifest m) =>
        new("server", StateFile, m.Servers?.List.Select(s => s.Id).ToList() ?? new List<string>(),
            m.Servers?.Files ?? new List<string>(), "server_set_corrupt");

    /// <summary>Servers shown to the player (hidden ones are excluded).</summary>
    public static List<ServerEntry> Visible(Manifest m) => m.Servers?.List.Where(s => !s.Hidden).ToList() ?? new List<ServerEntry>();

    /// <summary>The server to use: the preferred id if it exists and is visible, else the manifest default.</summary>
    public static string Resolve(Manifest m, string? preferred)
    {
        if (!HasServers(m)) return "";
        var p = (preferred ?? "").Trim().ToLowerInvariant();
        if (Visible(m).Any(s => s.Id == p)) return p;
        return m.Servers!.Default;
    }

    public static string DisplayName(ServerEntry s, bool russian)
    {
        string? n = null;
        if (s.Name != null && (s.Name.TryGetValue(russian ? "ru" : "en", out n) || s.Name.TryGetValue("en", out n)) && !string.IsNullOrWhiteSpace(n)) return n!;
        return s.Id;
    }

    public static int Apply(string gamePath, Manifest m, string id, HashCache cache, Action<string>? log = null, bool full = false) =>
        HasServers(m) ? VariantService.Apply(gamePath, m, Group(m), id, cache, log, full) : 0;

    public static List<string> Mismatched(string gamePath, Manifest m, string id, HashCache cache, bool full) =>
        HasServers(m) ? VariantService.Mismatched(gamePath, m, Group(m), id, cache, full) : new List<string>();

    public static string? ReadActive(string gamePath) => VariantService.ReadActive(gamePath, StateFile);

    /// <summary>Validation used by UpdateService.ParseAndVerify. <paramref name="seen"/> = normalized normal file paths.</summary>
    internal static void Validate(Manifest m, HashSet<string> seen)
    {
        var s = m.Servers;
        if (s == null) return;
        if (s.List.Count == 0) throw new LauncherException("bad_manifest", "Server list is empty");
        var ids = new HashSet<string>();
        foreach (var e in s.List)
        {
            if (!IsValidId(e.Id)) throw new LauncherException("bad_manifest", $"Bad server id: {e.Id}");
            if (!ids.Add(e.Id)) throw new LauncherException("bad_manifest", $"Duplicate server id: {e.Id}");
        }
        var def = s.List.FirstOrDefault(e => e.Id == s.Default);
        if (def == null || def.Hidden) throw new LauncherException("bad_manifest", $"Default server '{s.Default}' is missing or hidden");
        var langTargets = new HashSet<string>((m.Languages?.Files ?? new List<string>()).Select(PathSafety.Normalize));
        foreach (var t in s.Files)
        {
            PathSafety.ResolveSafePath(Path.GetTempPath(), t);
            var n = PathSafety.Normalize(t);
            if (seen.Contains(n)) throw new LauncherException("bad_manifest", $"Server file also listed as normal file: {t}");
            if (langTargets.Contains(n)) throw new LauncherException("bad_manifest", $"Server file also listed as language file: {t}");
            if (UserConfigFiles.IsProtected(t, m.UserConfig)) throw new LauncherException("bad_manifest", $"Server file is a protected user file: {t}");
            foreach (var id in ids)
                if (!seen.Contains(PathSafety.Normalize(SetPath(id, t))))
                    throw new LauncherException("bad_manifest", $"Server '{id}' is missing {t}");
        }
    }
}
