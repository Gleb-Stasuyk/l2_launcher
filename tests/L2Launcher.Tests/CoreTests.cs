using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using L2Launcher.Core;
using Xunit;

namespace L2Launcher.Tests;

public class SafetyTests
{
    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("system/../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("system/l2.exe:ads")]
    public void RejectsTraversal(string p) =>
        Assert.Throws<PathTraversalException>(() => PathSafety.ResolveSafePath(Path.GetTempPath(), p));

    [Fact]
    public void UserConfigIsProtected()
    {
        Assert.True(UserConfigFiles.IsProtected("system\\Option.ini"));
        Assert.True(UserConfigFiles.IsProtected("SYSTEM/user.ini"));
        Assert.True(UserConfigFiles.IsProtected("system/l2.log"));
        Assert.False(UserConfigFiles.IsProtected("system/l2.exe"));
        Assert.True(UserConfigFiles.IsProtected("system/custom.ini", new[] { "system/Custom.ini" }));
    }

    [Fact]
    public void SelfUpdateRules()
    {
        var m = new Manifest { Launcher = new LauncherRelease { Version = "1.2.0", Path = "launcher/L2Launcher.exe", Sha256 = new string('a', 64), Size = 1 } };
        Assert.True(LauncherUpdater.ShouldUpdate(m, new Version(1, 0, 0)));
        Assert.False(LauncherUpdater.ShouldUpdate(m, new Version(1, 2, 0)));
        Assert.False(LauncherUpdater.IsSameHttpsHost("http://a.com/x", "https://a.com/m.json"));
        Assert.False(LauncherUpdater.IsSameHttpsHost("http://a.com/x", "http://a.com/m.json"));
        Assert.True(LauncherUpdater.IsSameHttpsHost("http://127.0.0.1:8765/files/x.exe", "http://127.0.0.1:8765/manifest.json"));
        Assert.False(LauncherUpdater.IsSameHttpsHost("http://127.0.0.1:9999/files/x.exe", "http://127.0.0.1:8765/manifest.json"));
        Assert.False(LauncherUpdater.IsSameHttpsHost("https://b.com/x", "https://a.com/m.json"));
        Assert.True(LauncherUpdater.IsSameHttpsHost("https://a.com/files/launcher/x.exe", "https://a.com/m.json"));
    }

    [Fact]
    public async Task RejectsHttpInApp()
    {
        var svc = new UpdateService(new HttpClient());
        var ex = await Assert.ThrowsAsync<LauncherException>(() => svc.FetchManifestAsync("http://example.com/manifest.json", default));
        Assert.Equal("insecure_url", ex.Code);
    }

    [Theory]
    [InlineData("https://updates.example.com/m.json", true)]
    [InlineData("http://127.0.0.1:8765/manifest.json", true)]
    [InlineData("http://localhost:8765/manifest.json", true)]
    [InlineData("http://[::1]:8765/manifest.json", true)]
    [InlineData("http://example.com/manifest.json", false)]
    [InlineData("http://192.168.1.10/manifest.json", false)]
    [InlineData("http://10.0.0.1/manifest.json", false)]
    [InlineData("http://localhost.evil.com/manifest.json", false)]
    [InlineData("http://127.0.0.1.nip.io/manifest.json", false)]
    [InlineData("ftp://127.0.0.1/manifest.json", false)]
    [InlineData("file:///C:/x/manifest.json", false)]
    public void OnlyHttpsOrLoopbackHttpAllowed(string url, bool allowed) => Assert.Equal(allowed, Settings.IsAllowedUrl(url));

    [Fact]
    public async Task NonLoopbackHttpIsRefusedByUpdateService()
    {
        var svc = new UpdateService(new HttpClient());
        foreach (var u in new[] { "http://192.168.1.10/manifest.json", "http://localhost.evil.com/manifest.json" })
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(() => svc.FetchManifestAsync(u, default));
            Assert.Equal("insecure_url", ex.Code);
            ex = await Assert.ThrowsAsync<LauncherException>(() => svc.DownloadOneAsync(u, Path.Combine(Path.GetTempPath(), "x.bin"), new string('a', 64), 1, null, default));
            Assert.Equal("insecure_url", ex.Code);
        }
    }

    [Fact]
    public void ConfigRejectsNonLoopbackHttp()
    {
        var dir = Directory.CreateTempSubdirectory("l2cfg").FullName;
        File.WriteAllText(Path.Combine(dir, "launcher.json"), "{\"ManifestUrl\":\"http://example.com/manifest.json\"}");
        Assert.Equal(BuildDefaults.ManifestUrl, Settings.LoadConfig(dir).cfg.ManifestUrl);
        File.WriteAllText(Path.Combine(dir, "launcher.json"), "{\"ManifestUrl\":\"http://127.0.0.1:8765/manifest.json\"}");
        Assert.Equal("http://127.0.0.1:8765/manifest.json", Settings.LoadConfig(dir).cfg.ManifestUrl);
    }
}

/// <summary>
/// End-to-end: build a fake client + real enru_switch overlay with tools/build_manifest.py (signed with keys/),
/// serve it on 127.0.0.1 with python http.server, then install/verify/repair with UpdateService.
/// </summary>
public class EndToEndTests : IDisposable
{
    static readonly string Root = FindRoot();
    readonly string _work = Directory.CreateTempSubdirectory("l2e2e").FullName;
    Process? _server;

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "tools", "build_manifest.py"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repo root not found");
    }

    static void Run(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"{file} {args} failed:\n{o}");
    }

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    string MakeRefClient()
    {
        var c = Path.Combine(_work, "ref");
        var sys = Path.Combine(c, "system");
        Directory.CreateDirectory(sys);
        Directory.CreateDirectory(Path.Combine(c, "systextures"));
        var rnd = new Random(1);
        void W(string rel, int n) { var b = new byte[n]; rnd.NextBytes(b); File.WriteAllBytes(Path.Combine(c, rel), b); }
        W("system/l2.exe", 300_000);
        W("system/nwindow.dll", 100_000);
        W("system/castlename-e.dat", 2000);
        W("system/itemname-e.dat", 5000);
        W("systextures/L2Font-e.utx", 4000);
        File.WriteAllText(Path.Combine(sys, "Option.ini"), "[Game]\r\nIsNative=False\r\n");
        File.WriteAllText(Path.Combine(sys, "User.ini"), "server-side user.ini\r\n");
        File.WriteAllText(Path.Combine(sys, "l2.log"), "log");
        return c;
    }

    string MakeRuSet()
    {
        var d = Path.Combine(_work, "ru");
        Directory.CreateDirectory(Path.Combine(d, "system"));
        File.Copy("/workspace/l2ru/safe_subset/system/itemname-e.dat", Path.Combine(d, "system", "itemname-e.dat"));
        return d;
    }

    async Task<(UpdateService svc, Manifest m, string url, string outDir, HttpClient http)> Serve(string extraArgs)
    {
        var refc = MakeRefClient();
        var outDir = Path.Combine(_work, "out");
        Run("python3", $"\"{Root}/tools/build_manifest.py\" --client \"{refc}\" {extraArgs} --version test.1 --out \"{outDir}\"");
        int port = FreePort();
        _server = Process.Start(new ProcessStartInfo("python3", $"-m http.server {port} --bind 127.0.0.1 --directory \"{outDir}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true });
        var url = $"http://127.0.0.1:{port}/manifest.json";
        var http = new HttpClient();
        for (int i = 0; i < 50; i++) { try { await http.GetAsync(url); break; } catch { await Task.Delay(100); } }
        var svc = new UpdateService(http);
        return (svc, await svc.FetchManifestAsync(url, default), url, outDir, http);
    }

    [Fact]
    public async Task InstallVerifyRepair_WithLanguages()
    {
        var fakeLauncher = Path.Combine(_work, "L2Launcher.exe");
        File.WriteAllBytes(fakeLauncher, new byte[1234]);
        var ru = MakeRuSet();
        var (svc, m, url, outDir, _) = await Serve($"--lang-ru \"{ru}\" --obsolete-enru --launcher-exe \"{fakeLauncher}\" --launcher-version 9.9.9");

        var paths = m.Files.Select(f => f.Path.ToLowerInvariant()).ToList();
        Assert.Equal(new[] { "system/itemname-e.dat" }, m.Languages!.Files);
        Assert.Contains("lang/en/system/itemname-e.dat", paths);
        Assert.Contains("lang/ru/system/itemname-e.dat", paths);
        Assert.DoesNotContain("system/itemname-e.dat", paths);                  // managed via language sets
        Assert.Contains("system/castlename-e.dat", paths);                      // same in both -> normal file
        Assert.DoesNotContain(paths, p => p.EndsWith("-k.dat"));
        Assert.Contains("system/castlename-k.dat", m.Obsolete);
        Assert.DoesNotContain("system/option.ini", paths);
        Assert.DoesNotContain("system/user.ini", paths);
        Assert.DoesNotContain("system/l2.log", paths);
        var ruItem = LanguageService.Expected(m, "ru", "system/itemname-e.dat")!;
        Assert.Equal(FileIntegrity.Sha256("/workspace/l2ru/safe_subset/system/itemname-e.dat"), ruItem.Sha256);

        // fresh install into a folder with the player's own Option.ini and leftovers of the Korea-slot layout
        var game = Path.Combine(_work, "game");
        Directory.CreateDirectory(Path.Combine(game, "system"));
        var myOpt = "[Game]\r\nIsNative=True\r\nMyOwnSetting=42\r\n";
        File.WriteAllText(Path.Combine(game, "system", "Option.ini"), myOpt);
        File.WriteAllText(Path.Combine(game, "system", "castlename-k.dat"), "old");
        File.WriteAllText(Path.Combine(game, "system", "keep-me-k.dat"), "not in obsolete list");
        var cache = HashCache.Load(null);
        var v1 = svc.Verify(game, m, cache, full: false, null, default);
        Assert.Equal(m.Files.Count, v1.Missing.Count);
        await svc.DownloadAsync(game, m, url, v1.ToDownload.ToList(), cache, null, null, default);
        Assert.Equal(1, LanguageService.DeleteObsolete(game, m));
        Assert.False(File.Exists(Path.Combine(game, "system", "castlename-k.dat")));
        Assert.True(File.Exists(Path.Combine(game, "system", "keep-me-k.dat")));
        Assert.Equal(m.Files.Count, svc.Verify(game, m, HashCache.Load(null), full: true, null, default).Ok);
        Assert.Equal(myOpt, File.ReadAllText(Path.Combine(game, "system", "Option.ini")));

        // language: first apply EN copies the file, repeat is a no-op, switching to RU copies, back to EN copies
        var target = Path.Combine(game, "system", "itemname-e.dat");
        Assert.False(File.Exists(target));
        Assert.Equal(1, LanguageService.Apply(game, m, GameLanguage.English, cache));
        Assert.Equal("en", LanguageService.ReadActive(game));
        Assert.Equal(LanguageService.Expected(m, "en", "system/itemname-e.dat")!.Sha256, FileIntegrity.Sha256(target));
        Assert.Equal(0, LanguageService.Apply(game, m, GameLanguage.English, cache));
        Assert.Equal(1, LanguageService.Apply(game, m, GameLanguage.Russian, cache));
        Assert.Equal("ru", LanguageService.ReadActive(game));
        Assert.Equal(ruItem.Sha256, FileIntegrity.Sha256(target));
        Assert.Empty(LanguageService.Mismatched(game, m, GameLanguage.Russian, HashCache.Load(null), true));
        Assert.Single(LanguageService.Mismatched(game, m, GameLanguage.English, HashCache.Load(null), true));

        // state file says RU but system file was replaced by someone -> detected by hash and fixed
        File.Copy(Path.Combine(game, "lang", "en", "system", "itemname-e.dat"), target, overwrite: true);
        Assert.Equal(1, LanguageService.Apply(game, m, GameLanguage.Russian, HashCache.Load(null), null, full: true));
        Assert.Equal(ruItem.Sha256, FileIntegrity.Sha256(target));

        // damaged language source -> clear error, repair via normal verify fixes it
        var src = Path.Combine(game, "lang", "en", "system", "itemname-e.dat");
        var b = File.ReadAllBytes(src); b[5] ^= 0xFF; File.WriteAllBytes(src, b);
        var ex = Assert.Throws<LauncherException>(() => LanguageService.Apply(game, m, GameLanguage.English, HashCache.Load(null), null, true));
        Assert.Equal("lang_set_corrupt", ex.Code);
        var v3 = svc.Verify(game, m, HashCache.Load(null), full: true, null, default);
        Assert.Single(v3.Changed);
        await svc.DownloadAsync(game, m, url, v3.ToDownload.ToList(), HashCache.Load(null), null, null, default);
        Assert.Equal(1, LanguageService.Apply(game, m, GameLanguage.English, HashCache.Load(null), null, true));

        // tampered manifest -> rejected
        var bytes = File.ReadAllBytes(Path.Combine(outDir, "manifest.json"));
        var sig = File.ReadAllText(Path.Combine(outDir, "manifest.json.sig"));
        bytes[bytes.Length / 2] ^= 1;
        Assert.Equal("bad_signature", Assert.Throws<LauncherException>(() => svc.ParseAndVerify(bytes, sig)).Code);

        Assert.Equal("9.9.9", m.Launcher!.Version);
        Assert.Equal(FileIntegrity.Sha256(fakeLauncher), m.Launcher.Sha256);
    }

    string MakeServerDir(string id, string addr)
    {
        var d = Path.Combine(_work, "srv_" + id);
        Directory.CreateDirectory(Path.Combine(d, "system"));
        File.WriteAllText(Path.Combine(d, "system", "l2.ini"), $"[URL]\r\nServerAddr={addr}\r\n");
        return d;
    }

    [Fact]
    public async Task ServerSelection_EndToEnd()
    {
        var main = MakeServerDir("main", "80.78.246.62");
        var local = MakeServerDir("local", "127.0.0.1");
        var (svc, m, url, outDir, http) = await Serve($"--server main=\"{main}\" --server local=\"{local}\" --server-name \"main=Main|Основной сервер\"");
        Assert.NotNull(m.Servers);
        Assert.Equal("main", m.Servers!.Default);
        Assert.Equal(new[] { "system/l2.ini" }, m.Servers.Files);
        Assert.Equal(new[] { "main", "local" }, m.Servers.List.Select(x => x.Id));
        Assert.Equal("Основной сервер", ServerService.DisplayName(m.Servers.List[0], true));
        Assert.Equal("Local", ServerService.DisplayName(m.Servers.List[1], false));
        var paths = m.Files.Select(f => f.Path.ToLowerInvariant()).ToList();
        Assert.Contains("server/main/system/l2.ini", paths);
        Assert.Contains("server/local/system/l2.ini", paths);
        Assert.DoesNotContain("system/l2.ini", paths);

        // players default to main; unknown/hidden prefs fall back to the default
        Assert.Equal("main", ServerService.Resolve(m, ""));
        Assert.Equal("main", ServerService.Resolve(m, "nope"));
        Assert.Equal("local", ServerService.Resolve(m, "LOCAL"));

        var game = Path.Combine(_work, "game");
        var cache = HashCache.Load(null);
        var v = svc.Verify(game, m, cache, false, null, default);
        await svc.DownloadAsync(game, m, url, v.ToDownload.ToList(), cache, null, null, default);
        var ini = Path.Combine(game, "system", "l2.ini");
        Assert.Equal(1, ServerService.Apply(game, m, "main", cache));
        Assert.Contains("80.78.246.62", File.ReadAllText(ini));
        Assert.Equal("main", ServerService.ReadActive(game));
        Assert.Equal(0, ServerService.Apply(game, m, "main", cache));           // unchanged -> no copy
        Assert.Equal(1, ServerService.Apply(game, m, "local", cache));
        Assert.Contains("127.0.0.1", File.ReadAllText(ini));
        Assert.Equal("local", ServerService.ReadActive(game));
        // someone replaced l2.ini while state says local -> detected by hash
        File.Copy(Path.Combine(game, "server", "main", "system", "l2.ini"), ini, true);
        Assert.Single(ServerService.Mismatched(game, m, "local", HashCache.Load(null), true));
        Assert.Equal(1, ServerService.Apply(game, m, "local", HashCache.Load(null), null, true));
        Assert.Contains("127.0.0.1", File.ReadAllText(ini));
        // damaged source -> server_set_corrupt
        File.WriteAllText(Path.Combine(game, "server", "main", "system", "l2.ini"), "tampered");
        Assert.Equal("server_set_corrupt", Assert.Throws<LauncherException>(() => ServerService.Apply(game, m, "main", HashCache.Load(null), null, true)).Code);

        // obsolete list never deletes the server target
        m.Obsolete.Add("system/l2.ini");
        Assert.Equal(0, LanguageService.DeleteObsolete(game, m));
        Assert.True(File.Exists(ini));

        // cache of the signed manifest round-trips and is re-verified
        var dir = Path.Combine(_work, "appdata");
        var raw = svc.LastRaw!.Value;
        ManifestCache.Save(dir, url, raw.Bytes, raw.Sig);
        Assert.Equal(2, ManifestCache.TryLoad(dir, url, svc)!.Servers!.List.Count);
        Assert.Null(ManifestCache.TryLoad(dir, url + "x", svc));
        var cf = Directory.GetFiles(Path.Combine(dir, "manifest_cache"), "*.json").Single();
        var cb = File.ReadAllBytes(cf); cb[cb.Length / 2] ^= 1; File.WriteAllBytes(cf, cb);
        Assert.Null(ManifestCache.TryLoad(dir, url, svc));
    }

    [Fact]
    public async Task HiddenServer_NotVisible_AndBuildRejectsHiddenDefault()
    {
        var main = MakeServerDir("main", "1.2.3.4");
        var local = MakeServerDir("local", "127.0.0.1");
        var (svc, m, url, outDir, http) = await Serve($"--server main=\"{main}\" --server local=\"{local}\" --hide-server local");
        Assert.Equal(new[] { "main" }, ServerService.Visible(m).Select(x => x.Id));
        Assert.Equal("main", ServerService.Resolve(m, "local"));
        var refc = Path.Combine(_work, "ref");
        var ex = Assert.Throws<Exception>(() => Run("python3", $"\"{Root}/tools/build_manifest.py\" --client \"{refc}\" --server main=\"{main}\" --hide-server main --out \"{Path.Combine(_work, "o2")}\" --unsigned"));
        Assert.Contains("hidden", ex.Message);
    }

    public void Dispose()
    {
        try { _server?.Kill(true); } catch { }
        try { Directory.Delete(_work, true); } catch { }
    }
}

public class ServerAndOptionTests
{
    static Manifest M(string def, bool localHidden = false) => new()
    {
        Format = 1,
        Files =
        {
            new ManifestFile { Path = "server/main/system/l2.ini", Size = 1, Sha256 = new string('a', 64) },
            new ManifestFile { Path = "server/local/system/l2.ini", Size = 1, Sha256 = new string('b', 64) },
        },
        Servers = new ServerInfo
        {
            Default = def, Files = { "system/l2.ini" },
            List = { new ServerEntry { Id = "main" }, new ServerEntry { Id = "local", Hidden = localHidden } },
        },
    };

    [Fact]
    public void ValidateServers()
    {
        ServerService.Validate(M("main"), new HashSet<string>(M("main").Files.Select(f => PathSafety.Normalize(f.Path))));
        var seen = new HashSet<string>(M("main").Files.Select(f => PathSafety.Normalize(f.Path)));
        Assert.Throws<LauncherException>(() => ServerService.Validate(M("local", localHidden: true), seen)); // hidden default
        Assert.Throws<LauncherException>(() => ServerService.Validate(M("other"), seen));                    // unknown default
        var bad = M("main"); bad.Servers!.List.Add(new ServerEntry { Id = "../x" });
        Assert.Throws<LauncherException>(() => ServerService.Validate(bad, seen));
        var missing = M("main"); missing.Servers!.List.Add(new ServerEntry { Id = "third" });
        Assert.Throws<LauncherException>(() => ServerService.Validate(missing, seen));                       // no server/third/...
        var dup = new HashSet<string>(seen) { "system/l2.ini" };
        Assert.Throws<LauncherException>(() => ServerService.Validate(M("main"), dup));                       // also a normal file
        var prot = M("main"); prot.Servers!.Files = new() { "system/option.ini" };
        Assert.Throws<LauncherException>(() => ServerService.Validate(prot, new HashSet<string> { "server/main/system/option.ini", "server/local/system/option.ini" }));
    }

    [Theory]
    [InlineData("[Game]\r\nIsNative=True\r\nX=1\r\n", "[Game]\r\nIsNative=False\r\nX=1\r\n")]
    [InlineData("[Game]\r\nIsNative=False\r\n", "[Game]\r\nIsNative=False\r\n")]
    [InlineData("[Other]\r\nIsNative=True\r\n[Game]\r\nisnative = true \r\n", "[Other]\r\nIsNative=True\r\n[Game]\r\nisnative = False \r\n")]
    [InlineData("[Game]\r\nA=1\r\n[Video]\r\nB=2", "[Game]\r\nIsNative=False\r\nA=1\r\n[Video]\r\nB=2")]
    [InlineData("[Video]\nB=2", "[Video]\nB=2\n[Game]\nIsNative=False\n")]
    [InlineData("", "[Game]\r\nIsNative=False\r\n")]
    [InlineData("[Game]\r\nIsNative=1", "[Game]\r\nIsNative=False")]
    public void PatchIsNative(string input, string expected) => Assert.Equal(expected, OptionIni.Patch(input));

    [Fact]
    public void EnsureIsNativeFalse_BytePreserving()
    {
        var dir = Directory.CreateTempSubdirectory("l2opt").FullName;
        var sys = Path.Combine(dir, "system"); Directory.CreateDirectory(sys);
        var f = Path.Combine(sys, "Option.ini");
        // single-byte file with non-ASCII bytes must be preserved exactly except the value
        var before = new byte[] { 0x5B, 0x47, 0x61, 0x6D, 0x65, 0x5D, 0x0D, 0x0A }  // [Game]\r\n
            .Concat(System.Text.Encoding.ASCII.GetBytes("Name=")).Concat(new byte[] { 0xC0, 0xFF, 0x81 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("\r\nIsNative=True\r\n[Video]\r\nGamePlayViewportX=1920\r\n")).ToArray();
        File.WriteAllBytes(f, before);
        Assert.True(OptionIni.EnsureIsNativeFalse(dir));
        var after = File.ReadAllBytes(f);
        var expected = System.Text.Encoding.Latin1.GetBytes(System.Text.Encoding.Latin1.GetString(before).Replace("IsNative=True", "IsNative=False"));
        Assert.Equal(expected, after);
        Assert.False(OptionIni.EnsureIsNativeFalse(dir)); // idempotent, no rewrite
        // UTF-16LE with BOM
        var u = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("[Game]\r\nIsNative=True\r\n")).ToArray();
        File.WriteAllBytes(f, u);
        Assert.True(OptionIni.EnsureIsNativeFalse(dir));
        Assert.Equal(new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("[Game]\r\nIsNative=False\r\n")), File.ReadAllBytes(f));
        // missing file -> created minimal
        File.Delete(f);
        Assert.True(OptionIni.EnsureIsNativeFalse(dir));
        Assert.Equal("[Game]\r\nIsNative=False\r\n", File.ReadAllText(f));
        Directory.Delete(dir, true);
    }
}
