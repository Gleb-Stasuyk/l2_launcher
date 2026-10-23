using System.Diagnostics;
using System.Text;

namespace L2Launcher.Core.Local;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs external programs (docker). Abstracted for tests.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="file"/>; stdin from <paramref name="stdin"/>, stdout to <paramref name="stdoutTo"/> when given (binary), else captured.
    /// ExitCode -1 = program not found.</summary>
    Task<ProcResult> RunAsync(string file, IReadOnlyList<string> args, string? workDir = null, Stream? stdin = null,
        Stream? stdoutTo = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcResult> RunAsync(string file, IReadOnlyList<string> args, string? workDir = null, Stream? stdin = null,
        Stream? stdoutTo = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = stdoutTo == null ? Encoding.UTF8 : null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("start failed"); }
        catch (System.ComponentModel.Win32Exception) { return new ProcResult(-1, "", $"{file} not found"); }
        using (p)
        {
            var errTask = p.StandardError.ReadToEndAsync(ct);
            Task<string> outTask;
            if (stdoutTo != null) outTask = p.StandardOutput.BaseStream.CopyToAsync(stdoutTo, ct).ContinueWith(_ => "", ct);
            else outTask = p.StandardOutput.ReadToEndAsync(ct);
            if (stdin != null)
            {
                await stdin.CopyToAsync(p.StandardInput.BaseStream, ct);
                p.StandardInput.Close();
            }
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            return new ProcResult(p.ExitCode, await outTask, await errTask);
        }
    }
}
