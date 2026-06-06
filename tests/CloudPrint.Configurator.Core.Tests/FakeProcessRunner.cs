using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator.Core.Tests;

/// <summary>Records invocations and returns queued results, so ServiceExeClient is testable off-Windows.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public readonly List<(string Exe, IReadOnlyList<string> Args, string? Stdin)> Calls = new();
    private readonly Queue<ProcessResult> _results = new();

    public FakeProcessRunner Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _results.Enqueue(new ProcessResult(exitCode, stdout, stderr));
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string exePath, IReadOnlyList<string> args, string? stdin, CancellationToken ct = default)
    {
        Calls.Add((exePath, args, stdin));
        var result = _results.Count > 0 ? _results.Dequeue() : new ProcessResult(0, string.Empty, string.Empty);
        return Task.FromResult(result);
    }
}
