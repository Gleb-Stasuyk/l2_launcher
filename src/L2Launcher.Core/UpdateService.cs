// Download/verify logic derived from okuzpe/Lineage2Launcher Services/UpdateService.cs
// (MIT, Copyright (c) 2025-2026 L2Titan); reworked for the signed v1 manifest object format.
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;

namespace L2Launcher.Core;

public sealed class UpdateService
{
    public const int SupportedFormat = 1;
    private readonly HttpClient _http;
    private readonly bool _allowHttpForTests;
    private readonly string? _publicKeyPem;

    /// <param name="allowHttpForTests">Only for unit tests against a local http server. The app always passes false.</param>
    public UpdateService(HttpClient http, bool allowHttpForTests = false, string? publicKeyPem = null)
    {
        _http = http; _allowHttpForTests = allowHttpForTests; _publicKeyPem = publicKeyPem;
    }

    private void RequireSecure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new LauncherException("bad_url", $"Invalid URL: {url}");
        if (u.Scheme == Uri.UriSchemeHttps) return;
        if (_allowHttpForTests && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) return;
        throw new LauncherException("insecure_url", $"Only https URLs are allowed: {url}");
    }

    // ---------------------------------------------------------------- manifest
    public async Task<Manifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        RequireSecure(manifestUrl);
        byte[] bytes; string sig;
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(1500 * attempt, ct);
                var bust = (manifestUrl.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
                bytes = await _http.GetByteArrayAsync(manifestUrl + bust, ct);
                sig = await _http.GetStringAsync(manifestUrl + ".sig" + bust, ct);
                return ParseAndVerify(bytes, sig);
            }
            catch (LauncherException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw new LauncherException("server_unreachable", "Could not download the update manifest: " + last?.Message, last);
    }

    public Manifest ParseAndVerify(byte[] bytes, string signatureBase64)
    {
        if (!ManifestSecurity.Verify(bytes, signatureBase64, _publicKeyPem))
            throw new LauncherException("bad_signature", "Manifest signature is invalid. Update cancelled for safety.");
        Manifest? m;
        try { m = JsonConvert.DeserializeObject<Manifest>(Encoding.UTF8.GetString(bytes)); }
        catch (JsonException ex) { throw new LauncherException("bad_manifest", "Manifest is corrupted.", ex); }
        if (m == null || m.Format != SupportedFormat)
            throw new LauncherException("bad_manifest", $"Unsupported manifest format {m?.Format}. Please update the launcher.");
        var seen = new HashSet<string>();
        foreach (var f in m.Files)
        {
            PathSafety.ResolveSafePath(Path.GetTempPath(), f.Path); // throws on traversal
            if (!FileIntegrity.IsSha256Hex(f.Sha256) || f.Size < 0)
                throw new LauncherException("bad_manifest", $"Bad entry in manifest: {f.Path}");
            if (!seen.Add(PathSafety.Normalize(f.Path)))
                throw new LauncherException("bad_manifest", $"Duplicate entry in manifest: {f.Path}");
        }
        return m;
    }

    public static string FilesBase(Manifest m, string manifestUrl)
    {
        if (!string.IsNullOrWhiteSpace(m.BaseUrl)) return m.BaseUrl!.TrimEnd('/') + "/";
        var u = new Uri(manifestUrl);
        return new Uri(u, "files/").ToString();
    }

    public static string FileUrl(string filesBase, string relPath) =>
        filesBase + string.Join("/", relPath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    // ---------------------------------------------------------------- verify
    public VerifyResult Verify(string gamePath, Manifest m, HashCache cache, bool full, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var r = new VerifyResult();
        var files = m.Files.Where(f => !UserConfigFiles.IsProtected(f.Path, m.UserConfig)).ToList();
        int i = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new UpdateProgress(UpdateStage.Verifying, i, files.Count, 0, 0, f.Path));
            i++;
            var p = PathSafety.ResolveSafePath(gamePath, f.Path);
            if (!File.Exists(p)) { r.Missing.Add(f); continue; }
            var len = new FileInfo(p).Length;
            if (len != f.Size) { r.Changed.Add(f); continue; }
            var h = cache.GetHash(p, PathSafety.Normalize(f.Path), full);
            if (!string.Equals(h, f.Sha256, StringComparison.OrdinalIgnoreCase)) r.Changed.Add(f);
            else r.Ok++;
        }
        progress?.Report(new UpdateProgress(UpdateStage.Verifying, files.Count, files.Count, 0, 0, null));
        cache.Save();
        return r;
    }

    // ---------------------------------------------------------------- download
    public async Task DownloadAsync(string gamePath, Manifest m, string manifestUrl, IReadOnlyList<ManifestFile> files,
        HashCache cache, IProgress<UpdateProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var filesBase = FilesBase(m, manifestUrl);
        RequireSecure(filesBase);
        long total = files.Sum(f => f.Size), done = 0;
        var sw = Stopwatch.StartNew();
        int idx = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (UserConfigFiles.IsProtected(f.Path, m.UserConfig)) continue;
            var dest = PathSafety.ResolveSafePath(gamePath, f.Path);
            var url = FileUrl(filesBase, f.Path);
            log?.Invoke($"GET {url}");
            long before = done;
            await DownloadOneAsync(url, dest, f.Sha256, f.Size, b =>
            {
                progress?.Report(new UpdateProgress(UpdateStage.Downloading, idx, files.Count, before + b, total, f.Path,
                    sw.Elapsed.TotalSeconds > 0 ? (before + b) / sw.Elapsed.TotalSeconds : 0));
            }, ct);
            cache.Remember(dest, PathSafety.Normalize(f.Path), f.Sha256.ToLowerInvariant());
            done += f.Size; idx++;
        }
        cache.Save();
        progress?.Report(new UpdateProgress(UpdateStage.Done, files.Count, files.Count, total, total, null));
    }

    /// <summary>Downloads to "&lt;dest&gt;.part", verifies size+sha256, then atomically replaces dest.</summary>
    public async Task DownloadOneAsync(string url, string dest, string sha256, long expectedSize, Action<long>? onBytes, CancellationToken ct)
    {
        RequireSecure(url);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(1000 * attempt, ct);
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    await using var s = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[1 << 16];
                    long got = 0;
                    while (true)
                    {
                        int n;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(60));
                            try { n = await s.ReadAsync(buf, idle.Token); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            { throw new IOException("Connection stalled"); }
                        }
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        got += n;
                        if (got > expectedSize) throw new HashMismatchException("File is larger than the manifest says");
                        onBytes?.Invoke(got);
                    }
                }
                var len = new FileInfo(part).Length;
                if (len != expectedSize || !string.Equals(FileIntegrity.Sha256(part), sha256, StringComparison.OrdinalIgnoreCase))
                    throw new HashMismatchException($"Downloaded file does not match the manifest ({Path.GetFileName(dest)})");
                if (File.Exists(dest))
                {
                    var a = File.GetAttributes(dest);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, a & ~FileAttributes.ReadOnly);
                }
                File.Move(part, dest, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { TryDelete(part); throw; }
            catch (HashMismatchException ex)
            {
                TryDelete(part);
                throw new LauncherException("hash_mismatch", ex.Message, ex); // server-side problem: don't retry
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(part);
                throw new LauncherException("access_denied", $"Cannot write {dest}. Close the game or run from a folder you can write to.", ex);
            }
            catch (Exception ex) { last = ex; TryDelete(part); }
        }
        throw new LauncherException("download_failed", $"Download failed: {Path.GetFileName(dest)}: {last?.Message}", last);
    }

    internal static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}

public sealed class HashMismatchException : Exception { public HashMismatchException(string m) : base(m) { } }
