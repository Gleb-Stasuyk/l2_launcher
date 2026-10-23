using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using L2Launcher.Core;
using L2Launcher.Core.Local;

namespace L2Launcher;

/// <summary>"Local server" tab: server folder, docker control, settings (applied on restart), admin rights, backups, logs.</summary>
public sealed class LocalViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly MainViewModel _main;
    private readonly IProcessRunner _run = new ProcessRunner();
    private UserPrefs Prefs => _main.Prefs;
    public Loc Loc => _main.Loc;

    public LocalViewModel(MainViewModel main)
    {
        _main = main;
        BrowseCommand = new RelayCommand(() => { Browse(); return Task.CompletedTask; }, () => _main.NotBusy);
        StartCommand = Busy(ct => StartAsync(false, ct));
        StopCommand = Busy(StopAsync);
        RestartCommand = Busy(ct => StartAsync(true, ct));
        ApplyCommand = Busy(ApplyAndRestartAsync);
        RefreshCommand = Busy(async ct => { await RefreshStateAsync(ct); });
        LogsCommand = Busy(LogsAsync);
        LoadCharsCommand = Busy(LoadCharactersAsync);
        GrantGmCommand = Busy(ct => SetGmAsync(DbService.GmLevel, ct));
        RevokeGmCommand = Busy(ct => SetGmAsync(0, ct));
        BackupCommand = Busy(BackupAsync);
        RestoreCommand = Busy(RestoreAsync);
        ResetCommand = Busy(ResetAsync);
        InstallCommand = Busy(InstallAsync);
        OpenFolderCommand = new RelayCommand(() => { _main.OpenShellPublic(Folder); return Task.CompletedTask; }, () => FolderValid);
    }

    private ICommand Busy(Func<CancellationToken, Task> a) => new RelayCommand(() => _main.RunBusyPublic(async ct => { await a(ct); return true; }), () => _main.NotBusy);

    public ICommand BrowseCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LogsCommand { get; }
    public ICommand LoadCharsCommand { get; }
    public ICommand GrantGmCommand { get; }
    public ICommand RevokeGmCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string Folder => Prefs.ServerFolder;
    public bool FolderValid => DockerService.IsServerFolder(Folder);
    private DockerService Docker => new(_run, Folder);
    private DbService Db => new(_run, Folder);

    private LocalSettings _s = new();
    /// <summary>Bound by the settings tabs (two-way into this object).</summary>
    public LocalSettings S { get => _s; set { _s = value; On(); } }

    private ServerState _state = ServerState.Stopped;
    public ServerState State { get => _state; set { _state = value; On(); On(nameof(StateText)); } }
    public string StateText => FolderValid ? Loc["L_State_" + State] : Loc["L_NoFolder"];

    private string _logsText = "";
    public string LogsText { get => _logsText; set { _logsText = value; On(); } }

    public ObservableCollection<CharacterRow> Characters { get; } = new();
    private CharacterRow? _selected;
    public CharacterRow? SelectedCharacter { get => _selected; set { _selected = value; On(); } }

    public bool Autostart
    {
        get => Prefs.AutostartServer;
        set { Prefs.AutostartServer = value; Settings.SavePrefs(Prefs); On(); }
    }

    public void Init()
    {
        if (FolderValid) S = LocalSettings.Load(Folder);
        On(nameof(Folder)); On(nameof(FolderValid)); On(nameof(StateText));
    }

    public void RefreshLoc() => On(nameof(StateText));

    private void Browse()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = Loc["L_PickFolder"] };
        if (FolderValid) d.InitialDirectory = Folder;
        if (d.ShowDialog() != true) return;
        if (!DockerService.IsServerFolder(d.FolderName))
        {
            MessageBox.Show(Loc["L_NotServerFolder"], "L2Start", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Prefs.ServerFolder = d.FolderName;
        Settings.SavePrefs(Prefs);
        _main.AddLog($"Server folder -> {d.FolderName}");
        Init();
        _ = _main.RunBusyPublic(async ct => { await RefreshStateAsync(ct); return true; });
    }

    public async Task<ServerState> RefreshStateAsync(CancellationToken ct)
    {
        if (!FolderValid) { On(nameof(StateText)); return State; }
        State = await Docker.GetStateAsync(ct);
        _main.Status = StateText;
        return State;
    }

    private bool CheckDocker(ServerState s)
    {
        if (s == ServerState.DockerMissing)
        {
            if (MessageBox.Show(Loc["L_DockerMissing"], "L2Start", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _main.OpenShellPublic("https://www.docker.com/products/docker-desktop/");
            return false;
        }
        if (s == ServerState.DockerNotRunning) { _main.Status = Loc["L_State_DockerNotRunning"]; return false; }
        if (s == ServerState.NotInstalled) { _main.Status = Loc["L_State_NotInstalled"]; return false; }
        return true;
    }

    private void Log(ProcResult r, string what)
    {
        _main.AddLog($"{what}: exit {r.ExitCode}");
        foreach (var l in (r.Stdout + "\n" + r.Stderr).Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(30)) _main.AddLog("  " + l.TrimEnd());
    }

    /// <summary>Starts (or recreates) and waits for healthy. Returns true when the server is up.</summary>
    public async Task<bool> EnsureRunningAsync(bool recreate, CancellationToken ct)
    {
        if (!FolderValid) { _main.Status = Loc["L_NoFolder"]; return false; }
        var s = await RefreshStateAsync(ct);
        if (!CheckDocker(s)) return false;
        if (s == ServerState.Running && !recreate) return true;
        _main.Indeterminate = true;
        _main.Status = Loc["L_Starting"];
        var r = await Docker.UpAsync(recreate, ct);
        Log(r, recreate ? "compose up --force-recreate" : "compose up");
        if (!r.Ok) { _main.Status = Loc.F("S_Error", r.Stderr.Trim().Split('\n').LastOrDefault() ?? ""); return false; }
        var ok = await Docker.WaitHealthyAsync(TimeSpan.FromMinutes(12), st => State = st, ct);
        _main.Indeterminate = false;
        _main.Status = ok ? Loc["L_State_Running"] : Loc["L_Slow"];
        return ok;
    }

    private Task StartAsync(bool recreate, CancellationToken ct) => EnsureRunningAsync(recreate, ct);

    private async Task StopAsync(CancellationToken ct)
    {
        if (!FolderValid) return;
        _main.Indeterminate = true; _main.Status = Loc["L_Stopping"];
        Log(await Docker.StopAsync(ct), "compose stop");
        _main.Indeterminate = false;
        await RefreshStateAsync(ct);
    }

    private async Task ApplyAndRestartAsync(CancellationToken ct)
    {
        if (!FolderValid) { _main.Status = Loc["L_NoFolder"]; return; }
        LocalConfigWriter.Apply(Folder, S);
        S = LocalSettings.Load(Folder); // normalized values back into the UI
        _main.AddLog("Settings written: settings.env, modules.env, overrides\\game\\config");
        await EnsureRunningAsync(recreate: true, ct);
    }

    private async Task LogsAsync(CancellationToken ct)
    {
        if (!FolderValid) return;
        var r = await Docker.LogsAsync("game", 400, ct);
        LogsText = r.Ok ? r.Stdout : r.Stderr;
    }

    private async Task LoadCharactersAsync(CancellationToken ct)
    {
        if (!FolderValid) return;
        try
        {
            var rows = await Db.ListCharactersAsync(ct);
            Characters.Clear();
            foreach (var c in rows) Characters.Add(c);
            _main.Status = Loc.F("L_Chars", rows.Count);
        }
        catch (LauncherException ex) { _main.Status = Loc.F("S_Error", ex.Message); }
    }

    private async Task SetGmAsync(int level, CancellationToken ct)
    {
        if (SelectedCharacter is not { } c) { _main.Status = Loc["L_PickChar"]; return; }
        try
        {
            var ok = await Db.SetAccessLevelAsync(c.Name, level, ct);
            _main.AddLog($"accesslevel {c.Name} -> {level}: {ok}");
            _main.Status = ok ? Loc.F(level > 0 ? "L_GmOn" : "L_GmOff", c.Name) : Loc.F("S_Error", c.Name);
            await LoadCharactersAsync(ct);
        }
        catch (LauncherException ex) { _main.Status = Loc.F("S_Error", ex.Message); }
    }

    private async Task<string?> DoBackupAsync(CancellationToken ct)
    {
        try
        {
            _main.Indeterminate = true; _main.Status = Loc["L_BackingUp"];
            var f = await Db.BackupAsync(ct);
            _main.AddLog("Backup: " + f);
            _main.Status = Loc.F("L_BackupDone", f);
            return f;
        }
        catch (LauncherException ex) { _main.Status = Loc.F("S_Error", ex.Message); return null; }
        finally { _main.Indeterminate = false; }
    }

    private async Task BackupAsync(CancellationToken ct)
    {
        if (!FolderValid || !CheckDocker(await RefreshStateAsync(ct))) return;
        await DoBackupAsync(ct);
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        if (!FolderValid || !CheckDocker(await RefreshStateAsync(ct))) return;
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "DB dump (*.sql.gz)|*.sql.gz", InitialDirectory = Directory.Exists(Db.BackupDir) ? Db.BackupDir : Folder };
        if (d.ShowDialog() != true) return;
        if (MessageBox.Show(Loc["L_RestoreConfirm"], "L2Start", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await DoBackupAsync(ct) == null) return;
        _main.Indeterminate = true; _main.Status = Loc["L_Restoring"];
        Log(await _run.RunAsync("docker", new[] { "compose", "stop", "game", "login" }, Folder, ct: ct), "stop game");
        try { await Db.RestoreAsync(d.FileName, ct); _main.AddLog("Restored " + d.FileName); }
        catch (LauncherException ex) { _main.Status = Loc.F("S_Error", ex.Message); return; }
        finally { _main.Indeterminate = false; }
        await EnsureRunningAsync(false, ct);
    }

    private async Task ResetAsync(CancellationToken ct)
    {
        if (!FolderValid || !CheckDocker(await RefreshStateAsync(ct))) return;
        if (MessageBox.Show(Loc["L_ResetConfirm"], "L2Start", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if ((State is ServerState.Running or ServerState.Starting or ServerState.Unhealthy) && await DoBackupAsync(ct) == null) return;
        _main.Indeterminate = true; _main.Status = Loc["L_Resetting"];
        Log(await Docker.DownWithVolumesAsync(ct), "compose down -v");
        _main.Indeterminate = false;
        await EnsureRunningAsync(false, ct);
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        if (!FolderValid) { _main.Status = Loc["L_NoFolder"]; return; }
        var bat = Path.Combine(Folder, "install.bat");
        if (File.Exists(bat)) _main.OpenShellPublic(bat);
        await Task.CompletedTask;
    }
}
