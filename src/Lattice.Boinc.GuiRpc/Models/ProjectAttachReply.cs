using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Reply to project_attach_poll (&lt;project_attach_reply&gt;). ErrorNum 0 means the
/// daemon accepted the attach and created the project entry — it does NOT verify the
/// authenticator, which the daemon only checks on its first scheduler RPC. Messages
/// are the daemon's display-only attach messages, in reply order.
/// </summary>
public sealed record ProjectAttachReply(
    int ErrorNum,
    IReadOnlyList<string> Messages)
{
    internal static ProjectAttachReply Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "error_num"),
        [.. e.Elements("message").Select(m => (string)m)]);
}
