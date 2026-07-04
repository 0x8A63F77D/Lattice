using System.Text;
using System.Text.RegularExpressions;

namespace Lattice.Boinc.GuiRpc;

internal static partial class XmlSanitizer
{
    internal static string Sanitize(string raw)
    {
        // Some RPCs emit an illegal mid-document encoding declaration (BOINC bug #1509).
        raw = raw.Replace("<?xml version=\"1.0\" encoding=\"ISO-8859-1\" ?>", string.Empty);

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            if (c == '\t' || c == '\n' || c == '\r' || c >= '\x20')
                sb.Append(c);

        return BareAmpersand().Replace(sb.ToString(), "&amp;");
    }

    [GeneratedRegex(@"&(?!(?:[A-Za-z][A-Za-z0-9]*|#[0-9]+|#x[0-9A-Fa-f]+);)")]
    private static partial Regex BareAmpersand();
}
