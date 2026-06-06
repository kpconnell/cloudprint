using System.Drawing;
using System.Drawing.Printing;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>
/// Tabbed configurator: Transport, Printers, Devices, Apply. All non-UI logic lives in
/// CloudPrint.Configurator.Core (config model, queue naming, exe client) which is unit-tested;
/// this form is the thin Windows-only shell over it.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly string ServiceExePath =
        Path.Combine(AppContext.BaseDirectory, InstallPaths.ServiceExeName);

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, InstallPaths.ConfigFileName);

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // Transport tab
    private readonly RadioButton _rbSqs = new() { Text = "SQS (multiple printers)", Left = 16, Top = 16, Width = 200 };
    private readonly RadioButton _rbHttp = new() { Text = "HTTP API (single printer)", Left = 230, Top = 16, Width = 220 };
    private readonly ComboBox _region = new();
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true };
    private readonly Label _credResult = new() { Left = 16, Top = 250, Width = 600, ForeColor = Color.DarkGreen };
    private readonly Panel _httpApiPanel = new() { Left = 16, Top = 290, Width = 620, Height = 180 };
    private readonly TextBox _apiUrl = new();
    private readonly TextBox _ackUrl = new();
    private readonly TextBox _apiHeaderName = new();
    private readonly TextBox _apiHeaderValue = new() { UseSystemPasswordChar = true };

    // Printers tab
    private readonly ListBox _printerList = new() { Left = 16, Top = 16, Width = 600, Height = 360 };
    private List<PrinterLaneModel> _printers = new();

    // Devices tab
    private readonly TextBox _station = new();
    private readonly NumericUpDown _devicePollInterval = new();
    private readonly CheckBox _deviceStableOnly = new() { Text = "Only publish stable readings (default)" };
    private readonly ListBox _deviceList = new() { Left = 16, Top = 110, Width = 600, Height = 280 };
    private List<DeviceModel> _devices = new();

    // Apply tab
    private readonly CheckBox _dump = new() { Text = "Enable debug payload dumping (C:\\ProgramData\\CloudPrint\\dumps)" };
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _apply = new() { Text = "Apply — install && start service" };

    public MainForm()
    {
        Text = "CloudPrint Configurator";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(700, 600);

        BuildTransportTab();
        BuildPrintersTab();
        BuildDevicesTab();
        BuildApplyTab();
        Controls.Add(_tabs);

        _rbSqs.CheckedChanged += (_, _) => ApplyTransportVisibility();
        _rbHttp.CheckedChanged += (_, _) => ApplyTransportVisibility();
        _tabs.SelectedIndexChanged += (_, _) => UpdateSummary();

        LoadExisting();
    }

    // ---- Tab construction ----

    private void BuildTransportTab()
    {
        var tab = new TabPage("Transport");
        tab.Controls.Add(_rbSqs);
        tab.Controls.Add(_rbHttp);

        var aws = new GroupBox { Text = "AWS (SQS printing and/or SQS device output)", Left = 16, Top = 48, Width = 620, Height = 190 };
        aws.Controls.Add(new Label { Text = "Region", Left = 16, Top = 30, Width = 90 });
        _region.SetBounds(120, 26, 320, 24);
        _region.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var r in AwsRegions.All)
            _region.Items.Add($"{r.Id} — {r.Name}");
        aws.Controls.Add(_region);

        aws.Controls.Add(new Label { Text = "Access Key ID", Left = 16, Top = 66, Width = 90 });
        _accessKey.SetBounds(120, 62, 320, 24);
        aws.Controls.Add(_accessKey);

        aws.Controls.Add(new Label { Text = "Secret Access Key", Left = 16, Top = 102, Width = 100 });
        _secretKey.SetBounds(120, 98, 320, 24);
        aws.Controls.Add(_secretKey);

        var test = new Button { Text = "Test credentials", Left = 120, Top = 132, Width = 150 };
        test.Click += OnTestCredentials;
        aws.Controls.Add(test);
        tab.Controls.Add(aws);

        tab.Controls.Add(_credResult);

        _httpApiPanel.Controls.Add(new Label { Text = "HTTP API (fetch + acknowledge print jobs)", Left = 0, Top = 0, Width = 400, Font = new Font(Font, FontStyle.Bold) });
        _httpApiPanel.Controls.Add(new Label { Text = "API URL", Left = 0, Top = 30, Width = 90 });
        _apiUrl.SetBounds(110, 26, 500, 24);
        _httpApiPanel.Controls.Add(_apiUrl);
        _httpApiPanel.Controls.Add(new Label { Text = "Ack URL", Left = 0, Top = 62, Width = 90 });
        _ackUrl.SetBounds(110, 58, 500, 24);
        _httpApiPanel.Controls.Add(_ackUrl);
        _httpApiPanel.Controls.Add(new Label { Text = "Header name", Left = 0, Top = 94, Width = 90 });
        _apiHeaderName.SetBounds(110, 90, 200, 24);
        _httpApiPanel.Controls.Add(_apiHeaderName);
        _httpApiPanel.Controls.Add(new Label { Text = "Header value", Left = 0, Top = 126, Width = 90 });
        _apiHeaderValue.SetBounds(110, 122, 500, 24);
        _httpApiPanel.Controls.Add(_apiHeaderValue);
        tab.Controls.Add(_httpApiPanel);

        _tabs.TabPages.Add(tab);
    }

    private void BuildPrintersTab()
    {
        var tab = new TabPage("Printers");
        tab.Controls.Add(_printerList);

        var add = new Button { Text = "Add", Left = 630, Top = 16, Width = 90 };
        var edit = new Button { Text = "Edit", Left = 630, Top = 52, Width = 90 };
        var remove = new Button { Text = "Remove", Left = 630, Top = 88, Width = 90 };
        add.Click += (_, _) => AddOrEditPrinter(null);
        edit.Click += (_, _) => { if (_printerList.SelectedIndex >= 0) AddOrEditPrinter(_printerList.SelectedIndex); };
        remove.Click += (_, _) => RemovePrinter();
        tab.Controls.Add(add);
        tab.Controls.Add(edit);
        tab.Controls.Add(remove);

        _tabs.TabPages.Add(tab);
    }

    private void BuildDevicesTab()
    {
        var tab = new TabPage("Devices");
        tab.Controls.Add(new Label { Text = "Station (blank = machine name)", Left = 16, Top = 18, Width = 200 });
        _station.SetBounds(230, 14, 200, 24);
        tab.Controls.Add(_station);

        tab.Controls.Add(new Label { Text = "Default poll interval (ms)", Left = 16, Top = 50, Width = 200 });
        _devicePollInterval.SetBounds(230, 46, 100, 24);
        _devicePollInterval.Minimum = 0;
        _devicePollInterval.Maximum = 600000;
        _devicePollInterval.Value = ConfigDefaults.DefaultDevicePollIntervalMs;
        tab.Controls.Add(_devicePollInterval);

        _deviceStableOnly.SetBounds(16, 80, 400, 24);
        _deviceStableOnly.Checked = ConfigDefaults.DefaultDeviceStableOnly;
        tab.Controls.Add(_deviceStableOnly);

        tab.Controls.Add(_deviceList);

        var add = new Button { Text = "Add", Left = 630, Top = 110, Width = 90 };
        var edit = new Button { Text = "Edit", Left = 630, Top = 146, Width = 90 };
        var remove = new Button { Text = "Remove", Left = 630, Top = 182, Width = 90 };
        add.Click += (_, _) => AddOrEditDevice(null);
        edit.Click += (_, _) => { if (_deviceList.SelectedIndex >= 0) AddOrEditDevice(_deviceList.SelectedIndex); };
        remove.Click += (_, _) => RemoveDevice();
        tab.Controls.Add(add);
        tab.Controls.Add(edit);
        tab.Controls.Add(remove);

        _tabs.TabPages.Add(tab);
    }

    private void BuildApplyTab()
    {
        var tab = new TabPage("Apply");
        _dump.SetBounds(16, 16, 560, 24);
        tab.Controls.Add(_dump);

        tab.Controls.Add(new Label { Text = "Summary", Left = 16, Top = 48, Width = 200 });
        _summary.SetBounds(16, 72, 700, 150);
        tab.Controls.Add(_summary);

        _apply.SetBounds(16, 232, 260, 32);
        _apply.Click += OnApply;
        tab.Controls.Add(_apply);

        tab.Controls.Add(new Label { Text = "Log", Left = 16, Top = 272, Width = 200 });
        _log.SetBounds(16, 296, 700, 180);
        tab.Controls.Add(_log);

        _tabs.TabPages.Add(tab);
    }

    // ---- Transport ----

    private void ApplyTransportVisibility() => _httpApiPanel.Visible = _rbHttp.Checked;

    private async void OnTestCredentials(object? sender, EventArgs e)
    {
        try
        {
            _credResult.ForeColor = Color.DarkGreen;
            _credResult.Text = "Verifying...";
            var client = new ServiceExeClient(new ProcessRunner(), ServiceExePath);
            var arn = await client.VerifyCredentialsAsync(ReadCredentials());
            _credResult.Text = "Authenticated as: " + arn;
        }
        catch (Exception ex)
        {
            _credResult.ForeColor = Color.DarkRed;
            _credResult.Text = "Failed: " + ex.Message;
        }
    }

    private AwsCredentials ReadCredentials() =>
        new(_accessKey.Text.Trim(), _secretKey.Text, SelectedRegionId());

    private string SelectedRegionId()
    {
        if (_region.SelectedItem is not string s || s.Length == 0)
            return AwsRegions.All[0].Id;
        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }

    private void SelectRegion(string id)
    {
        for (var i = 0; i < _region.Items.Count; i++)
        {
            if (_region.Items[i] is string s && s.StartsWith(id + " ", StringComparison.Ordinal))
            {
                _region.SelectedIndex = i;
                return;
            }
        }

        if (_region.Items.Count > 0)
            _region.SelectedIndex = 0;
    }

    // ---- Printers ----

    private static IEnumerable<string> InstalledPrinters()
    {
        var list = new List<string>();
        foreach (string p in PrinterSettings.InstalledPrinters)
            list.Add(p);
        return list;
    }

    private void AddOrEditPrinter(int? index)
    {
        var existing = index is { } i ? _printers[i] : null;
        using var dlg = new PrinterEditorForm(existing, InstalledPrinters());
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        if (index is { } idx)
            _printers[idx] = dlg.Lane;
        else
            _printers.Add(dlg.Lane);
        RefreshPrinterList();
    }

    private void RemovePrinter()
    {
        if (_printerList.SelectedIndex < 0)
            return;
        _printers.RemoveAt(_printerList.SelectedIndex);
        RefreshPrinterList();
    }

    private void RefreshPrinterList()
    {
        _printerList.Items.Clear();
        foreach (var l in _printers)
            _printerList.Items.Add($"{l.PrinterName}  ({l.PdfRenderDpi ?? ConfigDefaults.DefaultPdfRenderDpi} DPI, {l.PdfFitMode ?? ConfigDefaults.DefaultPdfFitMode})");
    }

    // ---- Devices ----

    private void AddOrEditDevice(int? index)
    {
        var existing = index is { } i ? _devices[i] : null;
        using var dlg = new DeviceEditorForm(existing, ServiceExePath);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        if (index is { } idx)
            _devices[idx] = dlg.Device;
        else
            _devices.Add(dlg.Device);
        RefreshDeviceList();
    }

    private void RemoveDevice()
    {
        if (_deviceList.SelectedIndex < 0)
            return;
        _devices.RemoveAt(_deviceList.SelectedIndex);
        RefreshDeviceList();
    }

    private void RefreshDeviceList()
    {
        _deviceList.Items.Clear();
        foreach (var d in _devices)
            _deviceList.Items.Add($"{d.Name}  [{d.Type}] -> {d.Output?.Transport ?? ConfigDefaults.TransportSqs}");
    }

    // ---- Build config from UI ----

    private bool DevicesNeedAws() => _devices.Any(d => d.Output?.Transport == ConfigDefaults.TransportSqs);

    private bool NeedsAws() => _rbSqs.Checked || DevicesNeedAws();

    private CloudPrintConfig BuildConfigFromUi()
    {
        var config = new CloudPrintConfig
        {
            Transport = _rbHttp.Checked ? ConfigDefaults.TransportHttp : ConfigDefaults.TransportSqs,
            PdfRenderDpi = ConfigDefaults.DefaultPdfRenderDpi,
            PdfFitMode = ConfigDefaults.DefaultPdfFitMode,
            DumpPayloads = _dump.Checked,
            DumpPath = InstallPaths.DumpPathWindows,
            Station = string.IsNullOrWhiteSpace(_station.Text) ? null : _station.Text.Trim(),
            DevicePollIntervalMs = (int)_devicePollInterval.Value,
            DeviceStableOnly = _deviceStableOnly.Checked,
            Devices = _devices,
        };

        if (NeedsAws())
        {
            config.Region = SelectedRegionId();
            config.AwsAccessKeyId = _accessKey.Text.Trim();
            config.AwsSecretAccessKey = _secretKey.Text;
            config.VisibilityTimeoutSeconds = ConfigDefaults.DefaultVisibilityTimeoutSeconds;
        }

        if (_rbSqs.Checked)
        {
            config.Printers = _printers;
        }
        else
        {
            config.ApiUrl = _apiUrl.Text.Trim();
            config.AckUrl = _ackUrl.Text.Trim();
            config.ApiHeaderName = string.IsNullOrWhiteSpace(_apiHeaderName.Text) ? "X-Api-Key" : _apiHeaderName.Text.Trim();
            config.ApiHeaderValue = _apiHeaderValue.Text;
            config.HttpPollTimeoutSeconds = ConfigDefaults.DefaultHttpPollTimeoutSeconds;
            config.PrinterName = _printers.FirstOrDefault()?.PrinterName ?? string.Empty;
        }

        return config;
    }

    private string? Validate()
    {
        if (_printers.Count == 0)
            return "Add at least one printer.";

        if (NeedsAws())
        {
            if (string.IsNullOrWhiteSpace(_accessKey.Text) || string.IsNullOrWhiteSpace(_secretKey.Text))
                return "AWS Access Key ID and Secret Access Key are required (SQS printing or SQS device output is configured).";
        }

        if (_rbHttp.Checked)
        {
            if (string.IsNullOrWhiteSpace(_apiUrl.Text) || string.IsNullOrWhiteSpace(_ackUrl.Text) || string.IsNullOrWhiteSpace(_apiHeaderValue.Text))
                return "HTTP transport requires API URL, Ack URL, and header value.";
        }

        return null;
    }

    private void UpdateSummary()
    {
        if (_tabs.SelectedTab?.Text != "Apply")
            return;

        var transport = _rbHttp.Checked ? "HTTP API" : "SQS";
        var lines = new List<string>
        {
            $"Transport:  {transport}",
            $"Printers:   {_printers.Count}",
            $"Devices:    {_devices.Count}",
            $"AWS needed: {(NeedsAws() ? "yes" : "no")}",
            $"Install to: {AppContext.BaseDirectory}",
        };
        _summary.Text = string.Join(Environment.NewLine, lines);
    }

    // ---- Apply ----

    private async void OnApply(object? sender, EventArgs e)
    {
        var error = Validate();
        if (error is not null)
        {
            MessageBox.Show(this, error, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetBusy(true);
            _log.Clear();
            var config = BuildConfigFromUi();

            if (NeedsAws())
            {
                var creds = ReadCredentials();
                var client = new ServiceExeClient(new ProcessRunner(), ServiceExePath);
                Log("Verifying AWS credentials...");
                Log("Authenticated as " + await client.VerifyCredentialsAsync(creds));
                await ProvisionQueuesAsync(client, creds, config);
            }

            await Task.Run(() =>
            {
                WindowsInstaller.EnsureRuntimeDirectories(config.DumpPayloads, Log);
                WindowsInstaller.WriteAndSecureConfig(ConfigPath, config, Log);
                WindowsInstaller.RegisterAndStartService(ServiceExePath, Log);
            });

            Log("Done — CloudPrint is installed and running.");
            MessageBox.Show(this, "CloudPrint installed and started.", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ProvisionQueuesAsync(ServiceExeClient client, AwsCredentials creds, CloudPrintConfig config)
    {
        var host = Environment.MachineName;

        if (config.Transport == ConfigDefaults.TransportSqs)
        {
            foreach (var lane in config.Printers)
            {
                var queueName = QueueNaming.ForPrinter(host, lane.PrinterName);
                var tags = new Dictionary<string, string>
                {
                    ["Application"] = "cloudprint",
                    ["Hostname"] = host,
                    ["PrinterName"] = lane.PrinterName,
                };
                Log($"Creating printer queue {queueName}...");
                lane.QueueUrl = await client.CreateQueueAsync(creds, queueName, tags);
                Log("  " + lane.QueueUrl);
            }
        }

        var station = string.IsNullOrWhiteSpace(config.Station) ? host : config.Station!;
        foreach (var device in config.Devices.Where(d => d.Output?.Transport == ConfigDefaults.TransportSqs))
        {
            var queueName = QueueNaming.ForDevice(station, device.Name);
            var tags = new Dictionary<string, string>
            {
                ["Application"] = "cloudprint",
                ["Station"] = station,
                ["Device"] = device.Name,
            };
            Log($"Creating device queue {queueName}...");
            device.Output!.QueueUrl = await client.CreateQueueAsync(creds, queueName, tags);
            Log("  " + device.Output.QueueUrl);
        }
    }

    private void SetBusy(bool busy)
    {
        _apply.Enabled = !busy;
        _tabs.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Log(string message)
    {
        if (_log.InvokeRequired)
        {
            _log.BeginInvoke(() => Log(message));
            return;
        }

        _log.AppendText(message + Environment.NewLine);
    }

    // ---- Load existing config ----

    private void LoadExisting()
    {
        var existing = ConfigStore.Load(ConfigPath);
        if (existing is null)
        {
            _rbSqs.Checked = true;
            ApplyTransportVisibility();
            return;
        }

        _rbHttp.Checked = existing.Transport == ConfigDefaults.TransportHttp;
        _rbSqs.Checked = !_rbHttp.Checked;

        SelectRegion(existing.Region ?? AwsRegions.All[0].Id);
        _accessKey.Text = existing.AwsAccessKeyId ?? string.Empty;
        _secretKey.Text = existing.AwsSecretAccessKey ?? string.Empty;

        _apiUrl.Text = existing.ApiUrl ?? string.Empty;
        _ackUrl.Text = existing.AckUrl ?? string.Empty;
        _apiHeaderName.Text = existing.ApiHeaderName ?? "X-Api-Key";
        _apiHeaderValue.Text = existing.ApiHeaderValue ?? string.Empty;

        _station.Text = existing.Station ?? string.Empty;
        _devicePollInterval.Value = existing.DevicePollIntervalMs is >= 0 and <= 600000
            ? existing.DevicePollIntervalMs.Value
            : ConfigDefaults.DefaultDevicePollIntervalMs;
        _deviceStableOnly.Checked = existing.DeviceStableOnly ?? ConfigDefaults.DefaultDeviceStableOnly;
        _dump.Checked = existing.DumpPayloads;

        _printers = existing.Printers.Count > 0
            ? existing.Printers
            : (!string.IsNullOrWhiteSpace(existing.PrinterName)
                ? new List<PrinterLaneModel> { new() { PrinterName = existing.PrinterName! } }
                : new List<PrinterLaneModel>());
        _devices = existing.Devices;

        RefreshPrinterList();
        RefreshDeviceList();
        ApplyTransportVisibility();
    }
}
