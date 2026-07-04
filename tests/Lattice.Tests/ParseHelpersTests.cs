using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class ParseHelpersTests
{
    private static XElement El(string xml) => XElement.Parse(xml);

    [Fact]
    public void GetString_missing_returns_empty() =>
        Assert.Equal("", ParseHelpers.GetString(El("<r/>"), "name"));

    [Fact]
    public void GetString_present_returns_value() =>
        Assert.Equal("boinc", ParseHelpers.GetString(El("<r><name>boinc</name></r>"), "name"));

    [Theory]
    [InlineData("<r/>", false)]                       // missing
    [InlineData("<r><f/></r>", true)]                 // empty tag = true (BOINC dialect)
    [InlineData("<r><f>1</f></r>", true)]
    [InlineData("<r><f>true</f></r>", true)]
    [InlineData("<r><f>0</f></r>", false)]
    [InlineData("<r><f>false</f></r>", false)]
    public void GetBool_covers_boinc_dialect(string xml, bool expected) =>
        Assert.Equal(expected, ParseHelpers.GetBool(El(xml), "f"));

    [Fact]
    public void GetInt_missing_returns_default() =>
        Assert.Equal(7, ParseHelpers.GetInt(El("<r/>"), "n", 7));

    [Fact]
    public void GetDouble_ignores_culture() =>
        Assert.Equal(1.5, ParseHelpers.GetDouble(El("<r><n>1.5</n></r>"), "n"));

    [Fact]
    public void GetTimestamp_converts_epoch_seconds()
    {
        DateTimeOffset? t = ParseHelpers.GetTimestamp(El("<r><time>1751600000.0</time></r>"), "time");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), t);
    }

    [Theory]
    [InlineData("<r/>")]
    [InlineData("<r><time>0</time></r>")]
    public void GetTimestamp_missing_or_zero_returns_null(string xml) =>
        Assert.Null(ParseHelpers.GetTimestamp(El(xml), "time"));
}

public class VersionInfoTests
{
    [Fact]
    public void Parses_and_formats()
    {
        var e = XElement.Parse("<server_version><major>8</major><minor>0</minor><release>4</release></server_version>");
        var v = VersionInfo.Parse(e);
        Assert.Equal(new VersionInfo(8, 0, 4), v);
        Assert.Equal("8.0.4", v.ToString());
    }
}
