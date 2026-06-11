using System.Drawing;
using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator;

/// <summary>
/// Add/edit a printer. Render settings are part of the add flow: DPI offers the common
/// printer resolutions (editable for anything else) and must match the printer's native
/// DPI so pages rasterize 1:1 onto device dots; fit mode and black &amp; white sit alongside.
/// </summary>
internal sealed class PrinterEditorForm : Form
{
    private readonly ComboBox _printer = new();
    private readonly ComboBox _dpi = new();
    private readonly ComboBox _fit = new();
    private readonly CheckBox _mono = new()
    {
        Text = "Pure black && white (thermal label printers)",
        AutoSize = true,
    };

    public PrinterLaneModel Lane { get; private set; }

    public PrinterEditorForm(PrinterLaneModel? existing, IEnumerable<string> installedPrinters)
    {
        Lane = existing ?? new PrinterLaneModel();

        Text = existing is null ? "Add printer" : "Edit printer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 236);

        Controls.Add(new Label { Text = "Printer", Left = 16, Top = 20, Width = 80 });
        _printer.SetBounds(130, 16, 290, 24);
        _printer.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var p in installedPrinters)
            _printer.Items.Add(p);
        _printer.Text = Lane.PrinterName;
        Controls.Add(_printer);

        Controls.Add(new Label { Text = "Printer DPI", Left = 16, Top = 56, Width = 100 });
        _dpi.SetBounds(130, 52, 100, 24);
        _dpi.DropDownStyle = ComboBoxStyle.DropDown; // editable: uncommon DPIs can be typed in
        foreach (var d in ConfigDefaults.CommonPdfRenderDpis)
            _dpi.Items.Add(d.ToString());
        _dpi.Text = (Lane.PdfRenderDpi is >= ConfigDefaults.MinPdfRenderDpi and <= ConfigDefaults.MaxPdfRenderDpi
            ? Lane.PdfRenderDpi.Value : ConfigDefaults.DefaultPdfRenderDpi).ToString();
        Controls.Add(_dpi);
        Controls.Add(new Label
        {
            Text = "Match the printer's resolution (thermal: 203)",
            Left = 240, Top = 56, Width = 190,
            ForeColor = SystemColors.GrayText,
        });

        Controls.Add(new Label { Text = "PDF fit mode", Left = 16, Top = 88, Width = 100 });
        _fit.SetBounds(130, 84, 200, 24);
        _fit.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var f in ConfigDefaults.FitModes)
            _fit.Items.Add(f);
        var fit = Lane.PdfFitMode;
        _fit.SelectedItem = fit is not null && ConfigDefaults.FitModes.Contains(fit) ? fit : ConfigDefaults.DefaultPdfFitMode;
        Controls.Add(_fit);

        _mono.Location = new Point(130, 120);
        _mono.Checked = Lane.PdfMonochrome ?? ConfigDefaults.DefaultPdfMonochrome;
        Controls.Add(_mono);

        var ok = new Button { Text = "OK", Left = 250, Top = 186, Width = 80 };
        var cancel = new Button { Text = "Cancel", Left = 340, Top = 186, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnOk()
    {
        if (string.IsNullOrWhiteSpace(_printer.Text))
        {
            MessageBox.Show(this, "Select or enter a printer name.", "Almost there",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_dpi.Text.Trim(), out var dpi)
            || dpi is < ConfigDefaults.MinPdfRenderDpi or > ConfigDefaults.MaxPdfRenderDpi)
        {
            MessageBox.Show(this,
                $"Printer DPI must be a number between {ConfigDefaults.MinPdfRenderDpi} and {ConfigDefaults.MaxPdfRenderDpi}.",
                "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Lane = new PrinterLaneModel
        {
            PrinterName = _printer.Text.Trim(),
            QueueUrl = Lane.QueueUrl,
            PdfRenderDpi = dpi,
            PdfFitMode = (string)_fit.SelectedItem!,
            PdfMonochrome = _mono.Checked,
        };
        DialogResult = DialogResult.OK;
    }
}
