using System.Diagnostics;

namespace CloudPrint.Configurator.Core.Exe;

/// <summary>Result of running an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Abstraction over launching the service exe, so <see cref="ServiceExeClient"/> can be unit-tested
/// on any platform with a fake runner (the real one only works on Windows).
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string exePath, IReadOnlyList<string> args, string? stdin, CancellationToken ct = default);
}

/// <summary>Default <see cref="IProcessRunner"/> backed by System.Diagnostics.Process.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string exePath, IReadOnlyList<string> args, string? stdin, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            proc.StandardInput.Close();
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        return new ProcessResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
