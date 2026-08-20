using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator.Core.Tests;

public class QueueNamingTests
{
    [Fact]
    public void ForPrinter_matches_documented_scheme()
    {
        // From README: WAREHOUSE-PC1 + "Zebra ZP500" -> cloudprint-warehouse-pc1-zebra-zp500
        Assert.Equal("cloudprint-warehouse-pc1-zebra-zp500",
            QueueNaming.ForPrinter("WAREHOUSE-PC1", "Zebra ZP500"));
    }

    [Fact]
    public void ForDevice_matches_sample_scheme()
    {
        Assert.Equal("cloudprint-shipping-pc-01-scale-shipping",
            QueueNaming.ForDevice("shipping-pc-01", "scale-shipping"));
    }

    [Fact]
    public void Sanitizes_and_lowercases_and_trims_trailing_hyphens()
    {
        // Runs of non-alphanumeric chars collapse to a single '-', trailing hyphens are trimmed.
        Assert.Equal("cloudprint-host-a-b-c",
            QueueNaming.ForPrinter("HOST", "A_b c--"));
    }

    [Fact]
    public void Sanitizes_the_host_half_too()
    {
        Assert.Equal("cloudprint-kevin-s-pc-zebra",
            QueueNaming.ForPrinter("Kevin's PC", "Zebra"));
    }

    [Fact]
    public void Name_is_strictly_alphanumeric_and_single_dashes()
    {
        var name = QueueNaming.ForPrinter("DESKTOP-1234", "HP LaserJet Pro (Copy #2) — Übergroß");
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name);
        Assert.Equal("cloudprint-desktop-1234-hp-laserjet-pro-copy-2-bergro", name);
    }

    [Fact]
    public void Sanitize_makes_tag_safe_values()
    {
        Assert.Equal("hp-laserjet-copy-1", QueueNaming.Sanitize("HP LaserJet (Copy 1)"));
        Assert.Equal("", QueueNaming.Sanitize("()!"));
    }

    [Fact]
    public void Empty_name_part_does_not_leave_double_or_trailing_dashes()
    {
        Assert.Equal("cloudprint-host", QueueNaming.ForPrinter("host", "()"));
    }

    [Fact]
    public void Caps_length_to_leave_room_for_dlq_suffix()
    {
        var name = QueueNaming.ForPrinter("host", new string('x', 200));
        Assert.True(name.Length <= QueueNaming.MaxLength);
        Assert.True(QueueNaming.DeadLetter(name).Length <= 80);
    }

    [Fact]
    public void DeadLetter_appends_suffix()
    {
        Assert.Equal("cloudprint-host-printer-dlq",
            QueueNaming.DeadLetter("cloudprint-host-printer"));
    }

    [Fact]
    public void PrefixFor_lowercases_with_trailing_hyphen()
    {
        Assert.Equal("cloudprint-warehouse-pc1-", QueueNaming.PrefixFor("WAREHOUSE-PC1"));
        Assert.Equal("cloudprint-kevin-s-pc-", QueueNaming.PrefixFor("Kevin's PC"));
    }
}
