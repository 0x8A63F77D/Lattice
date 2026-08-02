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

    /// <summary>
    /// Returns a reply's payload container, which every op's handler is contractually
    /// required to emit (client/gui_rpc_server_ops.cpp: each handler unconditionally
    /// prints its own container tag — an idle host still sends an empty
    /// &lt;results&gt;&lt;/results&gt;).
    /// Absence is therefore contract-breaking and is reported as such rather than
    /// falling back to the reply root. That fallback fabricated data: parsing a
    /// containerless reply as a record yields an all-default one, so a missing
    /// &lt;project_attach_reply&gt; read as error_num 0 — "the attach succeeded".
    /// </summary>
    internal static XElement RequireContainer(XElement reply, string name, string op) =>
        reply.Element(name)
            ?? throw new BoincProtocolException($"{op} reply is missing <{name}>.", reply.ToString());
}
