using System.Security.Cryptography;
using System.Text;

namespace L2Launcher.Core;

/// <summary>
/// Keeps the last signed manifest (bytes + signature) so the launcher can show data such as the server list at
/// startup without contacting the update server. The signature is re-verified on load; it is never used for repairs.
/// </summary>
public static class ManifestCache
{
    private static string Base(string dir, string url)
    {
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).Substring(0, 16).ToLowerInvariant();
        return Path.Combine(dir, "manifest_cache", h);
    }

    public static void Save(string dir, string url, byte[] bytes, string sig)
    {
        try
        {
            var b = Base(dir, url);
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            File.WriteAllBytes(b + ".json", bytes);
            File.WriteAllText(b + ".sig", sig);
        }
        catch { /* cache only */ }
    }

    public static Manifest? TryLoad(string dir, string url, UpdateService svc)
    {
        try
        {
            var b = Base(dir, url);
            if (!File.Exists(b + ".json") || !File.Exists(b + ".sig")) return null;
            return svc.ParseAndVerify(File.ReadAllBytes(b + ".json"), File.ReadAllText(b + ".sig"));
        }
        catch { return null; }
    }
}
