using System.Drawing;
using System.Text.Json;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Devices;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>
/// Add/edit a device. On open it scans the machine and offers detected hardware to pick from; a Test
/// button shows a live reading; arcane serial/poll settings hide behind "Show advanced." Sensible
/// defaults everywhere — a mortal types a name, picks the device, and clicks Test.
/// </summary>
internal sealed class DeviceEditorForm : Form
{
    private readonly string _serviceExePath;
    private readonly DevicePreviewRunner _preview;
    private readonly List<Control> _advanced = new();

    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly CheckBox _showAdvanced = new() { Text = "Show advanced settings", AutoSize = true };

    private readonly GroupBox _serialGroup = new() { Text = "Serial connection", Left = 12, Top = 116, Width = 560, Height = 250 };
    private readonly ComboBox _comPort = new();
    private readonly ComboBox _baud = new();
    private readonly ComboBox _parity = new();
    private readonly ComboBox _dataBits = new();
    private readonly ComboBox _stopBits = new();
    private readonly ComboBox _lineEnding = new();
    private readonly ComboBox _encoding = new();
    private readonly ComboBox _protocol = new();
    private readonly TextBox _requestCommand = new();
    private readonly TextBox _initCommands = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };

    private readonly GroupBox _hidGroup = new() { Text = "USB device", Left = 12, Top = 116, Width = 560, Height = 250 };
    private readonly ComboBox _hidPick = new();
    private readonly NumericUpDown _vid = new();
    private readonly NumericUpDown _pid = new();

    private readonly Label _patternLabel = new() { Text = "Pattern (regex)", Left = 12, Top = 376, Width = 110 };
    private readonly TextBox _pattern = new() { Left = 130, Top = 372, Width = 440 };

    private readonly ComboBox _pollMode = new();
    private readonly NumericUpDown _pollInterval = new();
    private readonly CheckBox _stableOnly = new() { Text = "Only publish stable readings", AutoSize = true };

    private readonly GroupBox _outputGroup = new() { Text = "Where readings go", Left = 12, Top = 470, Width = 560, Height = 150 };
    private readonly ComboBox _outTransport = new();
    private readonly Label _outSqsHint = new() { Text = "Sent to the cloud (an SQS queue is created automatically).", Left = 16, Top = 56, Width = 520, ForeColor = Color.DimGray };
    private readonly TextBox _webhookUrl = new();
    private readonly TextBox _headerName = new();
    private readonly TextBox _headerValue = new();

    private readonly Button _test = new() { Text = "Test (live reading)", Left = 12, Top = 632, Width = 160 };
    private readonly Label _reading = new() { Left = 182, Top = 638, Width = 400, AutoSize = false, ForeColor = Color.DimGray };
    private bool _previewing;

    public DeviceModel Device { get; private set; }

    public DeviceEditorForm(DeviceModel? existing, string serviceExePath)
    {
        _serviceExePath = serviceExePath;
        _preview = new DevicePreviewRunner(serviceExePath);
        Device = existing ?? new DeviceModel { Type = ConfigDefaults.DeviceSerialScale };

        Text = existing is null ? "Add device" : "Edit device";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 720);
        AutoScroll = true;

        BuildTop();
        BuildSerialGroup();
        BuildHidGroup();
        BuildBehaviour();
        BuildOutputGroup();
        BuildTestAndButtons();

        LoadFrom(Device);
        ApplyTypeVisibility();
        ApplyAdvancedVisibility();
        ApplyOutputVisibility();

        Load += async (_, _) => await DetectAsync();
        FormClosing += (_, _) => _preview.Dispose();
    }

    // ---- Layout ----

    private void BuildTop()
    {
        Controls.Add(new Label { Text = "Name", Left = 12, Top = 18, Width = 100 });
        _name.SetBounds(130, 14, 240, 24);
        Controls.Add(_name);

        Controls.Add(new Label { Text = "Type", Left = 12, Top = 50, Width = 100 });
        _type.SetBounds(130, 46, 240, 24);
        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var t in ConfigDefaults.DeviceTypes)
            _type.Items.Add(t);
        _type.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();
        Controls.Add(_type);

        var rescan = new Button { Text = "Rescan", Left = 390, Top = 45, Width = 90 };
        rescan.Click += async (_, _) => await DetectAsync();
        Controls.Add(rescan);

        _showAdvanced.Location = new Point(12, 84);
        _showAdvanced.CheckedChanged += (_, _) => ApplyAdvancedVisibility();
        Controls.Add(_showAdvanced);
    }

    private void BuildSerialGroup()
    {
        AddLabel(_serialGroup, "COM port", 26);
        _comPort.SetBounds(150, 22, 160, 24);
        _comPort.DropDownStyle = ComboBoxStyle.DropDown;
        _serialGroup.Controls.Add(_comPort);

        AddLabel(_serialGroup, "Baud rate", 56);
        _baud.SetBounds(150, 52, 160, 24);
        FillSerial(_baud, ConfigDefaults.BaudRates);

        AdvancedRow(_serialGroup, "Parity", 86, _parity, ConfigDefaults.Parities);
        AdvancedRow(_serialGroup, "Data bits", 116, _dataBits, ConfigDefaults.DataBitsOptions);
        AdvancedRow(_serialGroup, "Stop bits", 146, _stopBits, ConfigDefaults.StopBitsOptions);
        AdvancedRow(_serialGroup, "Line ending", 176, _lineEnding, ConfigDefaults.LineEndings);
        AdvancedRow(_serialGroup, "Encoding", 206, _encoding, ConfigDefaults.Encodings);

        var protoLabel = AddLabel(_serialGroup, "Protocol", 26, 330);
        _protocol.SetBounds(420, 22, 120, 24);
        _protocol.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var p in ConfigDefaults.Protocols)
            _protocol.Items.Add(p);
        _serialGroup.Controls.Add(_protocol);
        MarkAdvanced(protoLabel, _protocol);

        var reqLabel = AddLabel(_serialGroup, "Request cmd", 56, 330);
        _requestCommand.SetBounds(420, 52, 120, 24);
        _serialGroup.Controls.Add(_requestCommand);
        MarkAdvanced(reqLabel, _requestCommand);

        var initLabel = AddLabel(_serialGroup, "Init commands", 86, 330);
        _initCommands.SetBounds(420, 82, 120, 140);
        _serialGroup.Controls.Add(_initCommands);
        MarkAdvanced(initLabel, _initCommands);

        Controls.Add(_serialGroup);
    }

    private void BuildHidGroup()
    {
        AddLabel(_hidGroup, "Detected", 26);
        _hidPick.SetBounds(150, 22, 390, 24);
        _hidPick.DropDownStyle = ComboBoxStyle.DropDownList;
        _hidPick.SelectedIndexChanged += OnHidPicked;
        _hidGroup.Controls.Add(_hidPick);

        AddLabel(_hidGroup, "Vendor ID", 56);
        _vid.SetBounds(150, 52, 120, 24);
        _vid.Minimum = 0;
        _vid.Maximum = 65535;
        _hidGroup.Controls.Add(_vid);

        AddLabel(_hidGroup, "Product ID", 86);
        _pid.SetBounds(150, 82, 120, 24);
        _pid.Minimum = 0;
        _pid.Maximum = 65535;
        _hidGroup.Controls.Add(_pid);

        _hidGroup.Controls.Add(new Label
        {
            Text = "Pick your device above — we fill in the IDs. (VID/PID are decimal.)",
            Left = 16,
            Top = 120,
            Width = 520,
            ForeColor = Color.DimGray,
        });

        Controls.Add(_hidGroup);
    }

    private void BuildBehaviour()
    {
        Controls.Add(_patternLabel);
        Controls.Add(_pattern);

        var pollLabel = new Label { Text = "Poll mode", Left = 12, Top = 410, Width = 100 };
        Controls.Add(pollLabel);
        _pollMode.SetBounds(130, 406, 150, 24);
        _pollMode.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var m in ConfigDefaults.PollModes)
            _pollMode.Items.Add(m);
        Controls.Add(_pollMode);
        MarkAdvanced(pollLabel, _pollMode);

        var intervalLabel = new Label { Text = "Poll interval (ms)", Left = 300, Top = 410, Width = 110 };
        Controls.Add(intervalLabel);
        _pollInterval.SetBounds(420, 406, 100, 24);
        _pollInterval.Minimum = 0;
        _pollInterval.Maximum = 600000;
        Controls.Add(_pollInterval);
        MarkAdvanced(intervalLabel, _pollInterval);

        _stableOnly.Location = new Point(130, 438);
        Controls.Add(_stableOnly);
        MarkAdvanced(_stableOnly);
    }

    private void BuildOutputGroup()
    {
        AddLabel(_outputGroup, "Send to", 26);
        _outTransport.SetBounds(150, 22, 150, 24);
        _outTransport.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var t in ConfigDefaults.Transports)
            _outTransport.Items.Add(t);
        _outTransport.SelectedIndexChanged += (_, _) => ApplyOutputVisibility();
        _outputGroup.Controls.Add(_outTransport);

        _outputGroup.Controls.Add(_outSqsHint);

        AddLabel(_outputGroup, "Webhook URL", 56);
        _webhookUrl.SetBounds(150, 52, 390, 24);
        _outputGroup.Controls.Add(_webhookUrl);

        AddLabel(_outputGroup, "Header name", 86);
        _headerName.SetBounds(150, 82, 200, 24);
        _outputGroup.Controls.Add(_headerName);

        AddLabel(_outputGroup, "Header value", 116);
        _headerValue.SetBounds(150, 112, 390, 24);
        _outputGroup.Controls.Add(_headerValue);

        Controls.Add(_outputGroup);
    }

    private void BuildTestAndButtons()
    {
        _test.Click += OnTest;
        Controls.Add(_test);
        Controls.Add(_reading);

        var ok = new Button { Text = "OK", Left = 410, Top = 678, Width = 80 };
        var cancel = new Button { Text = "Cancel", Left = 500, Top = 678, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private Label AddLabel(Control parent, string text, int top, int left = 16)
    {
        var label = new Label { Text = text, Left = left, Top = top + 4, Width = 130 };
        parent.Controls.Add(label);
        return label;
    }

    private void FillSerial(ComboBox combo, IEnumerable<int> items)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in items)
            combo.Items.Add(i);
        _serialGroup.Controls.Add(combo);
    }

    private void AdvancedRow(GroupBox group, string text, int top, ComboBox combo, IEnumerable<string> items)
    {
        var label = AddLabel(group, text, top);
        combo.SetBounds(150, top - 4, 160, 24);
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in items)
            combo.Items.Add(i);
        group.Controls.Add(combo);
        MarkAdvanced(label, combo);
    }

    private void AdvancedRow(GroupBox group, string text, int top, ComboBox combo, IEnumerable<int> items)
    {
        var label = AddLabel(group, text, top);
        combo.SetBounds(150, top - 4, 160, 24);
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in items)
            combo.Items.Add(i);
        group.Controls.Add(combo);
        MarkAdvanced(label, combo);
    }

    private void MarkAdvanced(params Control[] controls) => _advanced.AddRange(controls);

    // ---- State ----

    private void LoadFrom(DeviceModel d)
    {
        _name.Text = d.Name;
        _type.SelectedItem = ConfigDefaults.DeviceTypes.Contains(d.Type) ? d.Type : ConfigDefaults.DeviceSerialScale;

        _comPort.Text = d.ComPort ?? string.Empty;
        SelectOrDefault(_baud, d.BaudRate ?? ConfigDefaults.DefaultBaudRate);
        SelectOrDefault(_parity, d.Parity ?? ConfigDefaults.DefaultParity);
        SelectOrDefault(_dataBits, d.DataBits ?? ConfigDefaults.DefaultDataBits);
        SelectOrDefault(_stopBits, d.StopBits ?? ConfigDefaults.DefaultStopBits);
        SelectOrDefault(_lineEnding, d.LineEnding ?? ConfigDefaults.DefaultLineEnding);
        SelectOrDefault(_encoding, d.Encoding ?? ConfigDefaults.DefaultEncoding);
        SelectOrDefault(_protocol, d.Protocol ?? ConfigDefaults.DefaultProtocol);
        _requestCommand.Text = d.RequestCommand ?? string.Empty;
        _initCommands.Text = d.InitCommands is null ? string.Empty : string.Join(Environment.NewLine, d.InitCommands);

        _vid.Value = Clamp(d.Vid, _vid);
        _pid.Value = Clamp(d.Pid, _pid);

        _pattern.Text = d.Pattern ?? string.Empty;

        SelectOrDefault(_pollMode, d.PollMode ?? ConfigDefaults.DefaultPollMode);
        _pollInterval.Value = d.PollIntervalMs is >= 0 and <= 600000 ? d.PollIntervalMs.Value : ConfigDefaults.DefaultDevicePollIntervalMs;
        _stableOnly.Checked = d.StableOnly ?? ConfigDefaults.DefaultDeviceStableOnly;

        var output = d.Output ?? new DeviceOutputModel();
        SelectOrDefault(_outTransport, output.Transport);
        _webhookUrl.Text = output.WebhookUrl ?? string.Empty;
        _headerName.Text = output.HeaderName ?? "X-Api-Key";
        _headerValue.Text = output.HeaderValue ?? string.Empty;
    }

    private static decimal Clamp(int? value, NumericUpDown box) =>
        value is null ? box.Minimum : Math.Clamp(value.Value, (int)box.Minimum, (int)box.Maximum);

    private static void SelectOrDefault(ComboBox combo, object value)
    {
        if (combo.Items.Contains(value))
            combo.SelectedItem = value;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private string SelectedType() => (string?)_type.SelectedItem ?? ConfigDefaults.DeviceSerialScale;

    private void ApplyTypeVisibility()
    {
        var type = SelectedType();
        _serialGroup.Visible = ConfigDefaults.IsSerial(type);
        _hidGroup.Visible = ConfigDefaults.IsHid(type);

        var showPattern = type == ConfigDefaults.DeviceSerialRaw;
        _patternLabel.Visible = showPattern;
        _pattern.Visible = showPattern;
    }

    private void ApplyAdvancedVisibility()
    {
        foreach (var c in _advanced)
            c.Visible = _showAdvanced.Checked;
    }

    private void ApplyOutputVisibility()
    {
        var sqs = (string?)_outTransport.SelectedItem == ConfigDefaults.TransportSqs;
        _outSqsHint.Visible = sqs;
        _webhookUrl.Enabled = !sqs;
        _headerName.Enabled = !sqs;
        _headerValue.Enabled = !sqs;
    }

    // ---- Detection ----

    private async Task DetectAsync()
    {
        try
        {
            var client = new ServiceExeClient(new ProcessRunner(), _serviceExePath);
            var inventory = await client.ListDevicesAsync();

            var keepPort = _comPort.Text;
            _comPort.Items.Clear();
            foreach (var port in inventory.SerialPorts)
                _comPort.Items.Add(port);
            _comPort.Text = keepPort;

            _hidPick.Items.Clear();
            foreach (var hid in inventory.HidDevices)
                _hidPick.Items.Add(new HidChoice(hid));
            if (_hidPick.Items.Count == 0)
                _hidPick.Items.Add(new HidChoice(null));
        }
        catch (Exception ex)
        {
            // Detection is best-effort; the user can still type values.
            _reading.ForeColor = Color.DimGray;
            _reading.Text = "Could not scan hardware: " + ex.Message;
        }
    }

    private void OnHidPicked(object? sender, EventArgs e)
    {
        if (_hidPick.SelectedItem is HidChoice { Info: { } info })
        {
            _vid.Value = Math.Clamp(info.Vid, 0, 65535);
            _pid.Value = Math.Clamp(info.Pid, 0, 65535);
        }
    }

    // ---- Live preview ----

    private void OnTest(object? sender, EventArgs e)
    {
        if (_previewing)
        {
            _preview.Stop();
            _previewing = false;
            _test.Text = "Test (live reading)";
            return;
        }

        var device = BuildDevice(forPreview: true, out var error);
        if (device is null)
        {
            _reading.ForeColor = Color.Firebrick;
            _reading.Text = error;
            return;
        }

        _reading.ForeColor = Color.DimGray;
        _reading.Text = "connecting…";
        _previewing = true;
        _test.Text = "Stop";

        var json = JsonSerializer.Serialize(device);
        _preview.Start(json, OnReading, OnPreviewEnded);
    }

    private void OnReading(DeviceReadingPreview reading)
    {
        if (IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                _reading.ForeColor = Color.Green;
                _reading.Text = "reading now: " + reading.Describe();
            });
        }
        catch (InvalidOperationException) { /* handle gone */ }
    }

    private void OnPreviewEnded(string? error)
    {
        if (IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                _previewing = false;
                _test.Text = "Test (live reading)";
                if (!string.IsNullOrEmpty(error))
                {
                    _reading.ForeColor = Color.Firebrick;
                    _reading.Text = "✗ " + error;
                }
                else if (_reading.Text == "connecting…")
                {
                    _reading.ForeColor = Color.DimGray;
                    _reading.Text = "(no reading — check the device and settings)";
                }
            });
        }
        catch (InvalidOperationException) { /* handle gone */ }
    }

    // ---- Build / OK ----

    /// <summary>Builds a DeviceModel from the fields. When forPreview, output isn't required and the name defaults.</summary>
    private DeviceModel? BuildDevice(bool forPreview, out string? error)
    {
        error = null;
        var name = _name.Text.Trim();
        if (!forPreview && string.IsNullOrWhiteSpace(name))
        {
            error = "Device name is required.";
            return null;
        }

        var type = SelectedType();
        var device = new DeviceModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? "preview" : name,
            Type = type,
            PollMode = (string)_pollMode.SelectedItem!,
            PollIntervalMs = (int)_pollInterval.Value,
            StableOnly = _stableOnly.Checked,
        };

        if (ConfigDefaults.IsSerial(type))
        {
            if (string.IsNullOrWhiteSpace(_comPort.Text))
            {
                error = "Choose a COM port.";
                return null;
            }
            device.ComPort = _comPort.Text.Trim();
            device.BaudRate = (int)_baud.SelectedItem!;
            device.Parity = (string)_parity.SelectedItem!;
            device.DataBits = (int)_dataBits.SelectedItem!;
            device.StopBits = (int)_stopBits.SelectedItem!;
            device.LineEnding = (string)_lineEnding.SelectedItem!;
            device.Encoding = (string)_encoding.SelectedItem!;
            device.Protocol = (string)_protocol.SelectedItem!;
            device.RequestCommand = string.IsNullOrWhiteSpace(_requestCommand.Text) ? null : _requestCommand.Text.Trim();
            var init = _initCommands.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            device.InitCommands = init.Count > 0 ? init : null;
            if (type == ConfigDefaults.DeviceSerialRaw && !string.IsNullOrWhiteSpace(_pattern.Text))
                device.Pattern = _pattern.Text.Trim();
        }
        else
        {
            device.Vid = (int)_vid.Value;
            device.Pid = (int)_pid.Value;
        }

        if (!forPreview)
        {
            var transport = (string)_outTransport.SelectedItem!;
            var output = new DeviceOutputModel { Transport = transport };
            if (transport == ConfigDefaults.TransportHttp)
            {
                if (string.IsNullOrWhiteSpace(_webhookUrl.Text))
                {
                    error = "Webhook URL is required when sending to your own server.";
                    return null;
                }
                output.WebhookUrl = _webhookUrl.Text.Trim();
                output.HeaderName = string.IsNullOrWhiteSpace(_headerName.Text) ? null : _headerName.Text.Trim();
                output.HeaderValue = string.IsNullOrWhiteSpace(_headerValue.Text) ? null : _headerValue.Text;
            }
            device.Output = output;
        }

        return device;
    }

    private void OnOk()
    {
        var device = BuildDevice(forPreview: false, out var error);
        if (device is null)
        {
            MessageBox.Show(this, error, "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _preview.Stop();
        Device = device;
        DialogResult = DialogResult.OK;
    }

    /// <summary>ComboBox item wrapper for a discovered HID device.</summary>
    private sealed class HidChoice
    {
        public HidChoice(HidDeviceInfo? info) => Info = info;

        public HidDeviceInfo? Info { get; }

        public override string ToString()
        {
            var info = Info;
            return info is null
                ? "(no USB devices found)"
                : $"{info.Product}  (VID={info.Vid:X4} PID={info.Pid:X4})";
        }
    }
}
