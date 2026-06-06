using System.Drawing;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>Modal dialog to add or edit one outbound telemetry device. Fields adapt to the device type.</summary>
internal sealed class DeviceEditorForm : Form
{
    private readonly string _serviceExePath;

    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();

    private readonly GroupBox _serialGroup = new() { Text = "Serial", Left = 12, Top = 96, Width = 540, Height = 250 };
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

    private readonly GroupBox _hidGroup = new() { Text = "HID", Left = 12, Top = 96, Width = 540, Height = 250 };
    private readonly ComboBox _hidPick = new();
    private readonly NumericUpDown _vid = new();
    private readonly NumericUpDown _pid = new();

    private readonly Label _patternLabel = new() { Text = "Pattern (regex)", Left = 12, Top = 356, Width = 110 };
    private readonly TextBox _pattern = new() { Left = 130, Top = 352, Width = 420 };

    private readonly ComboBox _pollMode = new();
    private readonly NumericUpDown _pollInterval = new();
    private readonly CheckBox _stableOnly = new() { Text = "Only publish stable readings" };

    private readonly GroupBox _outputGroup = new() { Text = "Output", Left = 12, Top = 470, Width = 540, Height = 150 };
    private readonly ComboBox _outTransport = new();
    private readonly Label _outSqsHint = new() { Text = "Queue is created automatically on Apply.", Left = 16, Top = 56, Width = 500 };
    private readonly TextBox _webhookUrl = new();
    private readonly TextBox _headerName = new();
    private readonly TextBox _headerValue = new();

    public DeviceModel Device { get; private set; }

    public DeviceEditorForm(DeviceModel? existing, string serviceExePath)
    {
        _serviceExePath = serviceExePath;
        Device = existing ?? new DeviceModel { Type = ConfigDefaults.DeviceSerialScale };

        Text = existing is null ? "Add Device" : "Edit Device";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 700);
        AutoScroll = true;

        BuildTop();
        BuildSerialGroup();
        BuildHidGroup();
        BuildBehaviour();
        BuildOutputGroup();
        BuildButtons();

        LoadFrom(Device);
        ApplyTypeVisibility();
        ApplyOutputVisibility();
    }

    private void BuildTop()
    {
        Controls.Add(new Label { Text = "Name", Left = 12, Top = 18, Width = 90 });
        _name.SetBounds(130, 14, 250, 24);
        Controls.Add(_name);

        Controls.Add(new Label { Text = "Type", Left = 12, Top = 54, Width = 90 });
        _type.SetBounds(130, 50, 250, 24);
        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var t in ConfigDefaults.DeviceTypes)
            _type.Items.Add(t);
        _type.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();
        Controls.Add(_type);

        var detect = new Button { Text = "Detect hardware", Left = 400, Top = 49, Width = 150 };
        detect.Click += OnDetect;
        Controls.Add(detect);
    }

    private void BuildSerialGroup()
    {
        AddRowLabel(_serialGroup, "COM port", 26);
        _comPort.SetBounds(150, 22, 150, 24);
        _comPort.DropDownStyle = ComboBoxStyle.DropDown;
        _serialGroup.Controls.Add(_comPort);

        AddRowLabel(_serialGroup, "Baud rate", 56);
        _baud.SetBounds(150, 52, 150, 24);
        FillCombo(_baud, ConfigDefaults.BaudRates);

        AddRowLabel(_serialGroup, "Parity", 86);
        _parity.SetBounds(150, 82, 150, 24);
        FillCombo(_parity, ConfigDefaults.Parities);

        AddRowLabel(_serialGroup, "Data bits", 116);
        _dataBits.SetBounds(150, 112, 150, 24);
        FillCombo(_dataBits, ConfigDefaults.DataBitsOptions);

        AddRowLabel(_serialGroup, "Stop bits", 146);
        _stopBits.SetBounds(150, 142, 150, 24);
        FillCombo(_stopBits, ConfigDefaults.StopBitsOptions);

        AddRowLabel(_serialGroup, "Line ending", 176);
        _lineEnding.SetBounds(150, 172, 150, 24);
        FillCombo(_lineEnding, ConfigDefaults.LineEndings);

        AddRowLabel(_serialGroup, "Encoding", 206);
        _encoding.SetBounds(150, 202, 150, 24);
        FillCombo(_encoding, ConfigDefaults.Encodings);

        AddRowLabel(_serialGroup, "Protocol", 26, 320);
        _protocol.SetBounds(410, 22, 120, 24);
        FillCombo(_protocol, ConfigDefaults.Protocols);

        AddRowLabel(_serialGroup, "Request cmd", 56, 320);
        _requestCommand.SetBounds(410, 52, 120, 24);
        _serialGroup.Controls.Add(_requestCommand);

        AddRowLabel(_serialGroup, "Init commands", 86, 320);
        _initCommands.SetBounds(410, 82, 120, 144);
        _serialGroup.Controls.Add(_initCommands);

        Controls.Add(_serialGroup);
    }

    private void BuildHidGroup()
    {
        AddRowLabel(_hidGroup, "Detected", 26);
        _hidPick.SetBounds(150, 22, 370, 24);
        _hidPick.DropDownStyle = ComboBoxStyle.DropDownList;
        _hidPick.SelectedIndexChanged += OnHidPicked;
        _hidGroup.Controls.Add(_hidPick);

        AddRowLabel(_hidGroup, "Vendor ID", 56);
        _vid.SetBounds(150, 52, 120, 24);
        _vid.Minimum = 0;
        _vid.Maximum = 65535;
        _hidGroup.Controls.Add(_vid);

        AddRowLabel(_hidGroup, "Product ID", 86);
        _pid.SetBounds(150, 82, 120, 24);
        _pid.Minimum = 0;
        _pid.Maximum = 65535;
        _hidGroup.Controls.Add(_pid);

        _hidGroup.Controls.Add(new Label
        {
            Text = "VID/PID are decimal. Use Detect hardware to pick a connected device.",
            Left = 16,
            Top = 120,
            Width = 500,
        });

        Controls.Add(_hidGroup);
    }

    private void BuildBehaviour()
    {
        Controls.Add(_patternLabel);
        Controls.Add(_pattern);

        Controls.Add(new Label { Text = "Poll mode", Left = 12, Top = 390, Width = 110 });
        _pollMode.SetBounds(130, 386, 150, 24);
        _pollMode.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var m in ConfigDefaults.PollModes)
            _pollMode.Items.Add(m);
        Controls.Add(_pollMode);

        Controls.Add(new Label { Text = "Poll interval (ms)", Left = 300, Top = 390, Width = 110 });
        _pollInterval.SetBounds(420, 386, 100, 24);
        _pollInterval.Minimum = 0;
        _pollInterval.Maximum = 600000;
        Controls.Add(_pollInterval);

        _stableOnly.SetBounds(130, 420, 300, 24);
        Controls.Add(_stableOnly);
    }

    private void BuildOutputGroup()
    {
        AddRowLabel(_outputGroup, "Transport", 26);
        _outTransport.SetBounds(150, 22, 150, 24);
        _outTransport.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var t in ConfigDefaults.Transports)
            _outTransport.Items.Add(t);
        _outTransport.SelectedIndexChanged += (_, _) => ApplyOutputVisibility();
        _outputGroup.Controls.Add(_outTransport);

        _outputGroup.Controls.Add(_outSqsHint);

        AddRowLabel(_outputGroup, "Webhook URL", 56);
        _webhookUrl.SetBounds(150, 52, 370, 24);
        _outputGroup.Controls.Add(_webhookUrl);

        AddRowLabel(_outputGroup, "Header name", 86);
        _headerName.SetBounds(150, 82, 200, 24);
        _outputGroup.Controls.Add(_headerName);

        AddRowLabel(_outputGroup, "Header value", 116);
        _headerValue.SetBounds(150, 112, 370, 24);
        _outputGroup.Controls.Add(_headerValue);

        Controls.Add(_outputGroup);
    }

    private void BuildButtons()
    {
        var ok = new Button { Text = "OK", Left = 380, Top = 632, Width = 80 };
        var cancel = new Button { Text = "Cancel", Left = 470, Top = 632, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void AddRowLabel(Control parent, string text, int top, int left = 16)
        => parent.Controls.Add(new Label { Text = text, Left = left, Top = top + 4, Width = 120 });

    private void FillCombo(ComboBox combo, IEnumerable<string> items)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in items)
            combo.Items.Add(i);
        _serialGroup.Controls.Add(combo);
    }

    private void FillCombo(ComboBox combo, IEnumerable<int> items)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in items)
            combo.Items.Add(i);
        _serialGroup.Controls.Add(combo);
    }

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

    private static decimal Clamp(int? value, NumericUpDown box)
    {
        if (value is null)
            return box.Minimum;
        return Math.Clamp(value.Value, (int)box.Minimum, (int)box.Maximum);
    }

    private static void SelectOrDefault(ComboBox combo, object value)
    {
        if (combo.Items.Contains(value))
            combo.SelectedItem = value;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private void ApplyTypeVisibility()
    {
        var type = (string?)_type.SelectedItem ?? ConfigDefaults.DeviceSerialScale;
        var serial = ConfigDefaults.IsSerial(type);
        var hid = ConfigDefaults.IsHid(type);

        _serialGroup.Visible = serial;
        _hidGroup.Visible = hid;

        var showPattern = type == ConfigDefaults.DeviceSerialRaw;
        _patternLabel.Visible = showPattern;
        _pattern.Visible = showPattern;
    }

    private void ApplyOutputVisibility()
    {
        var sqs = (string?)_outTransport.SelectedItem == ConfigDefaults.TransportSqs;
        _outSqsHint.Visible = sqs;
        _webhookUrl.Enabled = !sqs;
        _headerName.Enabled = !sqs;
        _headerValue.Enabled = !sqs;
    }

    private async void OnDetect(object? sender, EventArgs e)
    {
        try
        {
            var client = new ServiceExeClient(new ProcessRunner(), _serviceExePath);
            var inventory = await client.ListDevicesAsync();

            var current = _comPort.Text;
            _comPort.Items.Clear();
            foreach (var port in inventory.SerialPorts)
                _comPort.Items.Add(port);
            _comPort.Text = current;

            _hidPick.Items.Clear();
            foreach (var hid in inventory.HidDevices)
                _hidPick.Items.Add(new HidChoice(hid));
            if (_hidPick.Items.Count == 0)
                _hidPick.Items.Add(new HidChoice(null));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not enumerate hardware: " + ex.Message, "Detect",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void OnOk()
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Device name is required.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var type = (string)_type.SelectedItem!;
        var serial = ConfigDefaults.IsSerial(type);
        var hid = ConfigDefaults.IsHid(type);

        var initCommands = _initCommands.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var outTransport = (string)_outTransport.SelectedItem!;
        var output = new DeviceOutputModel { Transport = outTransport };
        if (outTransport == ConfigDefaults.TransportHttp)
        {
            if (string.IsNullOrWhiteSpace(_webhookUrl.Text))
            {
                MessageBox.Show(this, "Webhook URL is required for HTTP output.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            output.WebhookUrl = _webhookUrl.Text.Trim();
            output.HeaderName = string.IsNullOrWhiteSpace(_headerName.Text) ? null : _headerName.Text.Trim();
            output.HeaderValue = string.IsNullOrWhiteSpace(_headerValue.Text) ? null : _headerValue.Text;
        }

        Device = new DeviceModel
        {
            Name = name,
            Type = type,
            PollMode = (string)_pollMode.SelectedItem!,
            PollIntervalMs = (int)_pollInterval.Value,
            StableOnly = _stableOnly.Checked,
            Output = output,
        };

        if (serial)
        {
            Device.ComPort = string.IsNullOrWhiteSpace(_comPort.Text) ? null : _comPort.Text.Trim();
            Device.BaudRate = (int)_baud.SelectedItem!;
            Device.Parity = (string)_parity.SelectedItem!;
            Device.DataBits = (int)_dataBits.SelectedItem!;
            Device.StopBits = (int)_stopBits.SelectedItem!;
            Device.LineEnding = (string)_lineEnding.SelectedItem!;
            Device.Encoding = (string)_encoding.SelectedItem!;
            Device.Protocol = (string)_protocol.SelectedItem!;
            Device.RequestCommand = string.IsNullOrWhiteSpace(_requestCommand.Text) ? null : _requestCommand.Text.Trim();
            Device.InitCommands = initCommands.Count > 0 ? initCommands : null;
            if (type == ConfigDefaults.DeviceSerialRaw && !string.IsNullOrWhiteSpace(_pattern.Text))
                Device.Pattern = _pattern.Text.Trim();
        }

        if (hid)
        {
            Device.Vid = (int)_vid.Value;
            Device.Pid = (int)_pid.Value;
        }

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
                ? "(no HID devices found)"
                : $"VID={info.Vid:X4} PID={info.Pid:X4} {info.Product}";
        }
    }
}
