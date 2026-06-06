using System.Drawing;
using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator;

/// <summary>Add/edit a printer. Pick the printer; DPI and fit mode are tucked behind "Show advanced."</summary>
internal sealed class PrinterEditorForm : Form
{
    private readonly ComboBox _printer = new();
    private readonly CheckBox _showAdvanced = new() { Text = "Show advanced (PDF render settings)", AutoSize = true };
    private readonly NumericUpDown _dpi = new();
    private readonly ComboBox _fit = new();
    private readonly List<Control> _advanced = new();

    public PrinterLaneModel Lane { get; private set; }

    public PrinterEditorForm(PrinterLaneModel? existing, IEnumerable<string> installedPrinters)
    {
        Lane = existing ?? new PrinterLaneModel();

        Text = existing is null ? "Add printer" : "Edit printer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 220);

        Controls.Add(new Label { Text = "Printer", Left = 16, Top = 20, Width = 80 });
        _printer.SetBounds(110, 16, 310, 24);
        _printer.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var p in installedPrinters)
            _printer.Items.Add(p);
        _printer.Text = Lane.PrinterName;
        Controls.Add(_printer);

        _showAdvanced.Location = new Point(16, 52);
        _showAdvanced.CheckedChanged += (_, _) => ApplyAdvancedVisibility();
        Controls.Add(_showAdvanced);

        var dpiLabel = new Label { Text = "PDF render DPI", Left = 16, Top = 90, Width = 100 };
        Controls.Add(dpiLabel);
        _dpi.SetBounds(130, 86, 100, 24);
        _dpi.Minimum = 72;
        _dpi.Maximum = 1200;
        _dpi.Value = Lane.PdfRenderDpi is >= 72 and <= 1200 ? Lane.PdfRenderDpi.Value : ConfigDefaults.DefaultPdfRenderDpi;
        Controls.Add(_dpi);

        var fitLabel = new Label { Text = "PDF fit mode", Left = 16, Top = 122, Width = 100 };
        Controls.Add(fitLabel);
        _fit.SetBounds(130, 118, 200, 24);
        _fit.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var f in ConfigDefaults.FitModes)
            _fit.Items.Add(f);
        var fit = Lane.PdfFitMode;
        _fit.SelectedItem = fit is not null && ConfigDefaults.FitModes.Contains(fit) ? fit : ConfigDefaults.DefaultPdfFitMode;
        Controls.Add(_fit);

        _advanced.AddRange(new Control[] { dpiLabel, _dpi, fitLabel, _fit });
        ApplyAdvancedVisibility();

        var ok = new Button { Text = "OK", Left = 250, Top = 170, Width = 80 };
        var cancel = new Button { Text = "Cancel", Left = 340, Top = 170, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void ApplyAdvancedVisibility()
    {
        foreach (var c in _advanced)
            c.Visible = _showAdvanced.Checked;
    }

    private void OnOk()
    {
        if (string.IsNullOrWhiteSpace(_printer.Text))
        {
            MessageBox.Show(this, "Select or enter a printer name.", "Almost there",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Lane = new PrinterLaneModel
        {
            PrinterName = _printer.Text.Trim(),
            QueueUrl = Lane.QueueUrl,
            PdfRenderDpi = (int)_dpi.Value,
            PdfFitMode = (string)_fit.SelectedItem!,
        };
        DialogResult = DialogResult.OK;
    }
}
