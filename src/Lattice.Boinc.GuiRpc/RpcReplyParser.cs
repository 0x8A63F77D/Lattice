using System.Xml;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

internal static class RpcReplyParser
{
    // throwOnError: false keeps a bare <error> reply as data instead of throwing.
    // Sole use: lookup_account_poll, where the daemon passes the project server's
    // <error> through and it means "lookup failed", not "RPC failed".
    internal static XElement Parse(string raw, bool throwOnUnauthorized = true, bool throwOnError = true)
    {
        XElement reply;
        try
        {
            reply = XElement.Parse(XmlSanitizer.Sanitize(raw), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            string snippet = raw.Length <= 2000 ? raw : raw[..2000];
            throw new BoincProtocolException("RPC reply is not parseable XML.", snippet, ex);
        }

        if (throwOnUnauthorized && reply.Element("unauthorized") is not null)
            throw new BoincUnauthorizedException();
        if (throwOnError && reply.Element("error") is { } error)
            throw new BoincRpcException(((string)error).Trim());

        return reply;
    }
}
