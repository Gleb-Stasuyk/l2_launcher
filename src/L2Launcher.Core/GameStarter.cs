using System.Diagnostics;

namespace L2Launcher.Core;

public static class GameStarter
{
    /// <summary>Starts system\l2.exe with working dir = system. (Language files are applied before, by LanguageService.)</summary>
    public static Process Start(LauncherConfig cfg, string? extraArgs = null)
    {
        var exe = PathSafety.ResolveSafePath(cfg.GamePath, cfg.GameExecutable);
        if (!File.Exists(exe)) throw new LauncherException("no_game_exe", $"Game executable not found: {exe}");
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
