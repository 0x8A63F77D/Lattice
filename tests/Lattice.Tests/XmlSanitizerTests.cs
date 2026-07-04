using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class XmlSanitizerTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Strips_illegal_iso_declaration()
    {
        string sanitized = XmlSanitizer.Sanitize(Fixture("malformed_iso_decl.xml"));
        XElement parsed = XElement.Parse(sanitized); // must not throw
        Assert.Equal("boinc_gui_rpc_reply", parsed.Name.LocalName);
    }

    [Fact]
    public void Strips_control_characters_but_keeps_legal_whitespace()
    {
        string sanitized = XmlSanitizer.Sanitize("<a>\x01" + "hi\x1F" + "\t\r\n</a>");
        Assert.Equal("<a>hi\t\r\n</a>", sanitized);
    }

    [Fact]
    public void Escapes_bare_ampersand()
    {
        string sanitized = XmlSanitizer.Sanitize("<a>Miles & More</a>");
        Assert.Equal("<a>Miles &amp; More</a>", sanitized);
    }

    [Theory]
    [InlineData("<a>a &amp; b</a>")]
    [InlineData("<a>&lt;tag&gt;</a>")]
    [InlineData("<a>&apos;&quot;</a>")]
    [InlineData("<a>&#38;</a>")]
    [InlineData("<a>&#x26;</a>")]
    public void Leaves_valid_entities_untouched(string xml)
    {
        Assert.Equal(xml, XmlSanitizer.Sanitize(xml));
    }

    [Theory]
    [InlineData("<a>&foo;</a>", "<a>&amp;foo;</a>")]
    [InlineData("<a>&nbsp;</a>", "<a>&amp;nbsp;</a>")]
    public void Escapes_undeclared_named_entities(string input, string expected)
    {
        string sanitized = XmlSanitizer.Sanitize(input);
        Assert.Equal(expected, sanitized);
        XElement.Parse(sanitized); // must not throw
    }
}
