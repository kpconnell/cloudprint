using System.Reflection;

namespace CloudPrint.Configurator;

/// <summary>
/// Build version stamped by CI (-p:Version from the release tag). Dev builds without the
/// property fall back to the default 1.0.0.
/// </summary>
internal static class AppVersion
{
    public static string Display { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "unknown";
}
