using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudPrint.Configurator.Core.Config;
using CloudPrint.Configurator.Core.Devices;
using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator;

/// <summary>
/// Add/edit a device. On open it scans the machine and offers detected hardware to pick from; a Test
/// button shows a live reading; arcane serial/poll/framing settings hide behind "Show advanced."
/// Sensible defaults everywhere — a mortal types a name, picks the device, and clicks Test.
///
/// Layout: one connection slot (serial / USB HID / TCP, switched by Type), then the shared
/// "Data framing &amp; commands" group for byte-stream devices (serial + TCP), then behaviour and output.
/// </summary>
internal sealed partial class DeviceEditorForm : Form
{
    private readonly string _serviceExePath;
    private readonly DevicePreviewRunner _preview;
    private readonly List<Control> _advanced = new();

    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly CheckBox _showAdvanced = new() { Text = "Show advanced settings", AutoSize = true };

    // Slot A: connection (one of three groups visible)
    private const int SlotTop = 116;
    private const int SlotHeight = 204;
    private readonly GroupBox _serialGroup = new() { Text = "Serial connection (RS-232 / USB virtual COM)", Left = 12, Top = SlotTop, Width = 560, Height = SlotHeight };
    private readonly ComboBox _comPort = new();
    private readonly ComboBox _baud = new();
    private readonly ComboBox _parity = new();
    private readonly ComboBox _dataBits = new();
    private readonly ComboBox _stopBits = new();
    private readonly CheckBox _dtr = new() { Text = "Assert DTR", AutoSize = true };
    private readonly CheckBox _rts = new() { Text = "Assert RTS", AutoSize = true };
    private readonly Label _serialHint = new() { Left = 16, Top = 176, Width = 530, ForeColor = Color.DimGray };

    private readonly GroupBox _hidGroup = new() { Text = "USB HID device", Left = 12, Top = SlotTop, Width = 560, Height = SlotHeight };
    private readonly ComboBox _hidPick = new();
    private readonly NumericUpDown _vid = new();
    private readonly NumericUpDown _pid = new();
    private readonly Label _hidHint = new() { Left = 16, Top = 136, Width = 530, Height = 60, ForeColor = Color.DimGray };

    private readonly GroupBox _tcpGroup = new() { Text = "Network device (TCP — the device listens, e.g. Cubiscan :1050)", Left = 12, Top = SlotTop, Width = 560, Height = SlotHeight };
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new();
    private readonly NumericUpDown _connectTimeout = new();

    // Stream group: framing + commands, shared by serial and TCP
    private const int StreamTop = SlotTop + SlotHeight + 10;
    private const int StreamHeight = 282;
    private readonly GroupBox _streamGroup = new() { Text = "Data framing && commands", Left = 12, Top = StreamTop, Width = 560, Height = StreamHeight };
    private readonly ComboBox _frameMode = new();
    private readonly ComboBox _lineEnding = new();
    private readonly TextBox _frameStart = new();
    private readonly TextBox _frameEnd = new();
    private readonly NumericUpDown _idleGap = new();
    private readonly ComboBox _encoding = new();
    private readonly TextBox _requestCommand = new();
    private readonly TextBox _commandTerminator = new();
    private readonly TextBox _initCommands = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _protocol = new();
    private readonly Label _patternLabel = new();
    private readonly TextBox _pattern = new();
    private readonly Label _streamHint = new() { Left = 16, Top = 236, Width = 530, Height = 40, ForeColor = Color.DimGray };

    // Everything below the connection/framing groups lives in one panel so it can move up when the
    // framing group is hidden (HID devices have no byte stream to frame).
    private const int BehaviourTop = StreamTop + StreamHeight + 12;
    private readonly Panel _lower = new() { Left = 0, Top = BehaviourTop, Width = 600 };
    private readonly ComboBox _pollMode = new();
    private readonly NumericUpDown _pollInterval = new();
    private readonly CheckBox _stableOnly = new() { Text = "Only publish stable readings", AutoSize = true };
    private readonly NumericUpDown _heartbeat = new();
    private readonly NumericUpDown _staleAfter = new();

    // Output (tops below are relative to _lower)
    private const int OutputTop = 96;
    private readonly GroupBox _outputGroup = new() { Text = "Where readings go", Left = 12, Top = OutputTop, Width = 560, Height = 150 };
    private readonly ComboBox _outTransport = new();
    private readonly Label _outSqsHint = new() { Text = "Sent to the cloud (SQS queue auto-created).", Left = 306, Top = 26, Width = 245, ForeColor = Color.DimGray };
    private readonly TextBox _webhookUrl = new();
    private readonly TextBox _headerName = new();
    private readonly TextBox _headerValue = new();

    private const int TestTop = OutputTop + 150 + 12;
    private const int LowerHeight = TestTop + 90;
    private readonly Button _test = new() { Text = "Test (live reading)", Left = 12, Top = TestTop, Width = 160 };
    private readonly Label _reading = new() { Left = 182, Top = TestTop + 2, Width = 400, Height = 40, AutoSize = false, ForeColor = Color.DimGray };
    private bool _previewing;

    public DeviceModel Device { get; private set; }

    /// <summary>Test/screenshot hook: supplies the hardware inventory instead of running the service exe.</summary>
    internal static Func<Task<DeviceInventory>>? InventoryOverride { get; set; }

    /// <summary>Full un-scrolled height of the dialog (used by the screenshot harness to capture everything).</summary>
    internal int FullClientHeight => _lower.Top + LowerHeight;

    /// <summary>Screenshot hook: force the type/advanced state after construction.</summary>
    internal void SetState(string type, bool showAdvanced)
    {
        _type.SelectedItem = type;
        _showAdvanced.Checked = showAdvanced;
        ApplyTypeVisibility();
        ApplyAdvancedVisibility();
    }

    internal Task DetectForScreenshotAsync() => DetectAsync();

    public DeviceEditorForm(DeviceModel? existing, string serviceExePath)
    {
        _serviceExePath = serviceExePath;
        _preview = new DevicePreviewRunner(serviceExePath);
        Device = existing ?? new DeviceModel { Type = ConfigDefaults.DefaultDeviceType };

        Text = existing is null ? "Add device" : "Edit device";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        _lower.Height = LowerHeight;
        Controls.Add(_lower);
        ClientSize = new Size(600, Math.Min(BehaviourTop + LowerHeight, Screen.PrimaryScreen?.WorkingArea.Height - 80 ?? 900));
        AutoScroll = true;

        BuildTop();
        BuildSerialGroup();
        BuildHidGroup();
        BuildTcpGroup();
        BuildStreamGroup();
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

        Controls.Add(new Label
        {
            Text = "raw = forward every frame verbatim (start here for discovery); scale = also try to parse a weight.",
            Left = 200, Top = 80, Width = 385, Height = 34, ForeColor = Color.DimGray
        });
    }

    private void BuildSerialGroup()
    {
        AddLabel(_serialGroup, "COM port", 26);
        _comPort.SetBounds(150, 22, 390, 24);
        _comPort.DropDownStyle = ComboBoxStyle.DropDown; // pick a detected port (shown with its USB identity) or type COMn / auto
        _serialGroup.Controls.Add(_comPort);

        AddLabel(_serialGroup, "Baud rate", 56);
        _baud.SetBounds(150, 52, 160, 24);
        FillSerial(_baud, ConfigDefaults.BaudRates);

        AdvancedRow(_serialGroup, "Parity", 86, _parity, ConfigDefaults.Parities);
        AdvancedRow(_serialGroup, "Data bits", 116, _dataBits, ConfigDefaults.DataBitsOptions);
        var stopLabel = AddLabel(_serialGroup, "Stop bits", 146, 330);
        _stopBits.SetBounds(420, 142, 120, 24);
        _stopBits.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var i in ConfigDefaults.StopBitsOptions) _stopBits.Items.Add(i);
        _serialGroup.Controls.Add(_stopBits);
        MarkAdvanced(stopLabel, _stopBits);

        _dtr.Location = new Point(334, 88);
        _rts.Location = new Point(334, 114);
        _serialGroup.Controls.Add(_dtr);
        _serialGroup.Controls.Add(_rts);
        MarkAdvanced(_dtr, _rts);

        _serialHint.Text = "Common: NCI/Toledo scales 9600 7E1 · Fairbanks 9600 7O2 · Cubiscan / most indicators 9600 8N1.";
        _serialGroup.Controls.Add(_serialHint);

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
            Left = 16, Top = 112, Width = 520, ForeColor = Color.DimGray
        });
        _hidHint.Text = "Devices marked [HID scale] expose the weighing-device usage page and need no driver. " +
                        "A scale that shows up as a keyboard is in keyboard-wedge mode — switch it to HID-POS on the scale.";
        _hidGroup.Controls.Add(_hidHint);

        Controls.Add(_hidGroup);
    }

    private void BuildTcpGroup()
    {
        AddLabel(_tcpGroup, "Host / IP", 26);
        _host.SetBounds(150, 22, 240, 24);
        _tcpGroup.Controls.Add(_host);

        AddLabel(_tcpGroup, "Port", 56);
        _port.SetBounds(150, 52, 120, 24);
        _port.Minimum = 1;
        _port.Maximum = 65535;
        _port.Value = ConfigDefaults.DefaultTcpPort;
        _tcpGroup.Controls.Add(_port);

        var toLabel = AddLabel(_tcpGroup, "Connect timeout (ms)", 86);
        _connectTimeout.SetBounds(150, 82, 120, 24);
        _connectTimeout.Minimum = 500;
        _connectTimeout.Maximum = 60000;
        _connectTimeout.Increment = 500;
        _connectTimeout.Value = 5000;
        _tcpGroup.Controls.Add(_connectTimeout);
        MarkAdvanced(toLabel, _connectTimeout);

        _tcpGroup.Controls.Add(new Label
        {
            Text = "Cubiscan: static IP (often 10.1.100.100), port 1050, framing STX/ETX, request <STX>M<ETX>. " +
                   "Rice Lake iDimension: its configured port, Cubiscan or QubeVu protocol.",
            Left = 16, Top = 118, Width = 530, Height = 60, ForeColor = Color.DimGray
        });

        Controls.Add(_tcpGroup);
    }

    private void BuildStreamGroup()
    {
        var g = _streamGroup;

        AddLabel(g, "Frame mode", 26);
        _frameMode.SetBounds(150, 22, 160, 24);
        _frameMode.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var m in ConfigDefaults.FrameModes) _frameMode.Items.Add(m);
        _frameMode.SelectedIndexChanged += (_, _) => ApplyFrameModeVisibility();
        g.Controls.Add(_frameMode);

        AddLabel(g, "Line ending", 26, 330);
        _lineEnding.SetBounds(420, 22, 120, 24);
        _lineEnding.DropDownStyle = ComboBoxStyle.DropDown; // editable: crlf/lf/cr or an escaped literal
        foreach (var e in ConfigDefaults.LineEndings.Where(e => e != "literal")) _lineEnding.Items.Add(e);
        g.Controls.Add(_lineEnding);

        AddLabel(g, "Frame start", 56);
        _frameStart.SetBounds(150, 52, 160, 24);
        _frameStart.PlaceholderText = "<STX>";
        g.Controls.Add(_frameStart);
        AddLabel(g, "Frame end", 56, 330);
        _frameEnd.SetBounds(420, 52, 120, 24);
        _frameEnd.PlaceholderText = "<ETX>";
        g.Controls.Add(_frameEnd);

        AddLabel(g, "Idle gap (ms)", 86);
        _idleGap.SetBounds(150, 82, 160, 24);
        _idleGap.Minimum = 10;
        _idleGap.Maximum = 10000;
        _idleGap.Value = ConfigDefaults.DefaultIdleGapMs;
        g.Controls.Add(_idleGap);
        var encLabel = AddLabel(g, "Encoding", 86, 330);
        _encoding.SetBounds(420, 82, 120, 24);
        _encoding.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var e in ConfigDefaults.Encodings) _encoding.Items.Add(e);
        g.Controls.Add(_encoding);
        MarkAdvanced(encLabel, _encoding);

        AddLabel(g, "Request cmd", 116);
        _requestCommand.SetBounds(150, 112, 160, 24);
        _requestCommand.PlaceholderText = "e.g. W or <STX>M<ETX>";
        g.Controls.Add(_requestCommand);
        var termLabel = AddLabel(g, "Cmd terminator", 116, 330);
        _commandTerminator.SetBounds(420, 112, 120, 24);
        _commandTerminator.PlaceholderText = ConfigDefaults.CommandTerminatorDefaultLabel;
        g.Controls.Add(_commandTerminator);
        MarkAdvanced(termLabel, _commandTerminator);

        var initLabel = AddLabel(g, "Init commands", 146);
        _initCommands.SetBounds(150, 142, 160, 56);
        g.Controls.Add(_initCommands);
        MarkAdvanced(initLabel, _initCommands);
        var protoLabel = AddLabel(g, "Protocol", 146, 330);
        _protocol.SetBounds(420, 142, 120, 24);
        _protocol.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var p in ConfigDefaults.Protocols) _protocol.Items.Add(p);
        g.Controls.Add(_protocol);
        MarkAdvanced(protoLabel, _protocol);

        _patternLabel.Text = "Pattern (regex)";
        _patternLabel.SetBounds(16, 210, 130, 20);
        _pattern.SetBounds(150, 206, 390, 24);
        _pattern.PlaceholderText = "optional: named groups value / unit / stable";
        g.Controls.Add(_patternLabel);
        g.Controls.Add(_pattern);
        MarkAdvanced(_patternLabel, _pattern);

        _streamHint.Text = "Escapes: <STX> <ETX> <CR> <LF> <ENQ> or \\x02. Cmd terminator: blank = same as line ending, none = nothing.";
        g.Controls.Add(_streamHint);

        Controls.Add(g);
    }

    private void BuildBehaviour()
    {
        var pollLabel = new Label { Text = "Poll mode", Left = 12, Top = 4, Width = 100 };
        _lower.Controls.Add(pollLabel);
        _pollMode.SetBounds(130, 0, 150, 24);
        _pollMode.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var m in ConfigDefaults.PollModes)
            _pollMode.Items.Add(m);
        _lower.Controls.Add(_pollMode);

        var intervalLabel = new Label { Text = "Poll interval (ms)", Left = 300, Top = 4, Width = 110 };
        _lower.Controls.Add(intervalLabel);
        _pollInterval.SetBounds(420, 0, 100, 24);
        _pollInterval.Minimum = 0;
        _pollInterval.Maximum = 600000;
        _lower.Controls.Add(_pollInterval);

        _stableOnly.Location = new Point(130, 32);
        _lower.Controls.Add(_stableOnly);

        var hbLabel = new Label { Text = "Heartbeat (s)", Left = 12, Top = 64, Width = 100 };
        _lower.Controls.Add(hbLabel);
        _heartbeat.SetBounds(130, 60, 100, 24);
        _heartbeat.Minimum = 0;
        _heartbeat.Maximum = 86400;
        _lower.Controls.Add(_heartbeat);
        var staleLabel = new Label { Text = "Stale after (s)", Left = 300, Top = 64, Width = 110 };
        _lower.Controls.Add(staleLabel);
        _staleAfter.SetBounds(420, 60, 100, 24);
        _staleAfter.Minimum = 0;
        _staleAfter.Maximum = 86400;
        _lower.Controls.Add(_staleAfter);
        MarkAdvanced(hbLabel, _heartbeat, staleLabel, _staleAfter);
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

        _lower.Controls.Add(_outputGroup);
    }

    private void BuildTestAndButtons()
    {
        _test.Click += OnTest;
        _lower.Controls.Add(_test);
        _lower.Controls.Add(_reading);

        var ok = new Button { Text = "OK", Left = 410, Top = TestTop + 46, Width = 80 };
        var cancel = new Button { Text = "Cancel", Left = 500, Top = TestTop + 46, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        _lower.Controls.Add(ok);
        _lower.Controls.Add(cancel);
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
        _type.SelectedItem = ConfigDefaults.DeviceTypes.Contains(d.Type) ? d.Type : ConfigDefaults.DefaultDeviceType;

        _comPort.Text = d.ComPort ?? string.Empty;
        SelectOrDefault(_baud, d.BaudRate ?? ConfigDefaults.DefaultBaudRate);
        SelectOrDefault(_parity, d.Parity ?? ConfigDefaults.DefaultParity);
        SelectOrDefault(_dataBits, d.DataBits ?? ConfigDefaults.DefaultDataBits);
        SelectOrDefault(_stopBits, d.StopBits ?? ConfigDefaults.DefaultStopBits);
        _dtr.Checked = d.DtrEnable ?? false;
        _rts.Checked = d.RtsEnable ?? false;

        _host.Text = d.Host ?? string.Empty;
        _port.Value = d.Port is >= 1 and <= 65535 ? d.Port.Value : ConfigDefaults.DefaultTcpPort;
        _connectTimeout.Value = d.ConnectTimeoutMs is >= 500 and <= 60000 ? d.ConnectTimeoutMs.Value : 5000;

        SelectOrDefault(_frameMode, d.FrameMode ?? ConfigDefaults.DefaultFrameMode);
        _lineEnding.Text = d.LineEnding ?? ConfigDefaults.DefaultLineEnding;
        _frameStart.Text = d.FrameStart ?? string.Empty;
        _frameEnd.Text = d.FrameEnd ?? string.Empty;
        _idleGap.Value = d.IdleGapMs is >= 10 and <= 10000 ? d.IdleGapMs.Value : ConfigDefaults.DefaultIdleGapMs;
        SelectOrDefault(_encoding, d.Encoding ?? ConfigDefaults.DefaultEncoding);
        _requestCommand.Text = d.RequestCommand ?? string.Empty;
        _commandTerminator.Text = d.CommandTerminator ?? string.Empty;
        _initCommands.Text = d.InitCommands is null ? string.Empty : string.Join(Environment.NewLine, d.InitCommands);
        SelectOrDefault(_protocol, d.Protocol ?? ConfigDefaults.DefaultProtocol);
        _pattern.Text = d.Pattern ?? string.Empty;

        _vid.Value = Clamp(d.Vid, _vid);
        _pid.Value = Clamp(d.Pid, _pid);

        SelectOrDefault(_pollMode, d.PollMode ?? ConfigDefaults.DefaultPollMode);
        _pollInterval.Value = d.PollIntervalMs is >= 0 and <= 600000 ? d.PollIntervalMs.Value : ConfigDefaults.DefaultDevicePollIntervalMs;
        _stableOnly.Checked = d.StableOnly ?? ConfigDefaults.DefaultDeviceStableOnly;
        _heartbeat.Value = d.HeartbeatSeconds is >= 0 and <= 86400 ? d.HeartbeatSeconds.Value : ConfigDefaults.DefaultDeviceHeartbeatSeconds;
        _staleAfter.Value = d.StaleAfterSeconds is >= 0 and <= 86400 ? d.StaleAfterSeconds.Value : ConfigDefaults.DefaultDeviceStaleAfterSeconds;

        var output = d.Output ?? new DeviceOutputModel();
        SelectOrDefault(_outTransport, output.Transport);
        _webhookUrl.Text = output.WebhookUrl ?? string.Empty;
        _headerName.Text = output.HeaderName ?? "X-Api-Key";
        _headerValue.Text = output.HeaderValue ?? string.Empty;

        ApplyFrameModeVisibility();
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

    private string SelectedType() => (string?)_type.SelectedItem ?? ConfigDefaults.DefaultDeviceType;

    private void ApplyTypeVisibility()
    {
        var type = SelectedType();
        _serialGroup.Visible = ConfigDefaults.IsSerial(type);
        _hidGroup.Visible = ConfigDefaults.IsHid(type);
        _tcpGroup.Visible = ConfigDefaults.IsTcp(type);
        _streamGroup.Visible = ConfigDefaults.IsStream(type);
        _lower.Top = _streamGroup.Visible ? BehaviourTop : StreamTop;

        var isRaw = type is ConfigDefaults.DeviceSerialRaw or ConfigDefaults.DeviceTcpRaw;
        _patternLabel.Visible = isRaw && _showAdvanced.Checked;
        _pattern.Visible = isRaw && _showAdvanced.Checked;
        _protocol.Enabled = !isRaw;
    }

    private void ApplyFrameModeVisibility()
    {
        var mode = (string?)_frameMode.SelectedItem ?? ConfigDefaults.DefaultFrameMode;
        _frameStart.Enabled = mode == "delimited";
        _frameEnd.Enabled = mode == "delimited";
        _idleGap.Enabled = mode == "idle";
        _lineEnding.Enabled = mode != "idle";
    }

    private void ApplyAdvancedVisibility()
    {
        foreach (var c in _advanced)
            c.Visible = _showAdvanced.Checked;
        ApplyTypeVisibility();
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
            var inventory = InventoryOverride is { } o
                ? await o()
                : await new ServiceExeClient(new ProcessRunner(), _serviceExePath).ListDevicesAsync();

            var keepPort = _comPort.Text;
            _comPort.Items.Clear();
            foreach (var port in inventory.SerialPorts)
                _comPort.Items.Add(new SerialChoice(port));
            _comPort.Text = keepPort;

            _hidPick.Items.Clear();
            foreach (var hid in inventory.HidDevices.OrderByDescending(h => h.IsScale).ThenBy(h => h.Product))
                _hidPick.Items.Add(new HidChoice(hid));
            if (_hidPick.Items.Count == 0)
                _hidPick.Items.Add(new HidChoice(null));

            // Pre-select: the device already configured, else the (first) HID scale for a brand-new entry.
            var current = _hidPick.Items.OfType<HidChoice>()
                .FirstOrDefault(c => c.Info is { } i && i.Vid == (int)_vid.Value && i.Pid == (int)_pid.Value && (_vid.Value > 0 || _pid.Value > 0));
            var firstScale = _hidPick.Items.OfType<HidChoice>().FirstOrDefault(c => c.Info is { IsScale: true });
            var pick = current ?? (_vid.Value == 0 && _pid.Value == 0 ? firstScale : null);
            if (pick is not null)
                _hidPick.SelectedItem = pick; // fires OnHidPicked → fills VID/PID
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
                _reading.ForeColor = reading.IsEvent ? Color.DarkSlateBlue : Color.Green;
                _reading.Text = (reading.IsEvent ? "" : "reading now: ") + reading.Describe();
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

    private static readonly Regex ComName = new(@"^\s*(COM\d+|auto(?::\S+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            HeartbeatSeconds = _heartbeat.Value > 0 ? (int)_heartbeat.Value : null,
            StaleAfterSeconds = _staleAfter.Value > 0 ? (int)_staleAfter.Value : null,
        };

        if (ConfigDefaults.IsSerial(type))
        {
            var portText = _comPort.Text;
            var m = ComName.Match(portText);
            if (!m.Success)
            {
                error = "Choose a COM port (or type auto to find it by VID/PID).";
                return null;
            }
            device.ComPort = m.Groups[1].Value.ToUpperInvariant().StartsWith("COM") ? m.Groups[1].Value.ToUpperInvariant() : m.Groups[1].Value;
            device.BaudRate = (int)_baud.SelectedItem!;
            device.Parity = (string)_parity.SelectedItem!;
            device.DataBits = (int)_dataBits.SelectedItem!;
            device.StopBits = (int)_stopBits.SelectedItem!;
            device.DtrEnable = _dtr.Checked ? true : null;
            device.RtsEnable = _rts.Checked ? true : null;
            if (_comPort.SelectedItem is SerialChoice { Info: { Vid: { } v, Pid: { } p } })
            {
                device.Vid = v; // remembered so "auto" can find the port later if it renumbers
                device.Pid = p;
            }
        }
        else if (ConfigDefaults.IsTcp(type))
        {
            if (string.IsNullOrWhiteSpace(_host.Text))
            {
                error = "Enter the device's host name or IP address.";
                return null;
            }
            device.Host = _host.Text.Trim();
            device.Port = (int)_port.Value;
            device.ConnectTimeoutMs = (int)_connectTimeout.Value == 5000 ? null : (int)_connectTimeout.Value;
        }
        else
        {
            device.Vid = (int)_vid.Value;
            device.Pid = (int)_pid.Value;
        }

        if (ConfigDefaults.IsStream(type))
        {
            var mode = (string)_frameMode.SelectedItem!;
            device.FrameMode = mode == ConfigDefaults.DefaultFrameMode ? null : mode;
            var lineEnding = _lineEnding.Text.Trim();
            device.LineEnding = string.IsNullOrEmpty(lineEnding) ? null : lineEnding;
            if (mode == "delimited")
            {
                device.FrameStart = string.IsNullOrEmpty(_frameStart.Text) ? null : _frameStart.Text;
                device.FrameEnd = string.IsNullOrEmpty(_frameEnd.Text) ? null : _frameEnd.Text;
                if (device.FrameEnd is null)
                {
                    error = "Delimited framing needs a frame end (e.g. <ETX>).";
                    return null;
                }
            }
            if (mode == "idle")
                device.IdleGapMs = (int)_idleGap.Value == ConfigDefaults.DefaultIdleGapMs ? null : (int)_idleGap.Value;
            device.Encoding = (string)_encoding.SelectedItem!;
            device.RequestCommand = string.IsNullOrWhiteSpace(_requestCommand.Text) ? null : _requestCommand.Text.Trim();
            device.CommandTerminator = string.IsNullOrWhiteSpace(_commandTerminator.Text) ? null : _commandTerminator.Text.Trim();
            var init = _initCommands.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            device.InitCommands = init.Count > 0 ? init : null;
            if (type is ConfigDefaults.DeviceSerialScale or ConfigDefaults.DeviceTcpScale)
                device.Protocol = (string)_protocol.SelectedItem!;
            if (type is ConfigDefaults.DeviceSerialRaw or ConfigDefaults.DeviceTcpRaw && !string.IsNullOrWhiteSpace(_pattern.Text))
                device.Pattern = _pattern.Text.Trim();
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
            if (info is null)
                return "(no USB devices found)";
            var name = string.IsNullOrWhiteSpace(info.Product) ? "USB device" : info.Product;
            return $"{name}  (VID={info.Vid:X4} PID={info.Pid:X4}){(info.IsScale ? "  [HID scale]" : "")}";
        }
    }

    /// <summary>ComboBox item wrapper for a discovered COM port.</summary>
    private sealed class SerialChoice
    {
        public SerialChoice(SerialPortInfo info) => Info = info;
        public SerialPortInfo Info { get; }
        public override string ToString() => Info.Describe();
    }
}
