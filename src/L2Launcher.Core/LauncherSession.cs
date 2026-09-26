namespace L2Launcher.Core;

/// <summary>
/// Launch flow policy (UI-independent, unit-tested):
///  - nothing is checked automatically at startup;
///  - "Check files" runs a check (+ repair) and never starts the game;
///  - "Play" starts the game directly if a check already succeeded in this launcher session,
///    otherwise it first runs a check (+ repair) and starts the game only if that succeeded.
/// The actual work is injected: check(full, ct) returns true when the client is verified/repaired.
/// </summary>
public sealed class LauncherSession
{
    public delegate Task<bool> CheckFunc(bool full, CancellationToken ct);
    public delegate Task LaunchFunc(CancellationToken ct);

    private readonly CheckFunc _check;
    private readonly LaunchFunc _launch;
    private int _busy;

    public LauncherSession(CheckFunc check, LaunchFunc launch) { _check = check; _launch = launch; }

    /// <summary>True once a check completed successfully in this session.</summary>
    public bool VerifiedThisSession { get; private set; }
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>Call when something may have changed the client (e.g. a future language switch that copies files).</summary>
    public void Invalidate() => VerifiedThisSession = false;

    /// <summary>"Check files" button: full re-hash + repair. Returns the check result.</summary>
    public async Task<bool> CheckFilesAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return false;
        try { return await RunCheck(full: true, ct); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>"Play" button. Returns true if the game was launched.</summary>
    public async Task<bool> PlayAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return false;
        try
        {
            if (!VerifiedThisSession && !await RunCheck(full: false, ct)) return false;
            ct.ThrowIfCancellationRequested();
            await _launch(ct);
            return true;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task<bool> RunCheck(bool full, CancellationToken ct)
    {
        VerifiedThisSession = false;
        VerifiedThisSession = await _check(full, ct);
        return VerifiedThisSession;
    }
}
