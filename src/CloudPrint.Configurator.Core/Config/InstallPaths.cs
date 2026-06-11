namespace CloudPrint.Configurator.Core.Config;

/// <summary>Well-known install locations and names.</summary>
public static class InstallPaths
{
    public const string ServiceName = "CloudPrint";
    public const string ServiceDisplayName = "CloudPrint";
    public const string ServiceDescription =
        "Polls AWS SQS for print jobs and routes them to local printers";

    public const string ServiceExeName = "CloudPrint.Service.exe";
    public const string ConfigFileName = "appsettings.json";

    /// <summary>%ProgramFiles%\CloudPrint — where the service binaries live.</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CloudPrint");

    /// <summary>C:\ProgramData\CloudPrint — runtime data root.</summary>
    public static string ProgramDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudPrint");

    public static string LogDir => Path.Combine(ProgramDataDir, "logs");
    public static string DumpDir => Path.Combine(ProgramDataDir, "dumps");

    public static string ConfigPath => Path.Combine(InstallDir, ConfigFileName);
    public static string ServiceExePath => Path.Combine(InstallDir, ServiceExeName);

    // Fixed Windows paths the service writes to (also embedded in the Serilog block).
    public const string DumpPathWindows = @"C:\ProgramData\CloudPrint\dumps";
}
