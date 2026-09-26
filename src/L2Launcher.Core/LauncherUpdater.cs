// Derived from okuzpe/Lineage2Launcher Services/LauncherUpdater.cs (MIT, Copyright (c) 2025-2026 L2Titan).
// Changed: launcher release info now comes from the (already signature-verified) manifest.
using System.Diagnostics;

namespace L2Launcher.Core;

/// <summary>
/// Self-update. Trust chain: embedded public key -> manifest signature -> manifest.launcher.sha256 -> downloaded exe.
/// Rules: must be strictly newer than the running version and than the highest version ever seen (anti-rollback),
/// must be served over https from the same host as the manifest, and must match size+sha256.
/// </summary>
public sealed class LauncherUpdater
{
    private readonly UpdateService _svc;
    public LauncherUpdater(UpdateService svc) { _svc = svc; }

    public static bool IsNewer(string? serverVersion, Version current) =>
        Version.TryParse(serverVersion, out var sv) && sv > current;

    public static bool IsSameHttpsHost(string url, string referenceUrl)
    {
        try
        {
            var u = new Uri(url); var r = new Uri(referenceUrl);
            return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, r.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool ShouldUpdate(Manifest m, Version current)
    {
        var l = m.Launcher;
        if (l == null || string.IsNullOrWhiteSpace(l.Path) || !FileIntegrity.IsSha256Hex(l.Sha256)) return false;
        if (!IsNewer(l.Version, current)) return false;
        var highest = VersionStore.ReadHighest();
        if (highest != null && Version.TryParse(l.Version, out var v) && v < highest) return false;
        return true;
    }

    /// <summary>Returns true if a replacement was started; the caller must exit the app immediately.</summary>
    public async Task<bool> TryUpdateAsync(Manifest m, string manifestUrl, Version current, string currentExePath, CancellationToken ct)
    {
        if (!ShouldUpdate(m, current)) return false;
        var l = m.Launcher!;
        var url = UpdateService.FileUrl(UpdateService.FilesBase(m, manifestUrl), l.Path);
        if (!IsSameHttpsHost(url, manifestUrl)) return false;
        var newExe = currentExePath + ".new";
        try { await _svc.DownloadOneAsync(url, newExe, l.Sha256, l.Size, null, ct); }
        catch (OperationCanceledException) { throw; }
        catch { UpdateService.TryDelete(newExe); return false; }
        VersionStore.Persist(l.Version);
        if (!OperatingSystem.IsWindows()) return false;
        SpawnReplaceAndRelaunch(currentExePath, newExe);
        return true;
    }

    // A running exe cannot overwrite itself: a PowerShell script waits for this PID, swaps the files and relaunches.
    private static void SpawnReplaceAndRelaunch(string currentExe, string newExe)
    {
        int pid = Environment.ProcessId;
        Directory.CreateDirectory(Settings.LocalDataDir);
        var ps1 = Path.Combine(Settings.LocalDataDir, "update.ps1");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            $"$cur = {Q(currentExe)}\r\n$new = {Q(newExe)}\r\n" +
            $"$log = {Q(Path.Combine(Settings.LocalDataDir, "update.log"))}\r\n" +
            $"try {{ Wait-Process -Id {pid} -Timeout 60 }} catch {{}}\r\n" +
            "try {\r\n" +
            "    Copy-Item -LiteralPath $cur -Destination ($cur + '.bak') -Force\r\n" +
            "    Move-Item -LiteralPath $new -Destination $cur -Force\r\n" +
            "    Start-Process -FilePath $cur\r\n" +
            "} catch {\r\n" +
            "    Add-Content -LiteralPath $log -Value ('[' + (Get-Date) + '] update failed: ' + $_.Exception.Message)\r\n" +
            "    if (Test-Path -LiteralPath ($cur + '.bak')) { Copy-Item -LiteralPath ($cur + '.bak') -Destination $cur -Force }\r\n" +
            "    Start-Process -FilePath $cur\r\n" +
            "} finally {\r\n" +
            "    Remove-Item -LiteralPath $new -Force\r\n" +
            "    Remove-Item -LiteralPath ($cur + '.bak') -Force\r\n" +
            "}\r\n";
        File.WriteAllText(ps1, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}

internal static class VersionStore
{
    private static string FilePath => Path.Combine(Settings.LocalDataDir, "launcher-version.txt");
    public static Version? ReadHighest()
    {
        try { return Version.TryParse(File.ReadAllText(FilePath).Trim(), out var v) ? v : null; } catch { return null; }
    }
    public static void Persist(string version)
    {
        try
        {
            if (!Version.TryParse(version, out var v)) return;
            var cur = ReadHighest();
            if (cur != null && cur >= v) return;
            Directory.CreateDirectory(Settings.LocalDataDir);
            File.WriteAllText(FilePath, v.ToString());
        }
        catch { }
    }
}
