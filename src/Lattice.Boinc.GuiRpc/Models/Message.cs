using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>One event-log message (get_messages). Seqno is monotonic per daemon.</summary>
public sealed record Message(
    string Project,
    MessagePriority Priority,
    int Seqno,
    DateTimeOffset? Timestamp,
    string Body)
{
    internal static Message Parse(XElement e) => new(
        ParseHelpers.GetString(e, "project"),
        (MessagePriority)ParseHelpers.GetInt(e, "pri"),
        ParseHelpers.GetInt(e, "seqno"),
        ParseHelpers.GetTimestamp(e, "time"),
        ParseHelpers.GetString(e, "body").Trim());
}
