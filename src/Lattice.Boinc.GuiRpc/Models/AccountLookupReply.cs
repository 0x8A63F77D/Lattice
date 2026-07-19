using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Reply to lookup_account_poll (&lt;account_out&gt;). ErrorNum semantics:
/// 0 = success and <see cref="Authenticator"/> carries the account key;
/// <see cref="BoincErrorCodes.InProgress"/> / <see cref="BoincErrorCodes.Retry"/> = keep polling;
/// -1 = the project server's raw &lt;error&gt; reply passed through by the daemon
/// (<see cref="ErrorMessage"/> carries its text; -1 mirrors upstream's generic-failure
/// return in RPC::parse_reply, lib/gui_rpc_client.cpp — not a Lattice invention);
/// any other negative value is a BOINC error code (lib/error_numbers.h).
/// ErrorMessage is display-only — never branch on it.
/// </summary>
public sealed record AccountLookupReply(
    int ErrorNum,
    string ErrorMessage,
    string Authenticator)
{
    internal static AccountLookupReply Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "error_num"),
        ParseHelpers.GetString(e, "error_msg"),
        ParseHelpers.GetString(e, "authenticator"));
}
