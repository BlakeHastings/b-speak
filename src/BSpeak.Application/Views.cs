using BSpeak.Domain;

namespace BSpeak.Application;

/// <param name="After">Pass this to the first utterance wait so the new participant does not receive history.</param>
/// <param name="InboxAfter">Pass this to the first inbox wait.</param>
public sealed record JoinResult(string Name, string Voice, int After, long InboxAfter);

public sealed record HandoffView(string Id, string From, string Text, int? UtteranceId, HandoffStatus Status, IReadOnlyList<HandoffNote> Notes);

/// <summary>One thing in a voice agent's inbox. <see cref="Kind"/> is utterance, handoff, speech-interrupted, speech-withdrawn or speech-failed.</summary>
public sealed record InboxItem(long Seq, string Kind, DateTimeOffset At)
{
    public int? UtteranceId { get; init; }
    public string? Text { get; init; }
    public string? AddressedTo { get; init; }
    public string? Focus { get; init; }
    public bool? ForYou { get; init; }
    public string? HandoffId { get; init; }
    public HandoffStatus? Status { get; init; }
    public string? By { get; init; }
    public string? SpeechId { get; init; }

    /// <summary>For speech-interrupted: the part the user never heard (Text is the part they did).</summary>
    public string? Unheard { get; init; }

    /// <summary>For speech-withdrawn: interrupted (cut off before it started) or user-spoke (the user said something new first).</summary>
    public string? Reason { get; init; }
}

/// <param name="Next">Pass back as --after on the next inbox wait.</param>
public sealed record InboxBatch(IReadOnlyList<InboxItem> Items, long Next, bool TimedOut);

/// <param name="ForYou">Addressed to you, to everyone, or unaddressed while you held focus.</param>
public sealed record UtteranceView(int Id, string Text, string? AddressedTo, string? Focus, bool ForYou, DateTimeOffset At);

public sealed record UtteranceBatch(IReadOnlyList<UtteranceView> Utterances, bool TimedOut);

public sealed record SpeechView(string Id, string Participant, string Text, SpeechState State, int? ReplyTo, int HeardThrough, string HeardText);

/// <param name="Waiting">True while the participant has an utterance wait open.</param>
public sealed record ParticipantView(string Name, string Voice, DateTimeOffset LastSeen, bool Waiting);

public sealed record RoomStatus(
    IReadOnlyList<ParticipantView> Participants,
    bool Listening,
    ListeningMode Mode,
    string? WakeWord,
    string? Focus,
    bool UserSpeaking,
    FloorSlot? Floor,
    IReadOnlyList<FloorSlot> Queue,
    int Utterances);
