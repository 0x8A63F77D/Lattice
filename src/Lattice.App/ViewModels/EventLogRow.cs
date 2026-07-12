using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

/// <summary>The three filter-pill priorities (design 2c). BOINC's
/// MessagePriority maps Info→Info, UserAlert→Warning, InternalError→Error.</summary>
public enum EventLogPriority
{
    Info,
    Warning,
    Error,
}

/// <summary>Closed holder so XAML can use x:DataType.</summary>
public sealed class EventLogRow(MessageKey key, EventLogRowViewModel data)
    : RowHolder<MessageKey, EventLogRowViewModel>(key, data);

/// <summary>Immutable row projection for one daemon log line.</summary>
public sealed record EventLogRowViewModel(
    string TimestampText,
    string Host,
    string Project,
    string Body,
    EventLogPriority Priority)
{
    public static MessageKey KeyOf(Message msg, Guid hostId) =>
        new(hostId, msg.Seqno, msg.Timestamp?.UtcTicks ?? 0L);

    public static EventLogPriority MapPriority(MessagePriority p) => p switch
    {
        MessagePriority.Info => EventLogPriority.Info,
        MessagePriority.UserAlert => EventLogPriority.Warning,
        MessagePriority.InternalError => EventLogPriority.Error,
        // BOINC's enum is daemon-defined; unknown values degrade to Info
        // rather than crashing the log view on a newer daemon.
        _ => EventLogPriority.Info,
    };

    public static EventLogRowViewModel From(Message msg, Guid hostId, string host) =>
        new(
            TimestampText: msg.Timestamp?.ToLocalTime()
                .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—",
            Host: host,
            Project: msg.Project,
            Body: msg.Body,
            Priority: MapPriority(msg.Priority));
}
