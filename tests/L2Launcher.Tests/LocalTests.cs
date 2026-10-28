using System.IO.Compression;
using System.Text;
using L2Launcher.Core;
using L2Launcher.Core.Local;
using Xunit;

namespace L2Launcher.Tests;

public class LocalTests
{
    sealed class FakeRunner : IProcessRunner
    {
        public List<(string file, string args, string? dir, string? stdin)> Calls { get; } = new();
        public Func<string, ProcResult> Answer = _ => new ProcResult(0, "", "");
        public async Task<ProcResult> RunAsync(string file, IReadOnlyList<string> args, string? workDir = null, Stream? stdin = null, Stream? stdoutTo = null, CancellationToken ct = default)
        {
            string? input = null;
            if (stdin != null) { using var r = new StreamReader(stdin, leaveOpen: true); input = await r.ReadToEndAsync(); }
            var a = string.Join(" ", args);
            Calls.Add((file, a, workDir, input));
            var res = Answer(a);
            if (stdoutTo != null) { var b = Encoding.UTF8.GetBytes(res.Stdout); await stdoutTo.WriteAsync(b); return res with { Stdout = "" }; }
            return res;
        }
    }

    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "l2l-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    static string ServerFolder(bool installed = true)
    {
        var d = TempDir();
        File.WriteAllText(Path.Combine(d, "docker-compose.yml"), "name: l2start-local\n");
        if (installed) File.WriteAllText(Path.Combine(d, ".env"), "L2S_VERSION=0.1.0\n");
        return d;
    }

    static string DevServerFolder(bool withSoloEnv = true)
    {
        var d = TempDir();
        File.WriteAllText(Path.Combine(d, "docker-compose.yml"), "name: l2s\n");
        if (withSoloEnv)
        {
            Directory.CreateDirectory(Path.Combine(d, "profiles"));
            File.WriteAllText(Path.Combine(d, "profiles", "solo.env"), "L2S_DB_PASSWORD=test\n");
        }
        return d;
    }

    [Theory]
    [InlineData("running|healthy", ServerState.Running)]
    [InlineData("running|starting", ServerState.Starting)]
    [InlineData("running|unhealthy", ServerState.Unhealthy)]
    [InlineData("exited|", ServerState.Stopped)]
    [InlineData("running|", ServerState.Running)]
    public void ParsesContainerState(string inspect, ServerState expected) => Assert.Equal(expected, DockerService.ParseState(inspect + "\n"));

    [Fact]
    public async Task DetectsDockerMissingAndNotRunning()
    {
        var f = new FakeRunner { Answer = _ => new ProcResult(-1, "", "not found") };
        Assert.Equal(ServerState.DockerMissing, await new DockerService(f, ServerFolder()).GetStateAsync());
        f.Answer = _ => new ProcResult(1, "", "error during connect");
        Assert.Equal(ServerState.DockerNotRunning, await new DockerService(f, ServerFolder()).GetStateAsync());
    }

    [Fact]
    public async Task NotInstalledWithoutEnv()
    {
        var f = new FakeRunner { Answer = a => new ProcResult(0, a.StartsWith("version") ? "27.0" : "running|healthy", "") };
        Assert.Equal(ServerState.NotInstalled, await new DockerService(f, ServerFolder(installed: false)).GetStateAsync());
        Assert.Equal(ServerState.Running, await new DockerService(f, ServerFolder()).GetStateAsync());
    }

    [Fact]
    public async Task RestartRecreatesSoSettingsAreReread()
    {
        var f = new FakeRunner();
        var dir = ServerFolder();
        await new DockerService(f, dir).UpAsync(recreate: true);
        Assert.Equal("compose up -d --force-recreate login game", f.Calls[0].args);
        Assert.Equal(dir, f.Calls[0].dir);
    }

    [Fact]
    public void ServerFolderDetection()
    {
        Assert.True(DockerService.IsServerFolder(ServerFolder()));
        Assert.False(DockerService.IsServerFolder(TempDir()));
        Assert.False(DockerService.IsServerFolder(""));
    }

    [Fact]
    public void DetectsReleaseAndDevFolders()
    {
        Assert.Equal(ServerPackageKind.Release, DockerService.DetectKind(ServerFolder()));
        Assert.Equal(ServerPackageKind.Dev, DockerService.DetectKind(DevServerFolder()));
        Assert.Null(DockerService.DetectKind(TempDir()));
        Assert.True(DockerService.IsServerFolder(DevServerFolder()));
    }

    [Fact]
    public async Task DevUpPassesSoloEnvFile()
    {
        var f = new FakeRunner();
        var dir = DevServerFolder();
        await new DockerService(f, dir).UpAsync(recreate: false);
        Assert.Equal("compose --env-file profiles\\solo.env up -d", f.Calls[0].args.Replace('/', '\\'));
        Assert.Equal(dir, f.Calls[0].dir);
    }

    [Fact]
    public async Task DevInspectsL2sGameContainer()
    {
        var f = new FakeRunner { Answer = a => new ProcResult(0, a.StartsWith("version") ? "27.0" : (a.Contains("l2s-game") ? "running|healthy" : "missing"), "") };
        Assert.Equal(ServerState.Running, await new DockerService(f, DevServerFolder()).GetStateAsync());
        Assert.Contains(f.Calls, c => c.args.Contains("l2s-game"));
    }

    [Fact]
    public void WriterUsesSoloOverridesForDev()
    {
        var dir = DevServerFolder();
        LocalConfigWriter.Apply(dir, new LocalSettings { RateXp = 11 });
        Assert.True(File.Exists(Path.Combine(dir, "overrides", "solo", "game", "config", "Rates.ini")));
        Assert.False(File.Exists(Path.Combine(dir, "overrides", "game", "config", "Rates.ini")));
    }

    [Fact]
    public async Task DbServiceUsesMatchingContainer()
    {
        var f = new FakeRunner { Answer = _ => new ProcResult(0, "Hero\t80\t100\tadmin\t0\n", "") };
        await new DbService(f, DevServerFolder()).ListCharactersAsync();
        Assert.Contains(f.Calls, c => c.args.Contains("l2s-db"));
    }

    [Fact]
    public void IniKeysReplaceKeepCommentsDedupeAndAppend()
    {
        var t = "# comment\r\nRateXp = 10\r\nRateSp = 10\r\nRateXp = 10\r\n";
        var r = IniKeys.Set(t, new Dictionary<string, string> { ["RateXp"] = "25", ["RateDropManor"] = "3" });
        Assert.Equal("# comment\r\nRateXp = 25\r\nRateSp = 10\r\nRateDropManor = 3\r\n", r);
        Assert.Equal("A=1\n", IniKeys.Set("", new Dictionary<string, string> { ["A"] = "1" }, "=").Replace("\r", ""));
    }

    [Fact]
    public void WriterProducesAllServerKeys()
    {
        var dir = ServerFolder();
        var s = new LocalSettings
        {
            RateXp = 25, FloatingDrop = false, RateAdena = 7, ClassChange2 = false, NpcBuffer = false, BuffDurationMinutes = 20,
            BotsEnabled = false, GmShop = false, CommunityBuffer = false, OpenCatacombs = true, EnchantMax = 20, EnchantChance = 70,
            AutoLearnMaxLevel = 40, StartLevel = 20, StartAdena = 1000, TownDensity = 50,
        };
        LocalConfigWriter.Apply(dir, s);
        var env = IniKeys.Parse(File.ReadAllText(Path.Combine(dir, "settings.env")));
        Assert.Equal("1,3", env["L2S_CLASS_MASTER"]);
        Assert.Equal("off", env["L2S_STOCK_NPC_BUFFER"]);
        Assert.Equal("1200", env["L2S_BUFF_DURATION"]);
        Assert.Equal("20", env["L2S_ENCHANT_MAX"]);
        Assert.Equal("70", env["L2S_ENCHANT_CHANCE"]);
        var mods = IniKeys.Parse(File.ReadAllText(Path.Combine(dir, "modules.env")));
        Assert.Equal("off", mods["L2_MODULE_BOTS_CORE"]);
        Assert.Equal("off", mods["L2_MODULE_TOWN_BUFFER"]);
        Assert.Equal("off", mods["L2_MODULE_RATES_FLOATING"]);
        Assert.Equal("40", mods["L2_MOD_RETURN_SKILL__AUTOLEARNMAXLEVEL"]);
        Assert.Equal("8", mods["L2_MOD_BOTS_CORE__PRESENCETOWNLARGEMAX"]);
        var cfg = Path.Combine(dir, "overrides", "game", "config");
        var rates = IniKeys.Parse(File.ReadAllText(Path.Combine(cfg, "Rates.ini")));
        Assert.Equal("25", rates["RateXp"]);
        Assert.StartsWith("57,7;", rates["DropAmountMultiplierByItemId"]);
        var player = IniKeys.Parse(File.ReadAllText(Path.Combine(cfg, "Player.ini")));
        Assert.Equal("20", player["StartingLevel"]);
        Assert.Equal("1000", player["StartingAdena"]);
        var feat = IniKeys.Parse(File.ReadAllText(Path.Combine(cfg, "Feature.ini")));
        Assert.Equal("True", feat["AltOpenCatacumbs"]);
        var cb = IniKeys.Parse(File.ReadAllText(Path.Combine(cfg, "Custom", "CommunityBoard.ini")));
        Assert.Equal("False", cb["CustomCommunityBoard"]);
        Assert.Equal("False", cb["CommunityEnableBuffs"]);
        Assert.Equal("False", cb["CommunityEnableMultisells"]);
        // round trip
        Assert.Equal(25, LocalSettings.Load(dir).RateXp);
    }

    [Fact]
    public void FloatingDropKeepsStockMultipliers()
    {
        var o = LocalConfigWriter.IniOverrides(new LocalSettings { FloatingDrop = true, RateDrop = 9 });
        Assert.Equal("1", o["Rates.ini"]["DeathDropAmountMultiplier"]);
        Assert.Equal("on", LocalConfigWriter.ModulesEnv(new LocalSettings { FloatingDrop = true })["L2_MODULE_RATES_FLOATING"]);
    }

    [Fact]
    public void ClassMasterOffWhenNoneChecked() =>
        Assert.Equal("off", LocalConfigWriter.SettingsEnv(new LocalSettings { ClassChange1 = false, ClassChange2 = false, ClassChange3 = false })["L2S_CLASS_MASTER"]);

    [Fact]
    public void CustomCommunityBoardEnabledWhenFeaturesAreUsed()
    {
        // both off -> CustomCommunityBoard=False
        var both_off = LocalConfigWriter.IniOverrides(new LocalSettings { CommunityBuffer = false, GmShop = false });
        Assert.Equal("False", both_off[Path.Combine("Custom", "CommunityBoard.ini")]["CustomCommunityBoard"]);
        Assert.Equal("False", both_off[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableBuffs"]);
        Assert.Equal("False", both_off[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableMultisells"]);

        // buffer on -> CustomCommunityBoard=True
        var buff_on = LocalConfigWriter.IniOverrides(new LocalSettings { CommunityBuffer = true, GmShop = false });
        Assert.Equal("True", buff_on[Path.Combine("Custom", "CommunityBoard.ini")]["CustomCommunityBoard"]);
        Assert.Equal("True", buff_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableBuffs"]);
        Assert.Equal("False", buff_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableMultisells"]);

        // shop on -> CustomCommunityBoard=True
        var shop_on = LocalConfigWriter.IniOverrides(new LocalSettings { CommunityBuffer = false, GmShop = true });
        Assert.Equal("True", shop_on[Path.Combine("Custom", "CommunityBoard.ini")]["CustomCommunityBoard"]);
        Assert.Equal("False", shop_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableBuffs"]);
        Assert.Equal("True", shop_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableMultisells"]);

        // both on -> CustomCommunityBoard=True
        var both_on = LocalConfigWriter.IniOverrides(new LocalSettings { CommunityBuffer = true, GmShop = true });
        Assert.Equal("True", both_on[Path.Combine("Custom", "CommunityBoard.ini")]["CustomCommunityBoard"]);
        Assert.Equal("True", both_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableBuffs"]);
        Assert.Equal("True", both_on[Path.Combine("Custom", "CommunityBoard.ini")]["CommunityEnableMultisells"]);
    }


    [Fact]
    public void NormalizeClamps()
    {
        var n = new LocalSettings { RateXp = -5, EnchantChance = 500, StartLevel = 0, BuffDurationMinutes = 99999 }.Normalized();
        Assert.Equal(0, n.RateXp); Assert.Equal(100, n.EnchantChance); Assert.Equal(1, n.StartLevel); Assert.Equal(600, n.BuffDurationMinutes);
    }

    [Fact]
    public async Task DbAdminSqlAndInjectionGuard()
    {
        Assert.Equal("UPDATE characters SET accesslevel=100 WHERE char_name='Ork'; SELECT ROW_COUNT();", DbService.AccessSql("Ork", 100));
        Assert.Throws<LauncherException>(() => DbService.AccessSql("x'; DROP TABLE characters;--", 100));
        Assert.Throws<LauncherException>(() => DbService.AccessSql("Ork", 50));
        var callCount = 0;
        var f = new FakeRunner
        {
            Answer = _ =>
            {
                callCount++;
                return callCount == 1
                    ? new ProcResult(0, "0\n", "")  // online check: offline
                    : new ProcResult(0, "1\n", ""); // UPDATE successful
            }
        };
        Assert.True(await new DbService(f, ServerFolder()).SetAccessLevelAsync("Ork", 100));
        Assert.Contains("exec -i l2l-db", f.Calls[0].args);
        Assert.Contains("SELECT online", f.Calls[0].stdin);
        Assert.Contains("accesslevel=100", f.Calls[1].stdin);
    }

    [Fact]
    public async Task DbRejectsGrantingGmToOnlineCharacter()
    {
        // First call checks online status, returns "1" (online)
        var callCount = 0;
        var f = new FakeRunner
        {
            Answer = _ =>
            {
                callCount++;
                return callCount == 1
                    ? new ProcResult(0, "1\n", "")  // online check: character is online
                    : new ProcResult(0, "1\n", ""); // would be the UPDATE call (not reached)
            }
        };
        var ex = await Assert.ThrowsAsync<LauncherException>(() => new DbService(f, ServerFolder()).SetAccessLevelAsync("Ork", 100));
        Assert.Equal("char_online", ex.Code);
        Assert.Contains("Ork", ex.Message);
        Assert.Equal(1, callCount); // Only the online check should have been called
        Assert.Contains("SELECT online", f.Calls[0].stdin);
    }

    [Fact]
    public async Task DbAllowsGrantingGmToOfflineCharacter()
    {
        var callCount = 0;
        var f = new FakeRunner
        {
            Answer = _ =>
            {
                callCount++;
                return callCount == 1
                    ? new ProcResult(0, "0\n", "")  // online check: character is offline
                    : new ProcResult(0, "1\n", ""); // UPDATE successful
            }
        };
        Assert.True(await new DbService(f, ServerFolder()).SetAccessLevelAsync("Ork", 100));
        Assert.Equal(2, callCount); // Both online check and UPDATE should have been called
        Assert.Contains("SELECT online", f.Calls[0].stdin);
        Assert.Contains("accesslevel=100", f.Calls[1].stdin);
    }

    [Fact]
    public void DbParsesCharacterRows()
    {
        var rows = DbService.ParseRows("Ork\t45\t100\tgleb\t1\r\nElf\t20\t0\tgleb\t0\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new CharacterRow("Ork", 45, 100, "gleb", true), rows[0]);
    }

    [Fact]
    public async Task BackupWritesDumpFile()
    {
        var dir = ServerFolder();
        var f = new FakeRunner { Answer = _ => new ProcResult(0, new string('x', 500), "") };
        var file = await new DbService(f, dir).BackupAsync();
        Assert.True(File.Exists(file));
        Assert.Equal(500, new FileInfo(file).Length);
        f.Answer = _ => new ProcResult(0, "x", "");
        await Assert.ThrowsAsync<LauncherException>(() => new DbService(f, dir).BackupAsync());
    }

    static string MakePatch()
    {
        var zip = Path.Combine(TempDir(), ClientPatch.FileName);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string n, string c) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(c); }
        Add("system/l2.ini", "LOCAL");
        Add("system/skillgrp.dat", "SKILLGRP");
        Add("lang/en/system/skillname-e.dat", "EN");
        Add("lang/ru/system/skillname-e.dat", "RU");
        return zip;
    }

    [Fact]
    public void ClientPatchAppliesWithBackupAndLanguage()
    {
        var game = TempDir();
        Directory.CreateDirectory(Path.Combine(game, "system"));
        File.WriteAllText(Path.Combine(game, "system", "l2.ini"), "REMOTE");
        var zip = MakePatch();
        Assert.Equal(3, ClientPatch.Mismatched(game, zip, "ru").Count);
        Assert.Equal(3, ClientPatch.Apply(game, zip, "ru"));
        Assert.Equal("LOCAL", File.ReadAllText(Path.Combine(game, "system", "l2.ini")));
        Assert.Equal("RU", File.ReadAllText(Path.Combine(game, "system", "skillname-e.dat")));
        Assert.Equal("REMOTE", File.ReadAllText(Path.Combine(game, ClientPatch.BackupDir, "system", "l2.ini")));
        Assert.Equal(0, ClientPatch.Apply(game, zip, "ru"));
        Assert.Equal(1, ClientPatch.Apply(game, zip, "en"));
        Assert.Equal("EN", File.ReadAllText(Path.Combine(game, "system", "skillname-e.dat")));
        // backup of l2.ini is not overwritten by the patched file
        File.WriteAllText(Path.Combine(game, "system", "l2.ini"), "CHANGED");
        ClientPatch.Apply(game, zip, "en");
        Assert.Equal("REMOTE", File.ReadAllText(Path.Combine(game, ClientPatch.BackupDir, "system", "l2.ini")));
        Assert.True(ClientPatch.Restore(game) >= 1);
        Assert.Equal("REMOTE", File.ReadAllText(Path.Combine(game, "system", "l2.ini")));
    }

    [Fact]
    public async Task WaitHealthyStopsWhenDockerGone()
    {
        var f = new FakeRunner { Answer = _ => new ProcResult(1, "", "down") };
        Assert.False(await new DockerService(f, ServerFolder()).WaitHealthyAsync(TimeSpan.FromSeconds(5), poll: TimeSpan.FromMilliseconds(10)));
    }
}

public class LocalCliTests
{
    sealed class OkRunner : IProcessRunner
    {
        public Task<ProcResult> RunAsync(string file, IReadOnlyList<string> args, string? workDir = null, Stream? stdin = null, Stream? stdoutTo = null, CancellationToken ct = default) =>
            Task.FromResult(new ProcResult(0, args[0] == "version" ? "27.0" : "running|healthy", ""));
    }

    [Fact]
    public async Task ApplyWithSetWritesAndRestarts()
    {
        var d = Path.Combine(Path.GetTempPath(), "l2cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "docker-compose.yml"), "name: l2start-local\n");
        File.WriteAllText(Path.Combine(d, ".env"), "L2S_VERSION=0.1.0\n");
        var (code, o) = await LocalCli.RunAsync(new[] { "--local", "apply", d, "--set", "RateXp=33", "--set", "NpcBuffer=False" }, new OkRunner());
        Assert.Equal(0, code);
        Assert.Contains("healthy=True", o);
        Assert.Equal(33, LocalSettings.Load(d).RateXp);
        Assert.Contains("L2S_STOCK_NPC_BUFFER=off", File.ReadAllText(Path.Combine(d, "settings.env")));
    }
}
