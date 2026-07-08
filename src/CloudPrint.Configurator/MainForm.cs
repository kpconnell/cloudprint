using System.Drawing;
using System.Drawing.Printing;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>
/// Single "Station Setup" hub: connection + Test at top, then a Printers list and a Devices list you grow
/// with Add (each Add is the loop), then one Install &amp; Start action with live status. All non-UI logic
/// lives in the tested CloudPrint.Configurator.Core; this is the thin Windows-only shell.
/// </summary>
internal sealed class MainForm : Form
{
    private static string ServiceExePath => InstallContext.ServiceExePath;
    private static string ConfigPath => InstallContext.ConfigPath;

    // Connection
    private readonly RadioButton _rbSqs = new() { Text = "Amazon SQS (use the keys you were given)", AutoSize = true };
    private readonly RadioButton _rbHttp = new() { Text = "Our own server (HTTP API)", AutoSize = true };
    private readonly ComboBox _region = new();
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true };
    private readonly Label _credResult = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Panel _httpPanel = new();
    private readonly TextBox _apiUrl = new();
    private readonly TextBox _ackUrl = new();
    private readonly TextBox _apiHeaderName = new();
    private readonly TextBox _apiHeaderValue = new() { UseSystemPasswordChar = true };

    // Printers
    private readonly ListBox _printerList = new();
    private List<PrinterLaneModel> _printers = new();

    // Devices
    private readonly TextBox _station = new();
    private readonly ListBox _deviceList = new();
    private List<DeviceModel> _devices = new();

    // Install
    private readonly CheckBox _dump = new() { Text = "Save debug copies of each job (C:\\ProgramData\\CloudPrint\\dumps)", AutoSize = true };
    private readonly Button _install = new() { Text = "Install && Start", Width = 180, Height = 36 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public MainForm()
    {
        Text = $"CloudPrint — Station Setup — v{AppVersion.Display}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 860);
        MinimumSize = new Size(740, 700);
        AutoScroll = true;

        var y = 12;
        BuildConnection(ref y);
        BuildPrinters(ref y);
        BuildDevices(ref y);
        BuildInstall(ref y);

        _rbSqs.CheckedChanged += (_, _) => ApplyTransportVisibility();
        _rbHttp.CheckedChanged += (_, _) => ApplyTransportVisibility();

        LoadExisting();
    }

    // ---- Layout ----

    private void BuildConnection(ref int y)
    {
        var group = new GroupBox { Text = "Connection", Left = 12, Top = y, Width = 690, Height = 320 };

        _rbSqs.Location = new Point(16, 26);
        _rbHttp.Location = new Point(360, 26);
        group.Controls.Add(_rbSqs);
        group.Controls.Add(_rbHttp);

        group.Controls.Add(new Label { Text = "AWS keys (for SQS printing and SQS devices):", Left = 16, Top = 56, AutoSize = true, ForeColor = Color.DimGray });

        group.Controls.Add(new Label { Text = "Region", Left = 16, Top = 88, Width = 110 });
        _region.SetBounds(130, 84, 320, 24);
        _region.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var r in AwsRegions.All)
            _region.Items.Add($"{r.Id} — {r.Name}");
        SelectRegion(AwsRegions.DefaultId);
        group.Controls.Add(_region);

        group.Controls.Add(new Label { Text = "Access Key ID", Left = 16, Top = 120, Width = 110 });
        _accessKey.SetBounds(130, 116, 320, 24);
        group.Controls.Add(_accessKey);

        group.Controls.Add(new Label { Text = "Secret Access Key", Left = 16, Top = 152, Width = 110 });
        _secretKey.SetBounds(130, 148, 320, 24);
        group.Controls.Add(_secretKey);

        var test = new Button { Text = "Test", Left = 470, Top = 147, Width = 90 };
        test.Click += OnTestCredentials;
        group.Controls.Add(test);
        _credResult.Location = new Point(130, 182);
        group.Controls.Add(_credResult);

        // HTTP fields (shown only for the HTTP transport)
        _httpPanel.SetBounds(12, 206, 666, 104);
        _httpPanel.Controls.Add(new Label { Text = "API URL", Left = 4, Top = 4, Width = 110 });
        _apiUrl.SetBounds(130, 0, 520, 24);
        _httpPanel.Controls.Add(_apiUrl);
        _httpPanel.Controls.Add(new Label { Text = "Ack URL", Left = 4, Top = 32, Width = 110 });
        _ackUrl.SetBounds(130, 28, 520, 24);
        _httpPanel.Controls.Add(_ackUrl);
        _httpPanel.Controls.Add(new Label { Text = "Header name", Left = 4, Top = 60, Width = 110 });
        _apiHeaderName.SetBounds(130, 56, 200, 24);
        _httpPanel.Controls.Add(_apiHeaderName);
        _httpPanel.Controls.Add(new Label { Text = "Header value", Left = 4, Top = 88, Width = 110 });
        _apiHeaderValue.SetBounds(130, 84, 520, 24);
        _httpPanel.Controls.Add(_apiHeaderValue);
        group.Controls.Add(_httpPanel);

        Controls.Add(group);
        y += group.Height + 10;
    }

    private void BuildPrinters(ref int y)
    {
        var group = new GroupBox { Text = "Printers", Left = 12, Top = y, Width = 690, Height = 180 };

        _printerList.SetBounds(16, 26, 540, 140);
        group.Controls.Add(_printerList);

        AddListButtons(group, 568, _printerList, "+ Add printer",
            () => AddOrEditPrinter(null),
            OnTestPrinter,
            () => { if (_printerList.SelectedIndex >= 0) AddOrEditPrinter(_printerList.SelectedIndex); },
            RemovePrinter);

        Controls.Add(group);
        y += group.Height + 10;
    }

    private void BuildDevices(ref int y)
    {
        var group = new GroupBox { Text = "Scales & devices (optional)", Left = 12, Top = y, Width = 690, Height = 210 };

        group.Controls.Add(new Label { Text = "Station name", Left = 16, Top = 28, Width = 90 });
        _station.SetBounds(110, 24, 220, 24);
        group.Controls.Add(_station);
        group.Controls.Add(new Label { Text = "(optional — defaults to this PC's name)", Left = 340, Top = 28, AutoSize = true, ForeColor = Color.DimGray });

        _deviceList.SetBounds(16, 60, 540, 140);
        group.Controls.Add(_deviceList);

        AddListButtons(group, 568, _deviceList, "+ Add device",
            () => AddOrEditDevice(null),
            OnTestDevice,
            () => { if (_deviceList.SelectedIndex >= 0) AddOrEditDevice(_deviceList.SelectedIndex); },
            RemoveDevice, topOffset: 34);

        Controls.Add(group);
        y += group.Height + 10;
    }

    private void BuildInstall(ref int y)
    {
        _dump.Location = new Point(16, y);
        Controls.Add(_dump);
        y += 30;

        _install.Location = new Point(16, y);
        _install.Click += OnInstall;
        Controls.Add(_install);

        _status.Location = new Point(210, y + 10);
        Controls.Add(_status);
        y += 46;

        _log.SetBounds(16, y, 686, 120);
        Controls.Add(_log);
        y += 130;

        Controls.Add(new Label
        {
            Text = $"CloudPrint v{AppVersion.Display}",
            Left = 16,
            Top = y,
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        y += 24;
    }

    private static void AddListButtons(
        Control parent, int left, ListBox list, string addText,
        Action add, Action test, Action edit, Action remove, int topOffset = 26)
    {
        var addBtn = new Button { Text = addText, Left = left, Top = topOffset, Width = 110 };
        var testBtn = new Button { Text = "Test", Left = left, Top = topOffset + 36, Width = 110 };
        var editBtn = new Button { Text = "Edit", Left = left, Top = topOffset + 72, Width = 110 };
        var removeBtn = new Button { Text = "Remove", Left = left, Top = topOffset + 108, Width = 110 };

        addBtn.Click += (_, _) => add();
        testBtn.Click += (_, _) => test();
        editBtn.Click += (_, _) => edit();
        removeBtn.Click += (_, _) => remove();

        // Test/Edit/Remove act on the selected row — disable them until something is selected.
        void Sync() => testBtn.Enabled = editBtn.Enabled = removeBtn.Enabled = list.SelectedIndex >= 0;
        list.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        parent.Controls.Add(addBtn);
        parent.Controls.Add(testBtn);
        parent.Controls.Add(editBtn);
        parent.Controls.Add(removeBtn);
    }

    private void ApplyTransportVisibility() => _httpPanel.Visible = _rbHttp.Checked;

    // ---- Connection ----

    private async void OnTestCredentials(object? sender, EventArgs e)
    {
        try
        {
            _credResult.ForeColor = Color.DimGray;
            _credResult.Text = "Verifying…";
            var client = new ServiceExeClient(new ProcessRunner(), ServiceExePath);
            var arn = await client.VerifyCredentialsAsync(ReadCredentials());
            _credResult.ForeColor = Color.Green;
            _credResult.Text = "✓ " + arn;
        }
        catch (Exception ex)
        {
            _credResult.ForeColor = Color.Firebrick;
            _credResult.Text = "✗ " + ex.Message;
        }
    }

    private AwsCredentials ReadCredentials() =>
        new(_accessKey.Text.Trim(), _secretKey.Text, SelectedRegionId());

    private string SelectedRegionId()
    {
        if (_region.SelectedItem is not string s || s.Length == 0)
            return AwsRegions.DefaultId;
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
        _printerList.SelectedIndex = index ?? _printers.Count - 1;
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
            _printerList.Items.Add($"{l.PrinterName}   ({l.PdfRenderDpi ?? ConfigDefaults.DefaultPdfRenderDpi} DPI, {l.PdfFitMode ?? ConfigDefaults.DefaultPdfFitMode}{(string.IsNullOrEmpty(l.PdfPaperSize) ? "" : $", {l.PdfPaperSize}")}{(l.PdfMonochrome == true ? ", B/W" : "")})");
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
        _deviceList.SelectedIndex = index ?? _devices.Count - 1;
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
            _deviceList.Items.Add($"{d.Name}   [{d.Type}] → {d.Output?.Transport ?? ConfigDefaults.TransportSqs}");
    }

    // ---- Test buttons ----

    private async void OnTestPrinter()
    {
        if (_printerList.SelectedIndex < 0)
        {
            MessageBox.Show(this, "Select a printer to test.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var printer = _printers[_printerList.SelectedIndex].PrinterName;
        try
        {
            SetBusy(true);
            await new ServiceExeClient(new ProcessRunner(), ServiceExePath).TestPrintAsync(printer);
            MessageBox.Show(this, $"Sent a test label to {printer}.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnTestDevice()
    {
        if (_deviceList.SelectedIndex < 0)
        {
            MessageBox.Show(this, "Select a device to test.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var device = _devices[_deviceList.SelectedIndex];
        var output = device.Output ?? new DeviceOutputModel();
        var station = string.IsNullOrWhiteSpace(_station.Text) ? Environment.MachineName : _station.Text.Trim();
        var sqs = output.Transport == ConfigDefaults.TransportSqs;

        if (sqs && (string.IsNullOrWhiteSpace(_accessKey.Text) || string.IsNullOrWhiteSpace(_secretKey.Text)))
        {
            MessageBox.Show(this, "Enter your AWS keys first — this device sends to SQS.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!sqs && string.IsNullOrWhiteSpace(output.WebhookUrl))
        {
            MessageBox.Show(this, "This device has no webhook URL set.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetBusy(true);
            var client = new ServiceExeClient(new ProcessRunner(), ServiceExePath);
            OutputTestRequest request;

            if (sqs)
            {
                var creds = ReadCredentials();
                var queueName = QueueNaming.ForDevice(station, device.Name);
                var url = await client.CreateQueueAsync(creds, queueName, new Dictionary<string, string>
                {
                    ["Application"] = "cloudprint",
                    ["Station"] = station,
                    ["Device"] = device.Name,
                });
                request = new OutputTestRequest
                {
                    Transport = "sqs",
                    QueueUrl = url,
                    AccessKey = creds.AccessKey,
                    SecretKey = creds.SecretKey,
                    Region = creds.Region,
                    Station = station,
                    DeviceName = device.Name,
                    Message = "Hello from CloudPrint",
                };
            }
            else
            {
                request = new OutputTestRequest
                {
                    Transport = "http",
                    WebhookUrl = output.WebhookUrl,
                    HeaderName = output.HeaderName,
                    HeaderValue = output.HeaderValue,
                    Station = station,
                    DeviceName = device.Name,
                    Message = "Hello from CloudPrint",
                };
            }

            await client.TestOutputAsync(request);
            MessageBox.Show(this, $"Sent a 'Hello' test message to {device.Name}'s output.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---- Config assembly ----

    private bool DevicesNeedAws() => _devices.Any(d => d.Output?.Transport == ConfigDefaults.TransportSqs);

    private bool NeedsAws() => _rbSqs.Checked || DevicesNeedAws();

    private CloudPrintConfig BuildConfigFromUi()
    {
        var config = new CloudPrintConfig
        {
            Transport = _rbHttp.Checked ? ConfigDefaults.TransportHttp : ConfigDefaults.TransportSqs,
            PdfRenderDpi = ConfigDefaults.DefaultPdfRenderDpi,
            PdfFitMode = ConfigDefaults.DefaultPdfFitMode,
            PdfMonochrome = ConfigDefaults.DefaultPdfMonochrome,
            DumpPayloads = _dump.Checked,
            DumpPath = InstallPaths.DumpPathWindows,
            Station = string.IsNullOrWhiteSpace(_station.Text) ? null : _station.Text.Trim(),
            DevicePollIntervalMs = ConfigDefaults.DefaultDevicePollIntervalMs,
            DeviceStableOnly = ConfigDefaults.DefaultDeviceStableOnly,
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

    private string? ValidateInput()
    {
        if (_printers.Count == 0)
            return "Add at least one printer.";

        if (NeedsAws() && (string.IsNullOrWhiteSpace(_accessKey.Text) || string.IsNullOrWhiteSpace(_secretKey.Text)))
            return "Enter your AWS Access Key ID and Secret Access Key (needed for SQS printing or SQS devices).";

        if (_rbHttp.Checked &&
            (string.IsNullOrWhiteSpace(_apiUrl.Text) || string.IsNullOrWhiteSpace(_ackUrl.Text) || string.IsNullOrWhiteSpace(_apiHeaderValue.Text)))
            return "For 'Our own server', enter the API URL, Ack URL, and header value.";

        return null;
    }

    // ---- Install ----

    private async void OnInstall(object? sender, EventArgs e)
    {
        var error = ValidateInput();
        if (error is not null)
        {
            MessageBox.Show(this, error, "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetBusy(true);
            _log.Clear();
            SetStatus("Installing…", Color.DimGray);
            var config = BuildConfigFromUi();

            if (NeedsAws())
            {
                var client = new ServiceExeClient(new ProcessRunner(), ServiceExePath);
                Log("Verifying AWS credentials…");
                Log("Authenticated as " + await client.VerifyCredentialsAsync(ReadCredentials()));
                await ProvisionQueuesAsync(client, ReadCredentials(), config);
            }

            await Task.Run(() =>
            {
                WindowsInstaller.EnsureRuntimeDirectories(config.DumpPayloads, Log);
                WindowsInstaller.WriteAndSecureConfig(ConfigPath, config, Log);
                WindowsInstaller.RegisterAndStartService(ServiceExePath, Log);
            });

            Log("Done.");
            SetStatus("● CloudPrint is running", Color.Green);
            MessageBox.Show(this, "CloudPrint is installed and running.", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            SetStatus("● Install failed", Color.Firebrick);
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
                Log($"Creating printer queue {queueName}…");
                lane.QueueUrl = await client.CreateQueueAsync(creds, queueName, new Dictionary<string, string>
                {
                    ["Application"] = "cloudprint",
                    ["Hostname"] = host,
                    ["PrinterName"] = lane.PrinterName,
                });
                Log("  " + lane.QueueUrl);
            }
        }

        var station = string.IsNullOrWhiteSpace(config.Station) ? host : config.Station!;
        foreach (var device in config.Devices.Where(d => d.Output?.Transport == ConfigDefaults.TransportSqs))
        {
            var queueName = QueueNaming.ForDevice(station, device.Name);
            Log($"Creating device queue {queueName}…");
            device.Output!.QueueUrl = await client.CreateQueueAsync(creds, queueName, new Dictionary<string, string>
            {
                ["Application"] = "cloudprint",
                ["Station"] = station,
                ["Device"] = device.Name,
            });
            Log("  " + device.Output.QueueUrl);
        }
    }

    private void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, Color color)
    {
        _status.ForeColor = color;
        _status.Text = text;
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

        SelectRegion(existing.Region ?? AwsRegions.DefaultId);
        _accessKey.Text = existing.AwsAccessKeyId ?? string.Empty;
        _secretKey.Text = existing.AwsSecretAccessKey ?? string.Empty;

        _apiUrl.Text = existing.ApiUrl ?? string.Empty;
        _ackUrl.Text = existing.AckUrl ?? string.Empty;
        _apiHeaderName.Text = existing.ApiHeaderName ?? "X-Api-Key";
        _apiHeaderValue.Text = existing.ApiHeaderValue ?? string.Empty;

        _station.Text = existing.Station ?? string.Empty;
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
