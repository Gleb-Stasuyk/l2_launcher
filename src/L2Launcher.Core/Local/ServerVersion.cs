namespace L2Launcher.Core.Local;

public enum VersionCompare { UpToDate, Outdated, Newer, Unknown }

/// <summary>Version of the local server: the running game container's image tag, else L2S_VERSION in .env.</summary>
public static class ServerVersion
{
    /// <summary>"ghcr.io/gleb-stasuyk/l2start-server:0.1.0" -> "0.1.0"; no tag -> null.</summary>
    public static string? TagOf(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var s = image.Trim();
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        var slash = s.LastIndexOf('/');
        var colon = s.LastIndexOf(':');
        return colon > slash ? s[(colon + 1)..] : null;
    }

    public static string? EnvVersion(string folder)
    {
        try
        {
            var f = Path.Combine(folder, ".env");
            if (!File.Exists(f)) return null;
            foreach (var line in File.ReadAllLines(f))
            {
                var t = line.Trim();
                if (t.StartsWith("L2S_VERSION=", StringComparison.Ordinal)) return t["L2S_VERSION=".Length..].Trim().Trim('"');
            }
        }
        catch { }
        return null;
    }

    /// <summary>Local version text (tag as is, e.g. "0.1.0" or "dev"), null = not known.</summary>
    public static async Task<string?> LocalAsync(IProcessRunner run, DockerService docker, CancellationToken ct)
    {
        try
        {
            var r = await run.RunAsync("docker", new[] { "inspect", "-f", "{{.Config.Image}}", docker.GameContainerName }, ct: ct);
            if (r.Ok && TagOf(r.Stdout) is { Length: > 0 } tag) return tag;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return EnvVersion(docker.Folder);
    }

    public static VersionCompare Compare(string? local, Version? remote)
    {
        var l = GitHubReleases.ParseVersion(local);
        if (l == null || remote == null) return VersionCompare.Unknown;
        var c = GitHubReleases.Compare(l, remote);
        return c == 0 ? VersionCompare.UpToDate : c < 0 ? VersionCompare.Outdated : VersionCompare.Newer;
    }
}