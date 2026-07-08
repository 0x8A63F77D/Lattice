using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>Thread-safe ring buffer of daemon messages, capped at a fixed capacity.</summary>
internal sealed class MessageLog(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<Message> _messages = new();

    /// <summary>Appends messages in order, evicting the oldest beyond capacity.</summary>
    public void Append(IReadOnlyList<Message> messages)
    {
        lock (_gate)
        {
            foreach (Message message in messages)
                _messages.Enqueue(message);
            while (_messages.Count > capacity)
                _messages.Dequeue();
        }
    }

    /// <summary>Returns a copy of the current contents, oldest first.</summary>
    public IReadOnlyList<Message> Snapshot()
    {
        lock (_gate)
            return [.. _messages];
    }

    /// <summary>
    /// Atomically replaces the entire retained buffer with <paramref name="items"/>
    /// (capped to capacity, keeping the newest). Readers see either the old content
    /// or the new — never an intermediate empty state.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<Message> items)
    {
        lock (_gate)
        {
            _messages.Clear();
            int skip = Math.Max(0, items.Count - capacity);
            for (int i = skip; i < items.Count; i++)
                _messages.Enqueue(items[i]);
        }
    }
}
