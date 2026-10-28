namespace L2Launcher.Core.Local;

/// <summary>Headless smoke commands: L2Launcher.exe --local &lt;status|apply|restart|chars|backup&gt; &lt;serverFolder&gt; [--set Key=Value ...].
/// Output goes to the returned text (the WPF exe writes it to %LOCALAPPDATA%\L2Launcher\cli.txt).</summary>
public static class LocalCli
{
    public static async Task<(int code, string output)> RunAsync(string[] args, IProcessRunner? runner = null)
    {
        var run = runner ?? new ProcessRunner();
        var i = Array.IndexOf(args, "--local");
        if (i < 0 || args.Length < i + 3) return (2, "usage: --local <status|apply|restart|chars|backup> <serverFolder> [--set Key=Value ...]");
        var cmd = args[i + 1];
        var folder = args[i + 2];
        if (!DockerService.IsServerFolder(folder)) return (2, $"not a server folder: {folder}");
        var docker = new DockerService(run, folder);
        var o = new System.Text.StringBuilder();
        switch (cmd)
        {
            case "status":
                o.AppendLine("state=" + await docker.GetStateAsync());
                return (0, o.ToString());
            case "apply":
            case "restart":
            {
                if (cmd == "apply")
                {
                    var s = LocalSettings.Load(folder);
                    for (int k = i + 3; k < args.Length - 1; k++)
                        if (args[k] == "--set") Set(s, args[++k]);
                    LocalConfigWriter.Apply(folder, s);
                    o.AppendLine("settings written");
                }
                var r = await docker.UpAsync(true);
                o.AppendLine($"compose up --force-recreate: exit {r.ExitCode}");
                var ok = await docker.WaitHealthyAsync(TimeSpan.FromMinutes(12));
                o.AppendLine("healthy=" + ok);
                return (ok ? 0 : 1, o.ToString());
            }
            case "chars":
                foreach (var c in await new DbService(run, folder).ListCharactersAsync()) o.AppendLine($"{c.Name}\t{c.Level}\t{c.AccessLevel}\t{c.Account}");
                return (0, o.ToString());
            case "backup":
                return (0, await new DbService(run, folder).BackupAsync());
            default:
                return (2, "unknown command " + cmd);
        }
    }

    static void Set(LocalSettings s, string kv)
    {
        var p = kv.Split('=', 2);
        var prop = typeof(LocalSettings).GetProperty(p[0]) ?? throw new ArgumentException("unknown setting " + p[0]);
        prop.SetValue(s, Convert.ChangeType(p[1], prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
    }
}
