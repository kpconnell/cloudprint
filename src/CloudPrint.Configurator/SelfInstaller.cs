using System.Diagnostics;
using System.Reflection;
using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator;

/// <summary>
/// Self-install logic for the one-file installer: extracts the embedded service binary into
/// %ProgramFiles%\CloudPrint, drops a copy of itself there for later reconfiguration, and registers
/// an Add/Remove Programs entry. Also handles --uninstall. Mirrors what scripts/install.ps1 set up.
/// </summary>
internal static class SelfInstaller
{
    private const string ArpKey =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CloudPrint";

    /// <summary>Lays the service binary + a copy of this exe into Program Files and registers ARP.</summary>
    public static void InstallFiles()
    {
        Directory.CreateDirectory(InstallPaths.InstallDir);

        // If re-running over an existing install, the service exe is locked — stop the service first.
        if (File.Exists(InstallPaths.ServiceExePath))
            Run("sc.exe", "stop", InstallPaths.ServiceName);

        EmbeddedService.ExtractTo(InstallPaths.ServiceExePath);

        var installedExe = Path.Combine(InstallPaths.InstallDir, "CloudPrint.exe");
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) &&
            !string.Equals(self, installedExe, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(self))
        {
            try { File.Copy(self, installedExe, overwrite: true); }
            catch { /* best effort — a stale copy may be locked */ }
        }

        WriteArpEntry(installedExe);
    }

    /// <summary>Stops/removes the service, deletes the ARP entry, and removes the install directory.</summary>
    public static void Uninstall()
    {
        Run("sc.exe", "stop", InstallPaths.ServiceName);
        Run("sc.exe", "delete", InstallPaths.ServiceName);
        Run("reg.exe", "delete", ArpKey, "/f");
        try
        {
            if (Directory.Exists(InstallPaths.InstallDir))
                Directory.Delete(InstallPaths.InstallDir, recursive: true);
        }
        catch { /* the running exe may live here; leftover files are harmless */ }
    }

    private static void WriteArpEntry(string installedExe)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        AddReg("DisplayName", "REG_SZ", "CloudPrint");
        AddReg("DisplayVersion", "REG_SZ", version);
        AddReg("Publisher", "REG_SZ", "CloudPrint");
        AddReg("InstallLocation", "REG_SZ", InstallPaths.InstallDir);
        AddReg("DisplayIcon", "REG_SZ", installedExe);
        AddReg("UninstallString", "REG_SZ", $"\"{installedExe}\" --uninstall");
        AddReg("NoModify", "REG_DWORD", "1");
        AddReg("NoRepair", "REG_DWORD", "1");
    }

    private static void AddReg(string name, string type, string data) =>
        Run("reg.exe", "add", ArpKey, "/v", name, "/t", type, "/d", data, "/f");

    private static void Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { /* best effort */ }
    }
}
