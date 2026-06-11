// Local harness that reproduces CloudPrint's PDF print pipeline off-Windows.
//
// Stage 1 mirrors PdfPrinter.cs exactly: Docnet/PDFium rasterization at
// PdfRenderDpi via PageDimensions(dpi / 72.0), raw BGRA output.
// Stage 2 simulates what PrintDocument + Graphics.DrawImage does on Windows:
// the bitmap is resampled to the printer's device-pixel grid with default
// (bilinear) interpolation, alpha-composited onto the page.
// Stage 3 is the proposed fix: render directly at the device DPI, flattened
// onto a white background, so the driver receives a 1:1 opaque bitmap.
//
// Usage: dotnet run -- <pdf> <renderDpi> <deviceDpi> [outDir]

using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: PdfRenderHarness <pdf> <renderDpi> <deviceDpi> [outDir]");
    return 1;
}

// PNG input: just emit the text zoom for comparison against other renderers.
if (args[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
{
    var png = Image.Load<Bgra32>(Path.GetFullPath(args[0]));
    var dir = Path.GetFullPath(args.Length > 3 ? args[3] : "out");
    Directory.CreateDirectory(dir);
    SaveTextZoom(png, Path.GetFileNameWithoutExtension(args[0]), dir);
    Console.WriteLine($"text zoom written to {dir}");
    return 0;
}

var pdfPath = Path.GetFullPath(args[0]);
var renderDpi = int.Parse(args[1]);
var deviceDpi = int.Parse(args[2]);
var outDir = Path.GetFullPath(args.Length > 3 ? args[3] : "out");
Directory.CreateDirectory(outDir);

// ---- Stage 1: rasterize exactly like PdfPrinter.cs ----
var (stage1, pdfWpt, pdfHpt) = RenderWithDocnet(pdfPath, renderDpi / 72.0);
ReportAlpha("stage1 (service raster)", stage1);
stage1.SaveAsPng(Path.Combine(outDir, $"stage1_raster_{renderDpi}dpi.png"));

// ---- Stage 2: simulate the print-time DrawImage resample ----
// PageBounds is in hundredths of an inch; the driver maps it to device pixels.
// Net effect: the stage-1 bitmap is resampled to (inches * deviceDpi) with
// GDI+'s default bilinear filter, then alpha-blended onto the page.
var devW = (int)Math.Round(pdfWpt / 72.0 * deviceDpi);
var devH = (int)Math.Round(pdfHpt / 72.0 * deviceDpi);
var stage2 = stage1.Clone(ctx => ctx.Resize(devW, devH, KnownResamplers.Triangle));
FlattenOntoWhite(stage2);
stage2.SaveAsPng(Path.Combine(outDir, $"stage2_printed_{renderDpi}to{deviceDpi}dpi.png"));

// ---- Stage 3: proposed — render at device DPI with Docnet's transparency
// remover (the fix used in PdfPrinter), no resample ----
var (stage3, _, _) = RenderWithDocnet(pdfPath, deviceDpi / 72.0, flatten: true);
ReportAlpha("stage3 (transparency remover)", stage3);
stage3.SaveAsPng(Path.Combine(outDir, $"stage3_optimized_{deviceDpi}dpi.png"));

// ---- Magnified crops of the same region for side-by-side comparison ----
SaveZoom(stage2, "stage2", outDir);
SaveZoom(stage3, "stage3", outDir);

// ---- Thermal driver simulation: 1-bit threshold (what a 203dpi Zebra
// driver does to the grayscale bitmap it receives) ----
var thermal2 = stage2.Clone(ctx => ctx.BinaryThreshold(0.5f));
var thermal3 = stage3.Clone(ctx => ctx.BinaryThreshold(0.5f));
thermal2.SaveAsPng(Path.Combine(outDir, "stage2_thermal_1bit.png"));
thermal3.SaveAsPng(Path.Combine(outDir, "stage3_thermal_1bit.png"));
SaveTextZoom(thermal2, "stage2_thermal", outDir);
SaveTextZoom(thermal3, "stage3_thermal", outDir);
SaveTextZoom(stage2, "stage2_gray", outDir);
SaveTextZoom(stage3, "stage3_gray", outDir);

// ---- Render-quality A/B at device DPI: flags and threshold variants ----
foreach (var (name, flags) in new (string, Docnet.Core.Models.RenderFlags)[]
{
    ("printing", Docnet.Core.Models.RenderFlags.RenderForPrinting),
    ("lcd", Docnet.Core.Models.RenderFlags.OptimizeTextForLcd),
    ("grayscale", Docnet.Core.Models.RenderFlags.Grayscale),
})
{
    try
    {
        using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(deviceDpi / 72.0));
        using var page = doc.GetPageReader(0);
        var img = Image.LoadPixelData<Bgra32>(
            page.GetImage(new NaiveTransparencyRemover(), flags), page.GetPageWidth(), page.GetPageHeight());
        var bw = img.Clone(ctx => ctx.BinaryThreshold(0.5f));
        SaveTextZoom(bw, $"flag_{name}", outDir);
        img.Dispose(); bw.Dispose();
    }
    catch (Exception ex) { Console.WriteLine($"flag {name}: {ex.Message}"); }
}
// Threshold variants on the plain render (bolder text keeps more AA edge pixels)
foreach (var t in new[] { 0.5f, 0.63f, 0.75f })
{
    var bw = stage3.Clone(ctx => ctx.BinaryThreshold(t));
    SaveTextZoom(bw, $"thresh_{(int)(t * 100)}", outDir);
    bw.Dispose();
}

// Bar-width fidelity: scan a row through the first barcode and report black
// run lengths. Uneven widths after thresholding = scannability risk.
foreach (var fy in new[] { 0.09, 0.10, 0.11, 0.12 })
{
    ReportBarWidths($"stage2 (current path) y={fy}", thermal2, fy);
    ReportBarWidths($"stage3 (direct render) y={fy}", thermal3, fy);
}

Console.WriteLine($"PDF page: {pdfWpt}x{pdfHpt}pt ({pdfWpt / 72.0:0.##}\" x {pdfHpt / 72.0:0.##}\")");
Console.WriteLine($"stage1: {stage1.Width}x{stage1.Height}px @ {renderDpi}dpi");
Console.WriteLine($"stage2: {stage2.Width}x{stage2.Height}px (resampled to {deviceDpi}dpi device grid)");
Console.WriteLine($"stage3: {stage3.Width}x{stage3.Height}px (direct {deviceDpi}dpi render)");
Console.WriteLine($"Output in {outDir}");
return 0;

static (Image<Bgra32> image, double widthPt, double heightPt) RenderWithDocnet(
    string path, double scale, bool flatten = false)
{
    using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));
    using var page = doc.GetPageReader(0);
    var w = page.GetPageWidth();
    var h = page.GetPageHeight();
    // flatten=false mirrors the pre-fix PdfPrinter.cs (BGRA, transparent
    // background); flatten=true mirrors the fixed pipeline.
    var bgra = flatten ? page.GetImage(new NaiveTransparencyRemover()) : page.GetImage();
    var img = Image.LoadPixelData<Bgra32>(bgra, w, h);
    return (img, w / scale, h / scale);
}

static void FlattenOntoWhite(Image<Bgra32> img)
{
    img.ProcessPixelRows(rows =>
    {
        for (var y = 0; y < rows.Height; y++)
        {
            var row = rows.GetRowSpan(y);
            for (var x = 0; x < row.Length; x++)
            {
                var p = row[x];
                var a = p.A / 255.0;
                row[x] = new Bgra32(
                    (byte)(p.B * a + 255 * (1 - a)),
                    (byte)(p.G * a + 255 * (1 - a)),
                    (byte)(p.R * a + 255 * (1 - a)));
            }
        }
    });
}

static void ReportAlpha(string label, Image<Bgra32> img)
{
    long transparent = 0, partial = 0, total = (long)img.Width * img.Height;
    img.ProcessPixelRows(rows =>
    {
        for (var y = 0; y < rows.Height; y++)
        {
            var row = rows.GetRowSpan(y);
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].A == 0) transparent++;
                else if (row[x].A < 255) partial++;
            }
        }
    });
    Console.WriteLine($"{label}: {transparent * 100.0 / total:0.0}% fully transparent px, " +
                      $"{partial * 100.0 / total:0.00}% partial-alpha px");
}

// Crop the top-left quadrant (header area) and magnify 4x with nearest
// neighbor so individual printed dots are visible for comparison.
static void SaveZoom(Image<Bgra32> img, string name, string outDir)
{
    var crop = new Rectangle(0, 0, img.Width / 2, img.Height / 4);
    using var zoom = img.Clone(ctx => ctx
        .Crop(crop)
        .Resize(crop.Width * 4, crop.Height * 4, KnownResamplers.NearestNeighbor));
    zoom.SaveAsPng(Path.Combine(outDir, $"zoom_{name}.png"));
}

static void ReportBarWidths(string label, Image<Bgra32> img, double fracY)
{
    var y = (int)(img.Height * fracY);
    var widths = new List<int>();
    img.ProcessPixelRows(rows =>
    {
        var row = rows.GetRowSpan(y);
        var run = 0;
        for (var x = 0; x < row.Length; x++)
        {
            var black = row[x].R < 128;
            if (black) run++;
            else if (run > 0) { widths.Add(run); run = 0; }
        }
        if (run > 0) widths.Add(run);
    });
    Console.WriteLine($"{label}: {widths.Count} bars at y={y}, " +
                      $"widths [{string.Join(",", widths)}]");
}

// Tight crop around the small-font description line (fractional coords so it
// works at any DPI), magnified 8x.
static void SaveTextZoom(Image<Bgra32> img, string name, string outDir)
{
    var crop = new Rectangle(
        0, (int)(img.Height * 0.145),
        (int)(img.Width * 0.55), (int)(img.Height * 0.035));
    using var zoom = img.Clone(ctx => ctx
        .Crop(crop)
        .Resize(crop.Width * 8, crop.Height * 8, KnownResamplers.NearestNeighbor));
    zoom.SaveAsPng(Path.Combine(outDir, $"textzoom_{name}.png"));
}
