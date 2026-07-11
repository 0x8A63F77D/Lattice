using System.Reflection;
using Lattice.Core;

namespace Lattice.App.Tests.Fakes;

/// <summary>
/// HostStore forwards HostMonitorManager.MessagesAdded as MessagesReceived. Real
/// polling couples that event with a SnapshotUpdated (which DOES raise Changed)
/// on every tick, so the forwarding contract — Find guard, disposed guard, and
/// the Changed decoupling — and any message-only VM behaviour can only be
/// exercised in isolation by raising the manager's event on its own. The event
/// is public but only its declaring type may invoke it, so the test reaches the
/// compiler's field-like backing delegate by reflection. This keeps the
/// message-only cases fully synchronous and deterministic.
/// </summary>
internal static class ManagerTestAccess
{
    public static void RaiseMessagesAdded(HostMonitorManager manager, MessagesAddedEventArgs args)
    {
        FieldInfo field = typeof(HostMonitorManager)
            .GetField(nameof(HostMonitorManager.MessagesAdded), BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = (EventHandler<MessagesAddedEventArgs>?)field.GetValue(manager);
        handler?.Invoke(manager, args);
    }
}
