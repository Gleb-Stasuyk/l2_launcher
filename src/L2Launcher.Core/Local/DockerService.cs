namespace L2Launcher.Core.Local;

public enum ServerState { DockerMissing, DockerNotRunning, NotInstalled, Stopped, Starting, Running, Unhealthy }

/// <summary>docker / docker compose control of the local server package (project "l2start-local", containers l2l-*).</summary>
public sealed class DockerService
{
    public const string GameContainer = "l2l-game";
    public const string DbContainer = "l2l-db";
    private readonly IProcessRunner _run;
    public string Folder { get; }
    public DockerService(IProcessRunner run, string folder) { _run = run; Folder = folder; }

    /// <summary>A folder of the server package: docker-compose.yml of project l2start-local.</summary>
    public static bool IsServerFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var f = Path.Combine(folder, "docker-compose.yml");
        return File.Exists(f) && File.ReadAllText(f).Contains("name: l2start-local");
    }

    public static bool IsInstalled(string folder) => File.Exists(Path.Combine(folder, ".env"));

    public async Task<ServerState> GetStateAsync(CancellationToken ct = default)
    {
        var v = await _run.RunAsync("docker", new[] { "version", "--format", "{{.Server.Version}}" }, ct: ct);
        if (v.ExitCode == -1) return ServerState.DockerMissing;
        if (!v.Ok || string.IsNullOrWhiteSpace(v.Stdout)) return ServerState.DockerNotRunning;
        if (!IsInstalled(Folder)) return ServerState.NotInstalled;
        var r = await _run.RunAsync("docker", new[] { "inspect", "-f", "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}", GameContainer }, ct: ct);
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

    private Task<ProcResult> Compose(CancellationToken ct, params string[] args) =>
        _run.RunAsync("docker", new[] { "compose" }.Concat(args).ToList(), Folder, ct: ct);

    /// <summary>Start; recreate = re-read settings.env/modules.env/overrides (a plain restart does not).</summary>
    public Task<ProcResult> UpAsync(bool recreate, CancellationToken ct = default) =>
        recreate ? Compose(ct, "up", "-d", "--force-recreate", "login", "game") : Compose(ct, "up", "-d");

    public Task<ProcResult> StopAsync(CancellationToken ct = default) => Compose(ct, "stop");
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
