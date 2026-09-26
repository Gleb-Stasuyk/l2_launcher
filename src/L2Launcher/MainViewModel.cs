using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using L2Launcher.Core;

namespace L2Launcher;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public Loc Loc { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<ServerOption> ServerOptions { get; } = new();
    private bool _serversVisible;
    public bool ServersVisible { get => _serversVisible; set { _serversVisible = value; On(); } }
    /// <summary>Manifest whose server list is shown (fresh one after a check, else the cached signed one).</summary>
    private Manifest? _serverSource;

    private readonly HttpClient _http;
    private readonly UpdateService _svc;
    private LauncherConfig _cfg = new();
    private UserPrefs _prefs = new();
    private Manifest? _manifest;
    private CancellationTokenSource? _cts;
    private readonly LauncherSession _session;
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    public MainViewModel()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"L2Launcher/{CurrentVersion}");
        _svc = new UpdateService(_http);
        _session = new LauncherSession(CheckCoreAsync, LaunchCoreAsync);
        PlayCommand = new RelayCommand(() => RunBusy(ct => _session.PlayAsync(ct)), () => !Busy);
        CheckCommand = new RelayCommand(() => RunBusy(ct => _session.CheckFilesAsync(ct)), () => !Busy);
        CancelCommand = new RelayCommand(() => { _cts?.Cancel(); return Task.CompletedTask; }, () => Busy);
        OpenFolderCommand = new RelayCommand(() => { OpenShell(_cfg.GamePath); return Task.CompletedTask; });
        WebsiteCommand = new RelayCommand(() => { OpenShell(_cfg.WebsiteUrl); return Task.CompletedTask; }, () => Settings.IsHttps(_cfg.WebsiteUrl));
        ToggleLogCommand = new RelayCommand(() => { LogVisible = !LogVisible; return Task.CompletedTask; });
    }

    // ------------------------------------------------------------------ bindable state
    public ICommand PlayCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand WebsiteCommand { get; }
    public ICommand ToggleLogCommand { get; }

    private string _title = BuildDefaults.ServerName;
    public string Title { get => _title; set { _title = value; On(); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; On(); } }
    private string _detail = "";
    public string Detail { get => _detail; set { _detail = value; On(); } }
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; On(); } }
    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; set { _indeterminate = value; On(); } }
    private bool _busy;
    public bool Busy { get => _busy; set { _busy = value; On(); On(nameof(NotBusy)); CommandManager.InvalidateRequerySuggested(); } }
    public bool NotBusy => !Busy;
    private bool _logVisible;
    public bool LogVisible { get => _logVisible; set { _logVisible = value; On(); } }
    public string VersionText => $"v{CurrentVersion.ToString(3)}";

    public bool IsRussian { get => Loc.Russian; set { if (value) SetLang(true); else On(); } }
    public bool IsEnglish { get => !Loc.Russian; set { if (value) SetLang(false); else On(); } }

    private void SetLang(bool ru)
    {
        if (Loc.Russian != ru)
        {
            Loc.SetRussian(ru);
            RefreshServerNames();
            _prefs.Language = ru ? "ru" : "en";
            Settings.SavePrefs(_prefs);
            AddLog($"Language -> {_prefs.Language}");
        }
        On(nameof(IsRussian)); On(nameof(IsEnglish));
    }
    private GameLanguage Lang => IsRussian ? GameLanguage.Russian : GameLanguage.English;

    public void AddLog(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Log.Add(line);
            while (Log.Count > 2000) Log.RemoveAt(0);
        });
    }

    // ------------------------------------------------------------------ startup
    public async Task InitAsync()
    {
        Status = Loc["S_Start"];
        var (cfg, src, warnings) = Settings.LoadConfig();
        _cfg = cfg;
        _prefs = Settings.LoadPrefs();
        bool ru = _prefs.Language switch
        {
            "ru" => true,
            "en" => false,
            // First start: take the language from Option.ini, else from Windows UI language.
            _ => LanguageService.ReadActive(_cfg.GamePath) is string a
                    ? a == "ru"
                    : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
        };
        Loc.SetRussian(ru); On(nameof(IsRussian)); On(nameof(IsEnglish));
        Title = string.IsNullOrWhiteSpace(_cfg.ServerName) ? BuildDefaults.ServerName : _cfg.ServerName;
        // Server list from the last signed manifest (no network at startup).
        var cached = ManifestCache.TryLoad(Settings.LocalDataDir, _cfg.ManifestUrl, _svc);
        if (cached != null) ShowServers(cached);
        AddLog($"L2Launcher {CurrentVersion} config={src ?? "(built-in defaults)"}");
        AddLog($"Manifest: {_cfg.ManifestUrl}");
        AddLog($"Game path: {_cfg.GamePath}");
        foreach (var w in warnings) AddLog("WARN " + w);
        if (PathSafety.LooksLikeLineageClient(_cfg.GamePath) == false)
            AddLog("No system\\l2.exe in game path yet: a full download will be performed.");
        // No automatic check at startup: files are checked on "Check files", or on "Play" if not yet checked this session.
        Status = Loc["S_Idle"];
        await Task.CompletedTask;
    }

    private async Task RunBusy(Func<CancellationToken, Task<bool>> action)
    {
        if (Busy) return;
        Busy = true;
        _cts = new CancellationTokenSource();
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { Status = Loc["S_Cancelled"]; Detail = ""; }
        catch (Exception ex) { AddLog("ERROR " + ex); Status = Loc.F("S_Error", ex.Message); }
        finally { Indeterminate = false; Busy = false; }
    }

    // ------------------------------------------------------------------ update / verify
    /// <summary>Fetch signed manifest, (self-update), verify, repair. Returns true when the client is ready.</summary>
    private async Task<bool> CheckCoreAsync(bool full, CancellationToken ct)
    {
        var progress = new Progress<UpdateProgress>(ShowProgress);
        try
        {
            Indeterminate = true; Status = Loc["S_Fetch"]; Detail = "";
            _manifest = await _svc.FetchManifestAsync(_cfg.ManifestUrl, ct);
            AddLog($"Manifest OK (signature verified): {_manifest.Product} {_manifest.Version}, {_manifest.Files.Count} files");
            if (_svc.LastRaw is { } raw) ManifestCache.Save(Settings.LocalDataDir, _cfg.ManifestUrl, raw.Bytes, raw.Sig);
            var fresh = _manifest;
            Application.Current?.Dispatcher.Invoke(() => ShowServers(fresh));

            // Self-update (only when running as the published single-file exe)
            var exe = Environment.ProcessPath;
            if (exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && LauncherUpdater.ShouldUpdate(_manifest, CurrentVersion))
            {
                Status = Loc.F("S_SelfUpdate", _manifest!.Launcher!.Version);
                AddLog($"Self-update {CurrentVersion} -> {_manifest.Launcher.Version}");
                if (await new LauncherUpdater(_svc).TryUpdateAsync(_manifest, _cfg.ManifestUrl, CurrentVersion, exe, ct))
                {
                    Application.Current.Shutdown();
                    return false;
                }
                AddLog("Self-update skipped/failed; continuing with current version.");
            }

            Indeterminate = false;
            var cache = HashCache.Load(HashCache.DefaultFileFor(_cfg.GamePath));
            var m = _manifest;
            var vr = await Task.Run(() => _svc.Verify(_cfg.GamePath, m, cache, full, progress, ct), ct);
            AddLog($"Verify: ok={vr.Ok} missing={vr.Missing.Count} changed={vr.Changed.Count} ({vr.BytesToDownload / 1048576.0:F1} MiB)");
            foreach (var f in vr.Missing.Take(50)) AddLog("  missing " + f.Path);
            foreach (var f in vr.Changed.Take(50)) AddLog("  changed " + f.Path);
            var todo = vr.ToDownload.ToList();
            if (todo.Count > 0)
            {
                await Task.Run(() => _svc.DownloadAsync(_cfg.GamePath, m, _cfg.ManifestUrl, todo, cache, progress, AddLog, ct), ct);
                Detail = Loc.F("S_Repaired", todo.Count);
            }
            else Detail = Loc.F("S_UpToDate", vr.Ok);
            var removed = LanguageService.DeleteObsolete(_cfg.GamePath, m, AddLog);
            if (removed > 0) AddLog($"Removed {removed} obsolete file(s)");
            var langCopied = await Task.Run(() => LanguageService.Apply(_cfg.GamePath, m, Lang, cache, AddLog, full), ct);
            AddLog($"Language {LanguageService.Code(Lang)}: {langCopied} file(s) copied into system");
            if (ServerService.HasServers(m))
            {
                var sid = SelectedServer(m);
                var srvCopied = await Task.Run(() => ServerService.Apply(_cfg.GamePath, m, sid, cache, AddLog, full), ct);
                AddLog($"Server {sid}: {srvCopied} file(s) copied into system");
            }
            Progress = 100;
            Status = Loc.F("S_Ready", m.Version);
            return true;
        }
        catch (OperationCanceledException) { Status = Loc["S_Cancelled"]; Detail = ""; return false; }
        catch (LauncherException ex) when (ex.Code == "server_unreachable" && _cfg.AllowOfflinePlay && PathSafety.LooksLikeLineageClient(_cfg.GamePath))
        {
            AddLog("ERROR " + ex.Message);
            Status = Loc["S_Offline"]; Detail = "";
            return true;
        }
        catch (LauncherException ex)
        {
            AddLog($"ERROR [{ex.Code}] {ex.Message}");
            var key = "E_" + ex.Code;
            Status = Loc.F("S_Error", Loc[key] == key ? ex.Message : Loc[key]);
            Detail = ex.Code == "hash_mismatch" || ex.Code == "download_failed" ? ex.Message : "";
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog("ERROR " + ex);
            Status = Loc.F("S_Error", ex.Message);
            return false;
        }
        finally
        {
            Indeterminate = false;
        }
    }

    private void ShowProgress(UpdateProgress p)
    {
        switch (p.Stage)
        {
            case UpdateStage.Verifying:
                Status = Loc.F("S_Verify", p.Done, p.Total);
                Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                Detail = p.CurrentFile ?? "";
                break;
            case UpdateStage.Downloading:
                var pct = p.BytesTotal == 0 ? 0 : 100.0 * p.Bytes / p.BytesTotal;
                Progress = pct;
                Status = Loc.F("S_Download", (int)pct, Size(p.Bytes), Size(p.BytesTotal), Size((long)p.BytesPerSecond));
                Detail = p.CurrentFile ?? "";
                break;
        }
    }

    private static string Size(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):F2} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):F1} MB" : $"{b / 1024.0:F0} KB";

    // ------------------------------------------------------------------ play
    /// <summary>Starts the game (optional pre-launch gate, Option.ini language). Called by LauncherSession.</summary>
    private async Task LaunchCoreAsync(CancellationToken ct)
    {
        try
        {
            string? extraArgs = null;
            if (!string.IsNullOrWhiteSpace(_cfg.PreLaunchUrl))
            {
                Status = Loc["S_Gate"];
                var gate = new PreLaunchGate(_http);
                var resp = await gate.CheckAsync(_cfg.PreLaunchUrl, new PreLaunchGate.Request(
                    CurrentVersion.ToString(3), _manifest?.Version ?? "", IsRussian ? "ru" : "en", _session.VerifiedThisSession, PreLaunchGate.MachineId()),
                    ct);
                AddLog($"Gate: allow={resp.Allow} msg={resp.Message}");
                if (!resp.Allow)
                {
                    Status = Loc.F("GateDenied", resp.Message ?? "-");
                    return;
                }
                extraArgs = resp.GameArguments;
            }
            if (_manifest != null && LanguageService.HasLanguages(_manifest))
            {
                Status = Loc["S_Lang"];
                var cache = HashCache.Load(HashCache.DefaultFileFor(_cfg.GamePath));
                var m = _manifest;
                var n = await Task.Run(() => LanguageService.Apply(_cfg.GamePath, m, Lang, cache, AddLog), ct);
                AddLog($"Language {LanguageService.Code(Lang)} active ({n} file(s) copied)");
            }
            else AddLog("Language files not applied (no verified manifest in this session).");
            if (_manifest != null && ServerService.HasServers(_manifest))
            {
                Status = Loc["S_Server"];
                var cache = HashCache.Load(HashCache.DefaultFileFor(_cfg.GamePath));
                var m = _manifest;
                var sid = SelectedServer(m);
                var n = await Task.Run(() => ServerService.Apply(_cfg.GamePath, m, sid, cache, AddLog), ct);
                AddLog($"Server {sid} active ({n} file(s) copied)");
            }
            try { if (OptionIni.EnsureIsNativeFalse(_cfg.GamePath)) AddLog("Option.ini: [Game] IsNative=False set"); }
            catch (Exception ex) { AddLog("WARN cannot update Option.ini: " + ex.Message); }
            Status = Loc["S_Launching"];
            var p = GameStarter.Start(_cfg, extraArgs);
            AddLog($"Started {_cfg.GameExecutable} pid={p.Id}");
            if (_cfg.CloseOnLaunch) Application.Current.Shutdown();
            else Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }
        catch (LauncherException ex)
        {
            AddLog($"ERROR [{ex.Code}] {ex.Message}");
            var key = "E_" + ex.Code;
            Status = Loc[key] == key ? Loc.F("S_Error", ex.Message) : Loc[key];
        }
    }

    // ------------------------------------------------------------------ servers
    private string SelectedServer(Manifest m) => ServerService.Resolve(m, _prefs.Server);

    private void ShowServers(Manifest m)
    {
        _serverSource = m;
        ServerOptions.Clear();
        if (!ServerService.HasServers(m)) { ServersVisible = false; return; }
        var sel = SelectedServer(m);
        foreach (var s in ServerService.Visible(m))
            ServerOptions.Add(new ServerOption(this, s, ServerService.DisplayName(s, Loc.Russian), s.Id == sel));
        ServersVisible = ServerOptions.Count > 1;
        AddLog($"Servers: {string.Join(", ", ServerOptions.Select(o => o.Id))}; selected {sel}");
    }

    private void RefreshServerNames()
    {
        foreach (var o in ServerOptions) o.Display = ServerService.DisplayName(o.Entry, Loc.Russian);
    }

    internal void SelectServer(string id)
    {
        if (Busy) { foreach (var o in ServerOptions) o.Refresh(); return; }
        if (_prefs.Server != id)
        {
            _prefs.Server = id;
            Settings.SavePrefs(_prefs);
            AddLog($"Server -> {id}");
        }
        foreach (var o in ServerOptions) o.SetSelected(o.Id == id);
    }

    private void OpenShell(string target)
    {
        try { if (!string.IsNullOrWhiteSpace(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AddLog("ERROR " + ex.Message); }
    }
}

public sealed class ServerOption : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly MainViewModel _vm;
    public ServerEntry Entry { get; }
    public string Id => Entry.Id;
    private string _display;
    public string Display { get => _display; set { _display = value; PropertyChanged?.Invoke(this, new(nameof(Display))); } }
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (value) _vm.SelectServer(Id); else Refresh(); }
    }
    public ServerOption(MainViewModel vm, ServerEntry e, string display, bool selected) { _vm = vm; Entry = e; _display = display; _selected = selected; }
    internal void SetSelected(bool v) { _selected = v; Refresh(); }
    internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
}
