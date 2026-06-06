using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator;

/// <summary>
/// Resolves where the service exe and config live. Packaged (embedded) builds install into
/// %ProgramFiles%\CloudPrint; dev builds operate next to the running exe.
/// </summary>
internal static class InstallContext
{
    public static bool Packaged { get; private set; }
    public static string ServiceExePath { get; private set; } = string.Empty;
    public static string ConfigPath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        Packaged = EmbeddedService.IsPresent;
        if (Packaged)
        {
            ServiceExePath = InstallPaths.ServiceExePath; // %ProgramFiles%\CloudPrint\CloudPrint.Service.exe
            ConfigPath = InstallPaths.ConfigPath;
        }
        else
        {
            ServiceExePath = Path.Combine(AppContext.BaseDirectory, InstallPaths.ServiceExeName);
            ConfigPath = Path.Combine(AppContext.BaseDirectory, InstallPaths.ConfigFileName);
        }
    }
}
