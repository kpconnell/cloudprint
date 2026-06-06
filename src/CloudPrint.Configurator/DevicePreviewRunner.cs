using System.Diagnostics;
using CloudPrint.Configurator.Core.Devices;

namespace CloudPrint.Configurator;

/// <summary>
/// Runs the service's <c>preview-device</c> subcommand and streams live readings back to the UI, so the
/// add-device dialog can show "reading now: 12.34 lb". Callbacks fire on a background thread — the caller
/// marshals to the UI thread. Stop()/Dispose() kills the child (the OS releases the COM/HID handle).
/// </summary>
internal sealed class DevicePreviewRunner : IDisposable
{
    private readonly string _exePath;
    private Process? _process;
    private CancellationTokenSource? _cts;

    public DevicePreviewRunner(string exePath) => _exePath = exePath;

    public void Start(string deviceJson, Action<DeviceReadingPreview> onReading, Action<string?> onEnded)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("preview-device");

                var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Could not start the service binary.");
                _process = process;

                await process.StandardInput.WriteAsync(deviceJson.AsMemory(), ct);
                process.StandardInput.Close();

                while (!ct.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line is null)
                        break;
                    if (DeviceReadingPreview.TryParse(line, out var reading))
                        onReading(reading);
                }

                if (!ct.IsCancellationRequested)
                {
                    var stderr = (await process.StandardError.ReadToEndAsync(ct)).Trim();
                    if (process.ExitCode != 0 && stderr.Length > 0)
                        error = stderr;
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by the caller — not an error.
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (!ct.IsCancellationRequested)
                onEnded(error);
        }, ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* race with natural exit */ }

        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
