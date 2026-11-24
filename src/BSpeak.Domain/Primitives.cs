namespace BSpeak.Domain;

/// <summary>A command broke a domain rule. <see cref="Code"/> is stable and machine-readable.</summary>
public sealed class DomainViolation(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Something that happened. <see cref="Stream"/> names the aggregate instance it belongs to.</summary>
public abstract record DomainEvent
{
    public abstract string Stream { get; }

    /// <summary>Event type name as stored and shown, for example "UserSpoke".</summary>
    public string Type => GetType().Name;
}

/// <summary>Base for aggregates: collects the events a command produced until the application takes them.</summary>
public abstract class AggregateRoot
{
    private readonly List<DomainEvent> _pending = [];

    public IReadOnlyList<DomainEvent> PendingEvents => _pending;

    protected void Raise(DomainEvent e) => _pending.Add(e);

    /// <summary>Returns the pending events and clears them.</summary>
    public IReadOnlyList<DomainEvent> TakeEvents()
    {
        var events = _pending.ToArray();
        _pending.Clear();
        return events;
    }
}
