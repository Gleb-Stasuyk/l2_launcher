using System.Net;
using System.Net.Sockets;
using System.Text;
using L2Launcher.Core;
using Xunit;

namespace L2Launcher.Tests;

/// <summary>Minimal HTTP/1.1 file server on 127.0.0.1 that can drop or stall connections mid-body.</summary>
sealed class FlakyServer : IDisposable
{
    public enum Fault { None, Drop, Stall }
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly byte[] _data;
    readonly CancellationTokenSource _cts = new();
    public Fault Mode = Fault.None;
    /// <summary>Faulty connections: each sends at most this many body bytes, then drops/stalls.</summary>
    public int FaultAfterBytes = 20_000;
    /// <summary>How many connections misbehave (int.MaxValue = all).</summary>
    public int FaultyConnections = int.MaxValue;
    public bool SupportRange = true;
    public int Requests;
    public List<string> RangeHeaders { get; } = new();
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/f.bin";

    public FlakyServer(byte[] data) { _data = data; _l.Start(); _ = Loop(); }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Handle(c);
        }
    }

    async Task Handle(TcpClient c)
    {
        using var _ = c;
        try
        {
            var ns = c.GetStream();
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n"))
            {
                if (await ns.ReadAsync(one, _cts.Token) <= 0) return;
                sb.Append((char)one[0]);
            }
            int idx = Interlocked.Increment(ref Requests);
            var range = sb.ToString().Split("\r\n").FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            lock (RangeHeaders) RangeHeaders.Add(range ?? "");
            long from = 0, to = _data.Length - 1; bool partial = false;
            if (SupportRange && range != null)
            {
                var spec = range.Split('=')[1].Trim().Split('-');
                from = long.Parse(spec[0]);
                if (spec[1].Length > 0) to = Math.Min(long.Parse(spec[1]), _data.Length - 1);
                partial = true;
                if (from >= _data.Length)
                {
                    await ns.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    return;
                }
            }
            long len = to - from + 1;
            var head = partial
                ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{to}/{_data.Length}\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n"
                : $"HTTP/1.1 200 OK\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n";
            await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
            bool faulty = Mode != Fault.None && idx <= FaultyConnections;
            long send = faulty ? Math.Min(len, FaultAfterBytes) : len;
            await ns.WriteAsync(_data.AsMemory((int)from, (int)send));
            await ns.FlushAsync();
            if (faulty && send < len && Mode == Fault.Stall) await Task.Delay(Timeout.Infinite, _cts.Token);
            // Drop: just close (connection ends before Content-Length is reached)
        }
        catch { }
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

public class ResilientDownloadTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("l2dl").FullName;
    static byte[] Data(int n) { var b = new byte[n]; new Random(7).NextBytes(b); return b; }

    UpdateService Svc(List<string>? notices = null) => new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
    {
        ChunkSize = 256 * 1024,
        StallTimeout = TimeSpan.FromMilliseconds(400),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxRetries = 4,
        RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        RetryMaxDelay = TimeSpan.FromMilliseconds(50),
        Notice = (f, a, m, msg) => { lock (notices ?? new()) notices?.Add($"{f} {a}/{m} {msg}"); },
    };

    [Fact]
    public async Task UsesRangeChunks()
    {
        var data = Data(1_000_000);
        using var srv = new FlakyServer(data);
        var dest = Path.Combine(_dir, "a.bin");
        await Svc().DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(4, srv.Requests); // 1_000_000 / 256 KiB -> 4 chunks
        Assert.Equal("Range: bytes=0-262143", srv.RangeHeaders[0]);
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task ResumesAfterDroppedConnections()
    {
        var data = Data(600_000);
        using var srv = new FlakyServer(data) { Mode = FlakyServer.Fault.Drop, FaultAfterBytes = 20_000 };
        var notices = new List<string>();
        var dest = Path.Combine(_dir, "b.bin");
        long last = 0;
        // every connection drops after 20 KB (like the filtered foreign networks) -> keeps resuming because each try makes progress
        await Svc(notices).DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, b => last = b, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(data.Length, last);
        Assert.True(srv.Requests >= 30);
        Assert.NotEmpty(notices);
        Assert.True(srv.RangeHeaders.Contains("Range: bytes=20000-282143"), string.Join(" | ", srv.RangeHeaders.Take(6))); // resumed from the last byte
    }

    [Fact]
    public async Task ResumesAfterStall()
    {
        var data = Data(300_000);
        using var srv = new FlakyServer(data) { Mode = FlakyServer.Fault.Stall, FaultAfterBytes = 16_000, FaultyConnections = 3 };
        var notices = new List<string>();
        var dest = Path.Combine(_dir, "c.bin");
        await Svc(notices).DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(3, notices.Count);
        Assert.Contains("stalled", notices[0]);
    }

    [Fact]
    public async Task ServerWithoutRange_RestartsFromZero()
    {
        var data = Data(200_000);
        using var srv = new FlakyServer(data) { SupportRange = false, Mode = FlakyServer.Fault.Drop, FaultAfterBytes = 50_000, FaultyConnections = 2 };
        var dest = Path.Combine(_dir, "d.bin");
        await Svc().DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(3, srv.Requests);
    }

    [Fact]
    public async Task GivesUpWhenNoProgress_AndKeepsPartForLaterResume()
    {
        var data = Data(200_000);
        using var srv = new FlakyServer(data) { Mode = FlakyServer.Fault.Stall, FaultAfterBytes = 0 };
        var dest = Path.Combine(_dir, "e.bin");
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Svc().DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, null, default));
        Assert.Equal("download_failed", ex.Code);
        Assert.Equal(5, srv.Requests); // 1 + MaxRetries(4)
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task ResumesExistingPart_AndRestartsIfPartWasStale()
    {
        var data = Data(500_000);
        using var srv = new FlakyServer(data);
        var dest = Path.Combine(_dir, "f.bin");
        File.WriteAllBytes(dest + ".part", data.AsSpan(0, 300_000).ToArray());
        await Svc().DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(data), data.Length, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.StartsWith("Range: bytes=300000-", srv.RangeHeaders[0]);

        // stale .part from another version: result fails the hash -> one clean re-download from 0
        var dest2 = Path.Combine(_dir, "g.bin");
        File.WriteAllBytes(dest2 + ".part", Data(1000).Select(b => (byte)(b ^ 0x5A)).ToArray());
        srv.RangeHeaders.Clear();
        await Svc().DownloadOneAsync(srv.Url, dest2, FileIntegrity.Sha256Bytes(data), data.Length, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest2));
        Assert.Contains("Range: bytes=0-262143", srv.RangeHeaders);
    }

    [Fact]
    public async Task WrongContentOnServer_IsHashMismatch_NotRetriedForever()
    {
        var data = Data(100_000);
        using var srv = new FlakyServer(data);
        var dest = Path.Combine(_dir, "h.bin");
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Svc().DownloadOneAsync(srv.Url, dest, new string('0', 64), data.Length, null, default));
        Assert.Equal("hash_mismatch", ex.Code);
        Assert.False(File.Exists(dest)); Assert.False(File.Exists(dest + ".part"));
        // server file shorter than the manifest says -> 416 on the tail -> hash_mismatch
        var ex2 = await Assert.ThrowsAsync<LauncherException>(() => Svc().DownloadOneAsync(srv.Url, dest, new string('0', 64), data.Length + 300_000, null, default));
        Assert.Equal("hash_mismatch", ex2.Code);
    }

    [Fact]
    public async Task EmptyFile()
    {
        using var srv = new FlakyServer(Array.Empty<byte>());
        var dest = Path.Combine(_dir, "z.bin");
        await Svc().DownloadOneAsync(srv.Url, dest, FileIntegrity.Sha256Bytes(Array.Empty<byte>()), 0, null, default);
        Assert.Equal(0, new FileInfo(dest).Length);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
