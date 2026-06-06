using System.Diagnostics;
using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator;

/// <summary>
/// Windows-only install actions: create runtime directories, write + lock down the config file,
/// and register/restart the service. Mirrors what scripts/install.ps1 does, but driven from the GUI.
/// Lives in the WinForms shell (not Core) because it touches sc.exe / icacls.
/// </summary>
internal static class WindowsInstaller
{
    /// <summary>Creates the ProgramData log directory (and dump directory when dumping is enabled).</summary>
    public static void EnsureRuntimeDirectories(bool dumps, Action<string> log)
    {
        Directory.CreateDirectory(InstallPaths.LogDir);
        log($"Ensured log directory: {InstallPaths.LogDir}");
        if (dumps)
        {
            Directory.CreateDirectory(InstallPaths.DumpDir);
            log($"Ensured dump directory: {InstallPaths.DumpDir}");
        }
    }

    /// <summary>Writes appsettings.json next to the service exe and restricts it to Administrators + SYSTEM.</summary>
    public static void WriteAndSecureConfig(string configPath, CloudPrintConfig config, Action<string> log)
    {
        ConfigStore.Save(configPath, config);
        log($"Wrote configuration: {configPath}");

        // Break inheritance and grant full control to Administrators and SYSTEM only (config holds secrets).
        var (code, _, err) = Run("icacls", configPath, "/inheritance:r",
            "/grant:r", "BUILTIN\\Administrators:(F)", "/grant:r", "NT AUTHORITY\\SYSTEM:(F)");
        if (code != 0)
            throw new InvalidOperationException($"Failed to lock down config file: {err}");
        log("Locked config to Administrators and SYSTEM only.");
    }

    /// <summary>Stops/removes any existing service, registers a fresh one, sets failure policy, and starts it.</summary>
    public static void RegisterAndStartService(string serviceExePath, Action<string> log)
    {
        // Remove any existing registration (ignore errors — the service may not exist yet).
        Run("sc.exe", "stop", InstallPaths.ServiceName);
        Run("sc.exe", "delete", InstallPaths.ServiceName);
        log("Removed any existing service registration.");

        var (code, _, err) = Run("sc.exe", "create", InstallPaths.ServiceName,
            "binPath=", serviceExePath,
            "start=", "auto",
            "DisplayName=", InstallPaths.ServiceDisplayName);
        if (code != 0)
            throw new InvalidOperationException($"Failed to create service: {err}");

        Run("sc.exe", "description", InstallPaths.ServiceName, InstallPaths.ServiceDescription);

        // Auto-restart on failure after 5s, 10s, then 30s (matches install.ps1).
        Run("sc.exe", "failure", InstallPaths.ServiceName,
            "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
        log("Registered service with auto-restart policy.");

        var (startCode, _, startErr) = Run("sc.exe", "start", InstallPaths.ServiceName);
        if (startCode != 0)
            throw new InvalidOperationException($"Service registered but failed to start: {startErr}");
        log("Started CloudPrint service.");
    }

    private static (int Code, string StdOut, string StdErr) Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
