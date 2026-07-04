using System.Xml;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

internal static class RpcReplyParser
{
    internal static XElement Parse(string raw, bool throwOnUnauthorized = true)
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
        if (reply.Element("error") is { } error)
            throw new BoincRpcException(((string)error).Trim());

        return reply;
    }
}
