using Newtonsoft.Json;

namespace L2Launcher.Core;

/// <summary>
/// Server-side settings shipped by the admin in "launcher.json" next to the exe.
/// Only ManifestUrl is required. Everything must be https.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Default used when launcher.json is absent. Change before building a release (BuildDefaults).</summary>
    public string ManifestUrl { get; set; } = BuildDefaults.ManifestUrl;
    public string ServerName { get; set; } = BuildDefaults.ServerName;
    /// <summary>Client folder. Empty = folder of the launcher exe.</summary>
    public string GamePath { get; set; } = "";
    public string GameExecutable { get; set; } = "system/l2.exe";
    public string GameArguments { get; set; } = "";
    /// <summary>Optional https endpoint called right before starting the game (future login gate). Empty = disabled.</summary>
    public string PreLaunchUrl { get; set; } = "";
    /// <summary>Allow Play when the update server is unreachable (the local client is then unverified).</summary>
    public bool AllowOfflinePlay { get; set; } = false;
    public bool CloseOnLaunch { get; set; } = true;
    public string WebsiteUrl { get; set; } = BuildDefaults.WebsiteUrl;
}

/// <summary>Per-user preferences (%LOCALAPPDATA%\L2Launcher\prefs.json).</summary>
public sealed class UserPrefs
{
    public string Language { get; set; } = "";
    /// <summary>Selected server id from manifest.servers ("" = manifest default).</summary>
    public string Server { get; set; } = "";
}

public static class BuildDefaults
{
    public const string ManifestUrl = "https://updates.example.com/l2/manifest.json";
    public const string ServerName = "L2Start";
    public const string WebsiteUrl = "https://l2start.ru";
}

public static class Settings
{
    public const string ConfigFileName = "launcher.json";

    public static string LocalDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "L2Launcher");

    public static string ExeDir => AppContext.BaseDirectory;

    public static (LauncherConfig cfg, string? source, List<string> warnings) LoadConfig(string? dir = null)
    {
        var warnings = new List<string>();
        var path = Path.Combine(dir ?? ExeDir, ConfigFileName);
        var cfg = new LauncherConfig();
        string? source = null;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonConvert.DeserializeObject<LauncherConfig>(File.ReadAllText(path)) ?? new LauncherConfig();
                source = path;
            }
            catch (Exception ex) { warnings.Add($"launcher.json invalid: {ex.Message}"); cfg = new LauncherConfig(); }
        }
        if (!IsAllowedUrl(cfg.ManifestUrl))
        {
            warnings.Add($"ManifestUrl must be https: '{cfg.ManifestUrl}' - using default.");
            cfg.ManifestUrl = BuildDefaults.ManifestUrl;
        }
        else if (IsLoopbackHttp(cfg.ManifestUrl))
            warnings.Add($"TEST MODE: plain http update host on loopback ({cfg.ManifestUrl}).");
        if (!string.IsNullOrWhiteSpace(cfg.PreLaunchUrl) && !IsAllowedUrl(cfg.PreLaunchUrl))
        {
            warnings.Add($"PreLaunchUrl must be https: '{cfg.PreLaunchUrl}' - disabled.");
            cfg.PreLaunchUrl = "";
        }
        if (string.IsNullOrWhiteSpace(cfg.GamePath)) cfg.GamePath = dir ?? ExeDir;
        else if (!Path.IsPathRooted(cfg.GamePath)) cfg.GamePath = Path.GetFullPath(Path.Combine(dir ?? ExeDir, cfg.GamePath));
        return (cfg, source, warnings);
    }

    public static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Test exception: plain http is accepted ONLY for loopback hosts (127.0.0.0/8, ::1, "localhost"),
    /// so a local test update host can be used without certificates. Everything else must be https.
    /// The manifest signature is still required, so a loopback server cannot push unsigned files.
    /// </summary>
    public static bool IsLoopbackHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(u);

    public static bool IsLoopbackHost(Uri u) =>
        u.IsLoopback && (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
                         || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowedUrl(string? url) => IsHttps(url) || IsLoopbackHttp(url);

    private static string PrefsFile => Path.Combine(LocalDataDir, "prefs.json");

    public static UserPrefs LoadPrefs()
    {
        try { if (File.Exists(PrefsFile)) return JsonConvert.DeserializeObject<UserPrefs>(File.ReadAllText(PrefsFile)) ?? new(); }
        catch { }
        return new UserPrefs();
    }

    public static void SavePrefs(UserPrefs p)
    {
        try { Directory.CreateDirectory(LocalDataDir); File.WriteAllText(PrefsFile, JsonConvert.SerializeObject(p, Formatting.Indented)); }
        catch { }
    }
}
