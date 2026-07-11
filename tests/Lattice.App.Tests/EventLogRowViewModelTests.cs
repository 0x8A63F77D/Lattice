using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.App.Tests;

public class EventLogRowViewModelTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 4, 14, 32, 8, TimeSpan.Zero);

    [Fact]
    public void Renders_timestamp_host_project_body()
    {
        var msg = new Message("Einstein@Home", MessagePriority.Info, 7, T, "work fetch");
        var row = EventLogRowViewModel.From(msg, Guid.NewGuid(), "host-a");
        Assert.Equal(T.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), row.TimestampText);
        Assert.Equal("host-a", row.Host);
        Assert.Equal("Einstein@Home", row.Project);
        Assert.Equal("work fetch", row.Body);
        Assert.Equal(EventLogPriority.Info, row.Priority);
    }

    [Fact]
    public void Null_timestamp_renders_as_dash()
    {
        var msg = new Message("", MessagePriority.Info, 3, null, "b");
        var row = EventLogRowViewModel.From(msg, Guid.NewGuid(), "h");
        Assert.Equal("—", row.TimestampText);
    }

    [Fact]
    public void Priority_maps_user_alert_to_warning_and_internal_error_to_error()
    {
        Assert.Equal(EventLogPriority.Warning, EventLogRowViewModel.MapPriority(MessagePriority.UserAlert));
        Assert.Equal(EventLogPriority.Error, EventLogRowViewModel.MapPriority(MessagePriority.InternalError));
    }

    [Fact]
    public void Key_carries_identity_with_null_timestamp_as_zero_ticks()
    {
        var hostId = Guid.NewGuid();
        var msg = new Message("", MessagePriority.Info, 3, null, "b");
        var key = EventLogRowViewModel.KeyOf(msg, hostId);
        Assert.Equal(hostId, key.HostId);
        Assert.Equal(3, key.Seqno);
        Assert.Equal(0L, key.TimestampTicks);
    }
}
