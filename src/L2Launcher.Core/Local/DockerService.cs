namespace L2Launcher.Core.Local;

public enum ServerState { DockerMissing, DockerNotRunning, NotInstalled, Stopped, Starting, Running, Unhealthy }

/// <summary>Release package (docker/release): project l2start-local, containers l2l-*.
/// Git checkout (docker/): project l2s, containers l2s-*, needs profiles/solo.env.</summary>
public enum ServerPackageKind { Release, Dev }

/// <summary>docker / docker compose control of the local server package (release l2l-* or git l2s-*).</summary>
public sealed class DockerService
{
    public const string ReleaseGameContainer = "l2l-game";
    public const string ReleaseDbContainer = "l2l-db";
    public const string DevGameContainer = "l2s-game";
    public const string DevDbContainer = "l2s-db";

    /// <summary>Legacy alias: release game container. Prefer instance <see cref="GameContainer"/>.</summary>
    public const string GameContainer = ReleaseGameContainer;
    /// <summary>Legacy alias: release db container. Prefer instance <see cref="DbContainer"/> or <see cref="DbContainerFor"/>.</summary>
    public const string DbContainer = ReleaseDbContainer;

    private readonly IProcessRunner _run;
    public string Folder { get; }
    public ServerPackageKind Kind { get; }
    public DockerService(IProcessRunner run, string folder)
    {
        _run = run;
        Folder = folder;
        Kind = DetectKind(folder) ?? ServerPackageKind.Release;
    }

    public string GameContainerName => Kind == ServerPackageKind.Dev ? DevGameContainer : ReleaseGameContainer;
    public string DbContainerName => Kind == ServerPackageKind.Dev ? DevDbContainer : ReleaseDbContainer;

    /// <summary>Host folder mapped to /opt/l2/overrides (release: overrides/; git: overrides/solo/).</summary>
    public string OverridesRoot => Kind == ServerPackageKind.Dev
        ? Path.Combine(Folder, "overrides", "solo")
        : Path.Combine(Folder, "overrides");

    public static string DbContainerFor(ServerPackageKind kind) =>
        kind == ServerPackageKind.Dev ? DevDbContainer : ReleaseDbContainer;

    public static string GameContainerFor(ServerPackageKind kind) =>
        kind == ServerPackageKind.Dev ? DevGameContainer : ReleaseGameContainer;

    /// <summary>Release package (name: l2start-local) or git docker folder (name: l2s).</summary>
    public static ServerPackageKind? DetectKind(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = Path.Combine(folder, "docker-compose.yml");
        if (!File.Exists(f)) return null;
        var text = File.ReadAllText(f);
        // Prefer exact project names; "name: l2start-local" before "name: l2s" substring checks.
        if (ContainsProjectName(text, "l2start-local")) return ServerPackageKind.Release;
        if (ContainsProjectName(text, "l2s")) return ServerPackageKind.Dev;
        return null;
    }

    static bool ContainsProjectName(string compose, string name)
    {
        // Match "name: <name>" as a YAML key (allow spaces around colon).
        foreach (var line in compose.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("#")) continue;
            if (t.Equals("name: " + name, StringComparison.Ordinal) ||
                t.Equals("name:" + name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsServerFolder(string? folder) => DetectKind(folder) != null;

    public static bool IsInstalled(string folder)
    {
        var kind = DetectKind(folder);
        if (kind == null) return false;
        if (kind == ServerPackageKind.Dev)
            return File.Exists(Path.Combine(folder, "profiles", "solo.env")) || File.Exists(Path.Combine(folder, ".env"));
        return File.Exists(Path.Combine(folder, ".env"));
    }

    public async Task<ServerState> GetStateAsync(CancellationToken ct = default)
    {
        var v = await _run.RunAsync("docker", new[] { "version", "--format", "{{.Server.Version}}" }, ct: ct);
        if (v.ExitCode == -1) return ServerState.DockerMissing;
        if (!v.Ok || string.IsNullOrWhiteSpace(v.Stdout)) return ServerState.DockerNotRunning;
        if (!IsInstalled(Folder)) return ServerState.NotInstalled;
        var r = await _run.RunAsync("docker", new[] { "inspect", "-f", "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}", GameContainerName }, ct: ct);
        return r.Ok ? ParseState(r.Stdout) : ServerState.Stopped;
    }

    public static ServerState ParseState(string inspect)
    {
        var parts = inspect.Trim().Split('|');
        var status = parts[0];
        var health = parts.Length > 1 ? parts[1] : "";
        if (status != "running") return ServerState.Stopped;
        return health switch
        {
            "healthy" => ServerState.Running,
            "unhealthy" => ServerState.Unhealthy,
            "starting" => ServerState.Starting,
            _ => ServerState.Running,
        };
    }

    /// <summary>Compose args with --env-file for the git stack (same as start.bat solo).</summary>
    IEnumerable<string> ComposePrefix()
    {
        yield return "compose";
        if (Kind != ServerPackageKind.Dev) yield break;
        var solo = Path.Combine(Folder, "profiles", "solo.env");
        if (File.Exists(solo)) { yield return "--env-file"; yield return Path.Combine("profiles", "solo.env"); }
        var dot = Path.Combine(Folder, ".env");
        if (File.Exists(dot)) { yield return "--env-file"; yield return ".env"; }
    }

    Task<ProcResult> Compose(CancellationToken ct, params string[] args) =>
        _run.RunAsync("docker", ComposePrefix().Concat(args).ToList(), Folder, ct: ct);

    /// <summary>Start; recreate = re-read settings.env/modules.env/overrides (a plain restart does not).</summary>
    public Task<ProcResult> UpAsync(bool recreate, CancellationToken ct = default) =>
        recreate ? Compose(ct, "up", "-d", "--force-recreate", "login", "game") : Compose(ct, "up", "-d");

    public Task<ProcResult> StopAsync(CancellationToken ct = default) => Compose(ct, "stop");
    public Task<ProcResult> StopServicesAsync(CancellationToken ct = default, params string[] services) =>
        Compose(ct, new[] { "stop" }.Concat(services).ToArray());
    public Task<ProcResult> PullAsync(CancellationToken ct = default) => Compose(ct, "pull");
    /// <summary>Deletes the world (all volumes). Caller makes a backup first.</summary>
    public Task<ProcResult> DownWithVolumesAsync(CancellationToken ct = default) => Compose(ct, "down", "-v");
    public Task<ProcResult> LogsAsync(string service = "game", int tail = 400, CancellationToken ct = default) =>
        Compose(ct, "logs", "--no-color", "--tail", tail.ToString(), service);

    /// <summary>Waits until the game server is healthy (first start can take minutes).</summary>
    public async Task<bool> WaitHealthyAsync(TimeSpan timeout, Action<ServerState>? tick = null, CancellationToken ct = default, TimeSpan? poll = null)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = await GetStateAsync(ct);
            tick?.Invoke(s);
            if (s == ServerState.Running) return true;
            if (s is ServerState.DockerMissing or ServerState.DockerNotRunning) return false;
            await Task.Delay(poll ?? TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }
}
