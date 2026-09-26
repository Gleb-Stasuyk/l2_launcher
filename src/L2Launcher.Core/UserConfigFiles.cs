namespace L2Launcher.Core;

/// <summary>
/// Files the launcher never overwrites or "repairs", even if a manifest lists them (defense in depth).
/// These hold per-player settings: options/language (Option.ini), key bindings (User.ini), window layout.
/// </summary>
public static class UserConfigFiles
{
    public static readonly string[] BuiltIn =
    {
        "system/option.ini",
        "system/user.ini",
        "system/windowsinfo.ini",
    };

    public static bool IsProtected(string relativePath, IEnumerable<string>? manifestUserConfig = null)
    {
        var n = PathSafety.Normalize(relativePath);
        if (n.EndsWith(".log") || n.EndsWith(".part")) return true;
        if (BuiltIn.Contains(n)) return true;
        return manifestUserConfig?.Any(u => PathSafety.Normalize(u) == n) == true;
    }
}
