using System.Text;
using System.Text.RegularExpressions;

namespace Lattice.Boinc.GuiRpc;

internal static partial class XmlSanitizer
{
    private const string CdataOpen = "<![CDATA[";
    private const string CdataClose = "]]>";

    /// <summary>
    /// Makes a daemon reply parseable by a real XML parser without altering its data.
    /// Two of the three repairs are MARKUP repairs and must not reach CDATA content:
    /// inside a CDATA section '&amp;' and '&lt;' are literal characters, so escaping
    /// them there would corrupt the payload rather than rescue it. The daemon wraps
    /// every event-log message body in CDATA (client/client_msgs.cpp,
    /// MESSAGE_DESCS::write: "&lt;body&gt;&lt;![CDATA[\n%s\n]]&gt;&lt;/body&gt;"), so
    /// message text is exactly where the distinction bites.
    /// Stripping XML-illegal control characters is safe everywhere, CDATA included —
    /// XML 1.0 forbids them in character data of any kind.
    /// </summary>
    internal static string Sanitize(string raw)
    {
        string text = StripIllegalChars(raw);

        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (true)
        {
            int open = text.IndexOf(CdataOpen, pos, StringComparison.Ordinal);
            if (open < 0)
            {
                AppendMarkup(sb, text[pos..]);
                return sb.ToString();
            }

            AppendMarkup(sb, text[pos..open]);

            int contentStart = open + CdataOpen.Length;
            int close = text.IndexOf(CdataClose, contentStart, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unterminated section: a garbled reply we cannot repair. Pass the
                // remainder through verbatim so XElement.Parse reports it as such.
                sb.Append(text[open..]);
                return sb.ToString();
            }

            // Verbatim: CDATA content is literal by definition.
            sb.Append(text.AsSpan(open, close + CdataClose.Length - open));
            pos = close + CdataClose.Length;
        }
    }

    private static void AppendMarkup(StringBuilder sb, string markup)
    {
        // Some replies carry an illegal mid-document encoding declaration (BOINC bug
        // #1509): the daemon passes a project server's reply through verbatim
        // (client/gui_rpc_server_ops.cpp, handle_lookup_account_poll) and BOINC's own
        // server code prefixes one (html/inc/xml.inc). Matched as the exact literal both
        // BOINC's Android client and its server emit — a variant spelling is unhandled
        // on purpose; see issue #207's open questions.
        markup = markup.Replace("<?xml version=\"1.0\" encoding=\"ISO-8859-1\" ?>", string.Empty);
        sb.Append(BareAmpersand().Replace(markup, "&amp;"));
    }

    private static string StripIllegalChars(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Where(c => c is '\t' or '\n' or '\r' or >= '\x20'))
            sb.Append(c);
        return sb.ToString();
    }

    // Only XML's five predefined entities and numeric references survive; anything
    // else (e.g. "&foo;" in an unescaped message body) would make XElement.Parse throw.
    [GeneratedRegex(@"&(?!(?:amp|lt|gt|apos|quot|#[0-9]+|#x[0-9A-Fa-f]+);)")]
    private static partial Regex BareAmpersand();
}
