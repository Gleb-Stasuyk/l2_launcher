using System.Diagnostics;

namespace L2Launcher.Core;

public static class GameStarter
{
    /// <summary>Writes the language to Option.ini, checks RU files, then starts system\l2.exe with working dir = system.</summary>
    public static Process Start(LauncherConfig cfg, GameLanguage lang, string? extraArgs = null)
    {
        var exe = PathSafety.ResolveSafePath(cfg.GamePath, cfg.GameExecutable);
        if (!File.Exists(exe)) throw new LauncherException("no_game_exe", $"Game executable not found: {exe}");
        if (lang == GameLanguage.Russian)
        {
            var missing = GameLanguageService.MissingRussianFiles(cfg.GamePath);
            if (missing.Count > 0)
                throw new LauncherException("ru_files_missing", "Russian language files are missing: " + string.Join(", ", missing));
        }
        GameLanguageService.Apply(cfg.GamePath, lang);
        var args = string.Join(" ", new[] { cfg.GameArguments, extraArgs }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        try
        {
            return Process.Start(psi) ?? throw new LauncherException("start_failed", "Could not start the game.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740) // ERROR_ELEVATION_REQUIRED
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            return Process.Start(psi) ?? throw new LauncherException("start_failed", "Could not start the game.");
        }
    }
}
