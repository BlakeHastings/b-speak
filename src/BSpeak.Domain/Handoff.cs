namespace BSpeak.Domain;

public enum HandoffStatus { Open, Working, Done, Failed }

public sealed record HandoffNote(string By, HandoffStatus Status, string? Text);

/// <summary>
/// Work the voice agent passes to a deeper agent (the orchestrator) instead of doing it in the room.
/// The orchestrator claims it (working), may post progress, and closes it with a result (done or failed)
/// written to be spoken. The voice agent hears every update in its inbox.
/// </summary>
public sealed class Handoff : AggregateRoot
{
    private readonly List<HandoffNote> _notes = [];

    public string Id { get; private init; } = "";
    public string From { get; private init; } = "";
    public string Text { get; private init; } = "";
    public int? UtteranceId { get; private init; }
    public HandoffStatus Status { get; private set; }
    public IReadOnlyList<HandoffNote> Notes => _notes;

    public bool IsClosed => Status is HandoffStatus.Done or HandoffStatus.Failed;

    private Handoff() { }

    public static Handoff Post(string id, string from, string text, int? utteranceId, IRoomView room)
    {
        var name = room.CanonicalName(from)
            ?? throw new DomainViolation("participant-unknown", $"'{from}' is not in the room. Join with participant post --name.");
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new DomainViolation("handoff-empty", "Say what needs doing.");
        if (utteranceId is int u && (u < 1 || u > room.UtteranceCount))
            throw new DomainViolation("reply-to-unknown", $"There is no utterance {u}.");

        var handoff = new Handoff { Id = id, From = name, Text = text, UtteranceId = utteranceId, Status = HandoffStatus.Open };
        handoff.Raise(new HandoffPosted(id, name, text, utteranceId));
        return handoff;
    }

    /// <summary>Moves the handoff forward. A closed handoff never changes; closing it requires a result.</summary>
    public void Update(string by, HandoffStatus status, string? note)
    {
        by = string.IsNullOrWhiteSpace(by) ? "orchestrator" : by.Trim();
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (IsClosed)
            throw new DomainViolation("state-transition", $"Handoff {Id} is already {Status.ToString().ToLowerInvariant()}.");
        if (status == HandoffStatus.Open && Status != HandoffStatus.Open)
            throw new DomainViolation("state-transition", $"Handoff {Id} is already being worked on; it cannot reopen.");
        if (status is HandoffStatus.Done or HandoffStatus.Failed && note is null)
            throw new DomainViolation("handoff-result-required", "Closing a handoff needs a result the voice agent can say out loud (--note).");
        if (status == Status && note is null) return;

        Status = status;
        _notes.Add(new HandoffNote(by, status, note));
        Raise(new HandoffUpdated(Id, From, by, status, note));
    }
}
