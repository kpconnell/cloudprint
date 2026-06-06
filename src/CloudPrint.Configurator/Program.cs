namespace CloudPrint.Configurator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            SelfInstaller.Uninstall();
            return;
        }

        InstallContext.Initialize();

        // Packaged one-file build: lay down the service binary + register before showing the GUI.
        if (InstallContext.Packaged)
        {
            try
            {
                SelfInstaller.InstallFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CloudPrint setup could not place its files (try running as administrator):\n\n" + ex.Message,
                    "CloudPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
