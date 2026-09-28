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
    private readonly string? _publicKeyPem;

    public UpdateService(HttpClient http, string? publicKeyPem = null)
    {
        _http = http; _publicKeyPem = publicKeyPem;
    }

    /// <summary>Raw bytes + signature of the last manifest fetched and verified (for ManifestCache).</summary>
    public (byte[] Bytes, string Sig)? LastRaw { get; private set; }

    private void RequireSecure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new LauncherException("bad_url", $"Invalid URL: {url}");
        if (Settings.IsAllowedUrl(url)) return; // https, or http on loopback only (local test host)
        throw new LauncherException("insecure_url", $"Only https URLs are allowed: {url}");
    }

    // ---------------------------------------------------------------- manifest
    public async Task<Manifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        RequireSecure(manifestUrl);
        byte[] bytes; string sig;
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(1500 * attempt, ct);
                var bust = (manifestUrl.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
                using var mcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                mcts.CancelAfter(TimeSpan.FromSeconds(60));
                bytes = await _http.GetByteArrayAsync(manifestUrl + bust, mcts.Token);
                sig = await _http.GetStringAsync(manifestUrl + ".sig" + bust, mcts.Token);
                var parsed = ParseAndVerify(bytes, sig);
                LastRaw = (bytes, sig);
                return parsed;
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
        if (m.Languages != null)
        {
            foreach (var t in m.Languages.Files)
            {
                PathSafety.ResolveSafePath(Path.GetTempPath(), t);
                if (seen.Contains(PathSafety.Normalize(t)))
                    throw new LauncherException("bad_manifest", $"Language file also listed as normal file: {t}");
                foreach (var code in m.Languages.Sets)
                    if (!seen.Contains(PathSafety.Normalize(LanguageService.SetPath(code, t))))
                        throw new LauncherException("bad_manifest", $"Language set '{code}' is missing {t}");
            }
        }
        ServerService.Validate(m, seen);
        foreach (var o in m.Obsolete) PathSafety.ResolveSafePath(Path.GetTempPath(), o);
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

    // ---------------------------------------------------------------- resilient download settings
    /// <summary>Bytes requested per HTTP Range request.</summary>
    public long ChunkSize { get; set; } = 4L << 20;
    /// <summary>No bytes received for this long = stalled connection: reconnect and resume.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>Max wait for response headers.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Consecutive failed attempts (without any new bytes) before giving up on a file.</summary>
    public int MaxRetries { get; set; } = 10;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Human-readable notices (reconnects/resumes) for the log/status line: (file name, attempt, max, message).</summary>
    public Action<string, int, int, string>? Notice { get; set; }

    /// <summary>
    /// Downloads to "&lt;dest&gt;.part" with HTTP Range chunks. A dropped or stalled connection (no bytes for
    /// StallTimeout) is reopened and the download resumes from the last byte received, with exponential backoff
    /// and at most MaxRetries consecutive attempts without progress. Servers that ignore Range (200) are handled
    /// by restarting from 0. The finished file must match size + sha256, then it atomically replaces dest.
    /// A leftover .part (e.g. after cancel) is resumed; if the result does not verify it is re-downloaded once from 0.
    /// </summary>
    public async Task DownloadOneAsync(string url, string dest, string sha256, long expectedSize, Action<long>? onBytes, CancellationToken ct)
    {
        RequireSecure(url);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        for (int pass = 0; pass < 2; pass++)
        {
            bool resumed;
            try { resumed = await FetchToPartAsync(url, part, expectedSize, onBytes, ct); }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(part);
                throw new LauncherException("access_denied", $"Cannot write {dest}. Close the game or run from a folder you can write to.", ex);
            }
            catch (HashMismatchException ex)
            {
                TryDelete(part);
                throw new LauncherException("hash_mismatch", ex.Message, ex);
            }
            var len = new FileInfo(part).Length;
            if (len == expectedSize && string.Equals(FileIntegrity.Sha256(part), sha256, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(dest))
                    {
                        var a = File.GetAttributes(dest);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, a & ~FileAttributes.ReadOnly);
                    }
                    File.Move(part, dest, overwrite: true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new LauncherException("access_denied", $"Cannot write {dest}. Close the game or run from a folder you can write to.", ex);
                }
                return;
            }
            TryDelete(part);
            if (!resumed) break; // fresh download that does not verify: server-side problem
        }
        throw new LauncherException("hash_mismatch", $"Downloaded file does not match the manifest ({Path.GetFileName(dest)})");
    }

    /// <summary>Fills <paramref name="part"/> up to expectedSize. Returns true if it resumed an existing partial file.</summary>
    private async Task<bool> FetchToPartAsync(string url, string part, long size, Action<long>? onBytes, CancellationToken ct)
    {
        var name = Path.GetFileName(part.EndsWith(".part") ? part[..^5] : part);
        await using var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16);
        long pos = fs.Length;
        bool resumed = pos > 0 && pos <= size;
        if (!resumed) pos = 0;
        fs.SetLength(pos);
        fs.Position = pos;
        if (pos > 0) onBytes?.Invoke(pos);
        bool useRange = true;
        int failures = 0;
        var buf = new byte[1 << 16];
        while (pos < size)
        {
            ct.ThrowIfCancellationRequested();
            long progressAtStart = pos;
            try
            {
                long end = useRange ? Math.Min(pos + ChunkSize, size) - 1 : size - 1;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (useRange) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(pos, end);
                HttpResponseMessage resp;
                using (var hcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    hcts.CancelAfter(ConnectTimeout);
                    try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hcts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("No response from server"); }
                }
                using (resp)
                {
                    var code = (int)resp.StatusCode;
                    if (code == 206)
                    {
                        var cr = resp.Content.Headers.ContentRange;
                        if (cr?.From != null && cr.From != pos) throw new IOException($"Server resumed at {cr.From}, expected {pos}");
                    }
                    else if (code == 200)
                    {
                        if (pos > 0) { pos = 0; fs.SetLength(0); fs.Position = 0; onBytes?.Invoke(0); }
                        useRange = false; end = size - 1;
                    }
                    else if (code == 416) throw new HashMismatchException($"File on the server is smaller than the manifest says ({name})");
                    else if (code == 408 || code == 429 || code >= 500) throw new IOException($"HTTP {code}");
                    else throw new LauncherException("download_failed", $"Download failed: {name}: HTTP {code}");

                    await using var s = await resp.Content.ReadAsStreamAsync(ct);
                    while (pos <= end)
                    {
                        int n;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            idle.CancelAfter(StallTimeout);
                            try { n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, end - pos + 1)), idle.Token); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            { throw new IOException($"Connection stalled (no data for {StallTimeout.TotalSeconds:0} s)"); }
                        }
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        pos += n;
                        onBytes?.Invoke(pos);
                    }
                    if (pos <= end) throw new IOException("Connection closed early");
                    if (!useRange && pos < size) throw new IOException("Connection closed early");
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { await fs.FlushAsync(CancellationToken.None); throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                await fs.FlushAsync(CancellationToken.None);
                if (pos > progressAtStart) failures = 0;
                failures++;
                if (failures > MaxRetries)
                    throw new LauncherException("download_failed", $"Download failed: {name}: {ex.Message}", ex);
                Notice?.Invoke(name, failures, MaxRetries, ex.Message);
                var delay = TimeSpan.FromMilliseconds(Math.Min(RetryMaxDelay.TotalMilliseconds, RetryBaseDelay.TotalMilliseconds * Math.Pow(2, failures - 1)));
                await Task.Delay(delay, ct);
            }
        }
        await fs.FlushAsync(ct);
        return resumed;
    }

    internal static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}

public sealed class HashMismatchException : Exception { public HashMismatchException(string m) : base(m) { } }
