using System.Drawing.Imaging;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>
/// <c>CloudPrint.Configurator.exe --screenshot &lt;dir&gt;</c>: renders the main window and the device editor
/// in each of its states to PNG files and exits. Lets a Windows CI runner (or a box nobody is looking at)
/// produce pictures of the UI so layout can be reviewed without a human clicking through it. Uses a canned
/// hardware inventory so pickers are populated deterministically.
/// </summary>
internal static class ScreenshotHarness
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();

        DeviceEditorForm.InventoryOverride = () => Task.FromResult(new DeviceInventory(
            new[]
            {
                new SerialPortInfo("COM1", "Communications Port (COM1)"),
                new SerialPortInfo("COM5", "USB Serial Port (COM5)", 0x0403, 0x6001, "A50285BI"),
                new SerialPortInfo("COM7", "Silicon Labs CP210x USB to UART Bridge (COM7)", 0x10C4, 0xEA60),
            },
            new[]
            {
                new HidDeviceInfo(0x0B67, 0x555E, "Fairbanks Scales SCB-R9000", "Fairbanks Scales", null, "008D:0020", IsScale: true),
                new HidDeviceInfo(0x0922, 0x8003, "DYMO M10", "DYMO", null, "008D:0020", IsScale: true),
                new HidDeviceInfo(0x046D, 0xC31C, "USB Keyboard", "Logitech", null, "0001:0006", IsScale: false),
            }));

        // Main window, loaded from the committed sample so lists are populated.
        try
        {
            var samplePath = FindSample();
            if (samplePath is not null)
                File.Copy(samplePath, InstallContext.ConfigPath, overwrite: true);
            using var main = new MainForm();
            Capture(main, Path.Combine(outDir, "01-main.png"), fullHeight: null, log);
        }
        catch (Exception ex) { log.Add($"main: FAILED {ex}"); }

        var states = new (string file, DeviceModel? model, string type, bool advanced)[]
        {
            ("10-device-new-default", null, ConfigDefaults.DefaultDeviceType, false),
            ("11-device-serial-raw-advanced", null, ConfigDefaults.DeviceSerialRaw, true),
            ("12-device-serial-scale", Sample("scale-shipping"), ConfigDefaults.DeviceSerialScale, false),
            ("13-device-hid-scale", Sample("scale-counter"), ConfigDefaults.DeviceHidScale, false),
            ("14-device-hid-raw-advanced", null, ConfigDefaults.DeviceHidRaw, true),
            ("15-device-tcp-raw-cubiscan", Sample("cubiscan-125"), ConfigDefaults.DeviceTcpRaw, false),
            ("16-device-tcp-raw-cubiscan-advanced", Sample("cubiscan-125"), ConfigDefaults.DeviceTcpRaw, true),
            ("17-device-serial-idle-auto-advanced", Sample("unknown-usb-serial"), ConfigDefaults.DeviceSerialRaw, true),
        };

        foreach (var (file, model, type, advanced) in states)
        {
            try
            {
                using var dlg = new DeviceEditorForm(model, InstallContext.ServiceExePath);
                dlg.SetState(type, advanced);
                dlg.DetectForScreenshotAsync().GetAwaiter().GetResult();
                Capture(dlg, Path.Combine(outDir, file + ".png"), dlg.FullClientHeight, log);
            }
            catch (Exception ex) { log.Add($"{file}: FAILED {ex}"); }
        }

        try
        {
            using var printer = new PrinterEditorForm(null, new[] { "Zebra_ZP500", "Microsoft Print to PDF" });
            Capture(printer, Path.Combine(outDir, "20-printer-new.png"), null, log);
        }
        catch (Exception ex) { log.Add($"printer: FAILED {ex}"); }

        File.WriteAllLines(Path.Combine(outDir, "screenshots.log"), log);
        Console.WriteLine(string.Join(Environment.NewLine, log));
        return log.Any(l => l.Contains("FAILED")) ? 1 : 0;
    }

    private static void Capture(Form form, string path, int? fullHeight, List<string> log)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.ShowInTaskbar = false;
        var wanted = fullHeight ?? form.ClientSize.Height;
        // Windows clamps a top-level window to the screen; on a small runner screen the dialog scrolls.
        // Ask for the full height, then stitch scrolled tiles into one tall image if we didn't get it.
        form.ClientSize = new Size(form.ClientSize.Width, wanted);
        form.Show();
        Application.DoEvents();
        Thread.Sleep(150);
        Application.DoEvents();

        var visibleH = form.ClientSize.Height;
        var width = form.ClientSize.Width;
        var totalH = Math.Max(wanted, visibleH);
        using var full = new Bitmap(width, totalH);
        using (var g = Graphics.FromImage(full))
            g.Clear(form.BackColor);

        var offset = 0;
        while (true)
        {
            form.AutoScrollPosition = new Point(0, offset);
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();
            var actual = -form.AutoScrollPosition.Y;
            using var tile = new Bitmap(width, visibleH);
            form.DrawToBitmap(tile, new Rectangle(0, 0, width, visibleH));
            using (var g = Graphics.FromImage(full))
                g.DrawImage(tile, 0, actual);
            if (actual + visibleH >= totalH || actual < offset) break; // reached the end (or can't scroll further)
            offset = actual + visibleH - 8; // small overlap so nothing falls between tiles
            if (offset >= totalH) break;
        }

        full.Save(path, ImageFormat.Png);
        log.Add($"{Path.GetFileName(path)}: {full.Width}x{full.Height} (visible {visibleH})");
        form.Hide();
    }

    private static DeviceModel? Sample(string name)
    {
        var path = FindSample();
        if (path is null) return null;
        var config = ConfigStore.Parse(File.ReadAllText(path));
        return config?.Devices.FirstOrDefault(d => d.Name == name);
    }

    private static string? FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "appsettings.sample.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
