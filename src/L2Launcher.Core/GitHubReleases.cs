using System.Net;
using System.Text.Json;

namespace L2Launcher.Core;

/// <summary>A downloadable file of a GitHub release. <see cref="Sha256"/> comes from the asset "digest" (null when absent).</summary>
public sealed record GhAsset(string Name, string Url, long Size, string? Sha256);

public sealed record GhRelease(string Tag, Version? Version, string HtmlUrl, IReadOnlyList<GhAsset> Assets);

public enum GhStatus { Ok, NoRelease, RateLimited, Error }

public sealed record GhResult(GhStatus Status, GhRelease? Release, string? Message);

/// <summary>Public GitHub REST API (no token): latest release of a repo. Never throws; offline / rate limit = a status.</summary>
public sealed class GitHubReleases
{
    public const string LauncherRepo = "Gleb-Stasuyk/l2_launcher";
    public const string ServerRepo = "Gleb-Stasuyk/l2start-server";
    /// <summary>Server releases are tagged server-vX.Y.Z (the repo may carry other tags).</summary>
    public const string ServerTagPrefix = "server-v";
    public const string LauncherAsset = "L2Launcher.exe";

    private readonly HttpClient _http;
    public GitHubReleases(HttpClient http) { _http = http; }

    public async Task<GhResult> LatestAsync(string repo, string? tagPrefix, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=30");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (req.Headers.UserAgent.Count == 0 && _http.DefaultRequestHeaders.UserAgent.Count == 0) req.Headers.UserAgent.ParseAdd("L2Launcher");
            using var resp = await _http.SendAsync(req, cts.Token);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var v) && long.TryParse(v.FirstOrDefault(), out var s)
                    ? DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime().ToString("HH:mm") : null;
                return new GhResult(GhStatus.RateLimited, null, reset);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound) return new GhResult(GhStatus.NoRelease, null, null);
            if (!resp.IsSuccessStatusCode) return new GhResult(GhStatus.Error, null, "HTTP " + (int)resp.StatusCode);
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var r = Pick(json, tagPrefix);
            return r == null ? new GhResult(GhStatus.NoRelease, null, null) : new GhResult(GhStatus.Ok, r, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new GhResult(GhStatus.Error, null, "timeout"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new GhResult(GhStatus.Error, null, ex.Message); }
    }

    /// <summary>Highest-version published release (no draft / prerelease) whose tag has the prefix (null = any v-tag).</summary>
    public static GhRelease? Pick(string json, string? tagPrefix)
    {
        using var doc = JsonDocument.Parse(json);
        GhRelease? best = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (Bool(e, "draft") || Bool(e, "prerelease")) continue;
            var tag = Str(e, "tag_name") ?? "";
            if (tagPrefix != null && !tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var ver = ParseVersion(tag, tagPrefix);
            if (ver == null) continue;
            if (tagPrefix == null && tag.StartsWith(ServerTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (best != null && best.Version >= ver) continue;
            var assets = new List<GhAsset>();
            if (e.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                {
                    var d = Str(x, "digest");
                    var sha = d != null && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
                    assets.Add(new GhAsset(Str(x, "name") ?? "", Str(x, "browser_download_url") ?? "",
                        x.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0, sha));
                }
            best = new GhRelease(tag, ver, Str(e, "html_url") ?? "", assets);
        }
        return best;
    }

    /// <summary>"v1.4.0", "server-v0.1.0", "1.4", "0.2.0-rc1" -> Version (suffix dropped); null when not a version.</summary>
    public static Version? ParseVersion(string? tag, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (prefix != null && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) return int.TryParse(s, out var major) ? new Version(major, 0) : null;
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>Compare on the parts both have (1.4 == 1.4.0 == 1.4.0.0).</summary>
    public static int Compare(Version a, Version b) =>
        Norm(a).CompareTo(Norm(b));

    private static Version Norm(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>Only https://github.com/{repo}/releases/download/… is trusted for a launcher download.</summary>
    public static bool IsTrustedAssetUrl(string url, string repo)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith("/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}