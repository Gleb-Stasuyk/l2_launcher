using System.Text;
using System.Text.RegularExpressions;

namespace L2Launcher.Core.Local;

public sealed record CharacterRow(string Name, int Level, int AccessLevel, string Account, bool Online);

/// <summary>Database of the local server through the l2l-db container (no host port, password from the container env).</summary>
public sealed class DbService
{
    public const int GmLevel = 100;
    private static readonly Regex NameRx = new("^[A-Za-z0-9]{1,16}$");
    private readonly IProcessRunner _run;
    private readonly string _folder;
    public DbService(IProcessRunner run, string folder) { _run = run; _folder = folder; }

    private static readonly string[] Mariadb =
        { "exec", "-i", DockerService.DbContainer, "sh", "-c", "mariadb -uroot -p\"$MARIADB_ROOT_PASSWORD\" -N -B l2start" };

    private async Task<string> SqlAsync(string sql, CancellationToken ct)
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(sql));
        var r = await _run.RunAsync("docker", Mariadb, stdin: stdin, ct: ct);
        if (!r.Ok) throw new LauncherException("db_failed", r.Stderr.Trim());
        return r.Stdout;
    }

    /// <summary>Live players only (bot accounts are 'b' + 10 random chars, created by bots-core).</summary>
    public async Task<List<CharacterRow>> ListCharactersAsync(CancellationToken ct = default)
    {
        var o = await SqlAsync("SELECT char_name, level, accesslevel, account_name, online FROM characters " +
                               "WHERE account_name NOT REGEXP '^b[a-z0-9]{10}$' ORDER BY char_name;", ct);
        return ParseRows(o);
    }

    public static List<CharacterRow> ParseRows(string tsv) =>
        tsv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.Split('\t'))
           .Where(c => c.Length >= 5)
           .Select(c => new CharacterRow(c[0], int.Parse(c[1]), int.Parse(c[2]), c[3], c[4] == "1"))
           .ToList();

    public static string AccessSql(string name, int level)
    {
        if (!NameRx.IsMatch(name)) throw new LauncherException("bad_name", $"Bad character name: {name}");
        if (level != 0 && level != GmLevel) throw new LauncherException("bad_level", $"Access level must be 0 or {GmLevel}");
        return $"UPDATE characters SET accesslevel={level} WHERE char_name='{name}'; SELECT ROW_COUNT();";
    }

    /// <summary>Takes effect at the next login of the character.</summary>
    public async Task<bool> SetAccessLevelAsync(string name, int level, CancellationToken ct = default)
    {
        // Check if character is online before applying (GM rights don't take effect while logged in)
        var online = await SqlAsync($"SELECT online FROM characters WHERE char_name='{name}';", ct);
        if (online.Trim() == "1")
            throw new LauncherException("char_online", $"Character {name} is currently online");
        
        return (await SqlAsync(AccessSql(name, level), ct)).Trim() == "1";
    }

    public string BackupDir => Path.Combine(_folder, "backups");

    public async Task<string> BackupAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupDir);
        var file = Path.Combine(BackupDir, $"l2l-{DateTime.Now:yyyyMMdd-HHmmss}.sql.gz");
        await using (var fs = File.Create(file))
        {
            var r = await _run.RunAsync("docker", new[] { "exec", DockerService.DbContainer, "sh", "-c",
                "mariadb-dump -uroot -p\"$MARIADB_ROOT_PASSWORD\" --single-transaction --routines --triggers l2start | gzip -c" }, stdoutTo: fs, ct: ct);
            if (!r.Ok) { fs.Close(); File.Delete(file); throw new LauncherException("backup_failed", r.Stderr.Trim()); }
        }
        if (new FileInfo(file).Length < 100) { File.Delete(file); throw new LauncherException("backup_failed", "dump too small"); }
        return file;
    }

    /// <summary>Restore a dump (the game/login servers must be stopped, the db running).</summary>
    public async Task RestoreAsync(string file, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(file);
        var r = await _run.RunAsync("docker", new[] { "exec", "-i", DockerService.DbContainer, "sh", "-c",
            "gunzip -c | mariadb -uroot -p\"$MARIADB_ROOT_PASSWORD\" l2start" }, stdin: fs, ct: ct);
        if (!r.Ok) throw new LauncherException("restore_failed", r.Stderr.Trim());
    }
}
