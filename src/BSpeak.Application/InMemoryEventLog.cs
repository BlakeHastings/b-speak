using BSpeak.Domain;

namespace BSpeak.Application;

/// <summary>The v1 event log: in memory, trimmed to the most recent events. A host restart clears it.</summary>
public sealed class InMemoryEventLog(int retain = 5000) : IEventLog
{
    private readonly object _gate = new();
    private readonly List<StoredEvent> _events = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _head;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Head { get { lock (_gate) return _head; } }

    public Task Changed { get { lock (_gate) return _changed.Task; } }

    public StoredEvent Append(DomainEvent e, DateTimeOffset at)
    {
        StoredEvent stored;
        TaskCompletionSource signal;
        lock (_gate)
        {
            stored = new StoredEvent(++_head, at, e);
            _events.Add(stored);
            if (_events.Count > retain) _events.RemoveRange(0, _events.Count - retain);
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
        return stored;
    }

    public IReadOnlyList<StoredEvent> After(long seq, int max = 1000)
    {
        lock (_gate)
        {
            var start = _events.FindIndex(e => e.Seq > seq);
            if (start < 0) return [];
            return _events.GetRange(start, Math.Min(max, _events.Count - start));
        }
    }
}
