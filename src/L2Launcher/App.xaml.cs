using System.IO;
using System.Windows;
using L2Launcher.Core;

namespace L2Launcher;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--local"))
        {
            // headless smoke mode (no window): result in %LOCALAPPDATA%\L2Launcher\cli.txt
            var (code, output) = Task.Run(() => Core.Local.LocalCli.RunAsync(e.Args)).GetAwaiter().GetResult(); // off the UI context (no deadlock)
            try { Directory.CreateDirectory(Settings.LocalDataDir); File.WriteAllText(Path.Combine(Settings.LocalDataDir, "cli.txt"), output); } catch { }
            Shutdown(code);
            return;
        }
        _mutex = new Mutex(true, @"Local\L2Launcher_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("The launcher is already running. / Лаунчер уже запущен.", "L2Start");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, a) =>
        {
            WriteCrash(a.Exception);
            MessageBox.Show(a.Exception.Message, "L2Start", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrash(a.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    internal static void WriteCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.LocalDataDir);
            File.AppendAllText(Path.Combine(Settings.LocalDataDir, "crash.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
