using System.Reflection;

namespace CloudPrint.Configurator;

/// <summary>
/// Access to the CloudPrint.Service.exe binary embedded into this exe at CI build time.
/// When present, this exe is a self-contained one-file installer; when absent (dev builds),
/// the configurator falls back to a service exe sitting next to it.
/// </summary>
internal static class EmbeddedService
{
    private const string ResourceName = "CloudPrint.Service.exe";

    /// <summary>True when the service binary was embedded (i.e. this is a packaged installer).</summary>
    public static bool IsPresent =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(ResourceName);

    /// <summary>Extracts the embedded service binary to <paramref name="destPath"/>, retrying if briefly locked.</summary>
    public static void ExtractTo(string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException("Embedded service binary not found.");
                using var file = File.Create(destPath);
                resource.CopyTo(file);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                // The target may still be locked by a service that was just stopped; back off and retry.
                Thread.Sleep(1500);
            }
        }
    }
}
