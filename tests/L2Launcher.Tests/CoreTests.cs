using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using L2Launcher.Core;
using Xunit;

namespace L2Launcher.Tests;

public class IniTests
{
    [Fact]
    public void SetKey_ReplacesOnlyIsNative_PreservesCrlfAndOtherLines()
    {
        var src = File.ReadAllText("data/Option.sample.ini", Encoding.Latin1);
        Assert.Contains("IsNative=True", src);
        var en = GameLanguageService.SetKey(src, "Game", "IsNative", "False");
        Assert.Contains("IsNative=False", en);
        Assert.DoesNotContain("IsNative=True", en);
        Assert.Equal(src.Replace("IsNative=True", "IsNative=False"), en);
        var back = GameLanguageService.SetKey(en, "Game", "IsNative", "True");
        Assert.Equal(src, back);
    }

    [Fact]
    public void SetKey_AddsSectionAndKey()
    {
        Assert.Equal("[Game]\r\nIsNative=True\r\n", GameLanguageService.SetKey("", "Game", "IsNative", "True"));
        var t = GameLanguageService.SetKey("[Video]\r\nA=1\r\n[Game]\r\nB=2\r\n\r\n[X]\r\nC=3\r\n", "game", "IsNative", "False");
        Assert.Equal("[Video]\r\nA=1\r\n[Game]\r\nB=2\r\nIsNative=False\r\n\r\n[X]\r\nC=3\r\n", t);
    }

    [Fact]
    public void Apply_And_Read_RoundTrip_OnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("l2ini").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "system"));
        File.Copy("data/Option.sample.ini", Path.Combine(dir, "system", "Option.ini"));
        Assert.Equal(GameLanguage.Russian, GameLanguageService.Read(dir));
        GameLanguageService.Apply(dir, GameLanguage.English);
        Assert.Equal(GameLanguage.English, GameLanguageService.Read(dir));
        var bytesEn = File.ReadAllBytes(Path.Combine(dir, "system", "Option.ini"));
        var orig = File.ReadAllBytes("data/Option.sample.ini");
        Assert.Equal(orig.Length + 1, bytesEn.Length); // "True" -> "False"
        GameLanguageService.Apply(dir, GameLanguage.Russian);
        Assert.Equal(orig, File.ReadAllBytes(Path.Combine(dir, "system", "Option.ini")));
    }

    [Fact]
    public void Apply_CreatesOptionIniWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory("l2ini").FullName;
        GameLanguageService.Apply(dir, GameLanguage.Russian);
        Assert.Equal("[Game]\r\nIsNative=True\r\n", File.ReadAllText(Path.Combine(dir, "system", "Option.ini")));
    }
}

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

    [Fact]
    public async Task InstallVerifyRepair()
    {
        var refc = MakeRefClient();
        var outDir = Path.Combine(_work, "out");
        var fakeLauncher = Path.Combine(_work, "L2Launcher.exe");
        File.WriteAllBytes(fakeLauncher, new byte[1234]);
        Run("python3", $"\"{Root}/tools/build_manifest.py\" --client \"{refc}\" --overlay /workspace/l2ru/enru_switch/pkg --enru " +
                       $"--launcher-exe \"{fakeLauncher}\" --launcher-version 9.9.9 --version test.1 --out \"{outDir}\"");

        int port = FreePort();
        _server = Process.Start(new ProcessStartInfo("python3", $"-m http.server {port} --bind 127.0.0.1 --directory \"{outDir}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true });
        var url = $"http://127.0.0.1:{port}/manifest.json";
        using var http = new HttpClient();
        for (int i = 0; i < 50; i++) { try { await http.GetAsync(url); break; } catch { await Task.Delay(100); } }

        var svc = new UpdateService(http, allowHttpForTests: true);
        var m = await svc.FetchManifestAsync(url, default);
        Assert.Equal("test.1", m.Version);
        var paths = m.Files.Select(f => f.Path.ToLowerInvariant()).ToList();
        Assert.Contains("system/castlename-k.dat", paths);       // -k copy of English file (install_finish.bat logic)
        Assert.Contains("system/itemname-k.dat", paths);         // Russian from enru pkg
        Assert.Contains("systextures/l2font-r.utx", paths);
        Assert.Contains("system/localization.ini", paths);
        Assert.DoesNotContain("system/option.ini", paths);       // user config excluded
        Assert.DoesNotContain("system/user.ini", paths);
        Assert.DoesNotContain("system/l2.log", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("enru_switch/"));
        // the RU itemname must be the package one, not the English copy
        var ruItem = m.Files.First(f => f.Path.Equals("system/itemname-k.dat", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FileIntegrity.Sha256("/workspace/l2ru/enru_switch/pkg/system/itemname-k.dat"), ruItem.Sha256);

        // fresh install into an empty folder that already has the player's own Option.ini
        var game = Path.Combine(_work, "game");
        Directory.CreateDirectory(Path.Combine(game, "system"));
        var myOpt = "[Game]\r\nIsNative=True\r\nMyOwnSetting=42\r\n";
        File.WriteAllText(Path.Combine(game, "system", "Option.ini"), myOpt);
        var cache = HashCache.Load(null);
        var v1 = svc.Verify(game, m, cache, full: false, null, default);
        Assert.Equal(m.Files.Count, v1.Missing.Count);
        await svc.DownloadAsync(game, m, url, v1.ToDownload.ToList(), cache, null, null, default);
        var v2 = svc.Verify(game, m, HashCache.Load(null), full: true, null, default);
        Assert.Equal(m.Files.Count, v2.Ok);
        Assert.Equal(myOpt, File.ReadAllText(Path.Combine(game, "system", "Option.ini")));
        Assert.Empty(GameLanguageService.MissingRussianFiles(game));

        // tamper one file (same size) + delete one -> only those two are re-downloaded
        var nw = Path.Combine(game, "system", "nwindow.dll");
        var b = File.ReadAllBytes(nw); b[10] ^= 0xFF; File.WriteAllBytes(nw, b);
        File.Delete(Path.Combine(game, "systextures", "L2Font-r.utx"));
        var v3 = svc.Verify(game, m, HashCache.Load(null), full: true, null, default);
        Assert.Single(v3.Changed); Assert.Single(v3.Missing);
        Assert.Equal(new[] { "systextures/L2Font-r.utx" }, GameLanguageService.MissingRussianFiles(game));
        await svc.DownloadAsync(game, m, url, v3.ToDownload.ToList(), HashCache.Load(null), null, null, default);
        Assert.Equal(m.Files.Count, svc.Verify(game, m, HashCache.Load(null), true, null, default).Ok);

        // language switch writes only IsNative
        GameLanguageService.Apply(game, GameLanguage.English);
        Assert.Equal(myOpt.Replace("IsNative=True", "IsNative=False"), File.ReadAllText(Path.Combine(game, "system", "Option.ini")));

        // tampered manifest -> rejected
        var bytes = File.ReadAllBytes(Path.Combine(outDir, "manifest.json"));
        var sig = File.ReadAllText(Path.Combine(outDir, "manifest.json.sig"));
        bytes[bytes.Length / 2] ^= 1;
        var ex = Assert.Throws<LauncherException>(() => svc.ParseAndVerify(bytes, sig));
        Assert.Equal("bad_signature", ex.Code);

        // self-update entry present and consistent
        Assert.Equal("9.9.9", m.Launcher!.Version);
        Assert.Equal(FileIntegrity.Sha256(fakeLauncher), m.Launcher.Sha256);
        Assert.Equal($"http://127.0.0.1:{port}/files/launcher/L2Launcher.exe", UpdateService.FileUrl(UpdateService.FilesBase(m, url), m.Launcher.Path));
    }

    public void Dispose()
    {
        try { _server?.Kill(true); } catch { }
        try { Directory.Delete(_work, true); } catch { }
    }
}
