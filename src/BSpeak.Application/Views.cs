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
/// <param name="Context">Who was heard last and how long ago, for deciding whether to open with a micro introduction.</param>
public sealed record InboxBatch(IReadOnlyList<InboxItem> Items, long Next, bool TimedOut, RoomContext? Context = null);

/// <param name="LastHeard">"user" or the agent heard most recently; null when nobody has spoken yet.</param>
/// <param name="YouLastSpokeSecondsAgo">When you last finished (or were cut off) speaking; null if you have not spoken.</param>
/// <param name="LastSpokeToYouSecondsAgo">When the user last said something meant for you.</param>
/// <param name="Turns">open: speak when the floor is free. hands-up: a new speech raises your hand until the user calls on you (direct replies skip the line).</param>
/// <param name="YourHand">The speech id of your raised hand, if it is up. Keep it current with speech patch, or lower it with speech delete.</param>
public sealed record RoomContext(string? LastHeard, int? LastHeardSecondsAgo, int? YouLastSpokeSecondsAgo, int? LastSpokeToYouSecondsAgo,
    TurnMode Turns = TurnMode.Open, string? YourHand = null);

/// <summary>A raised hand: who, and what they will say when called on.</summary>
public sealed record HandView(string SpeechId, string Participant, string Text);

public sealed record TurnsView(TurnMode Mode, IReadOnlyList<HandView> Hands);

/// <param name="ForYou">Addressed to you, to everyone, or unaddressed while you held focus.</param>
public sealed record UtteranceView(int Id, string Text, string? AddressedTo, string? Focus, bool ForYou, DateTimeOffset At);

public sealed record UtteranceBatch(IReadOnlyList<UtteranceView> Utterances, bool TimedOut);

/// <param name="HandUp">The speech is a raised hand, waiting to be called on.</param>
public sealed record SpeechView(string Id, string Participant, string Text, SpeechState State, int? ReplyTo, int HeardThrough, string HeardText, bool HandUp = false);

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
    int Utterances,
    TurnMode Turns,
    IReadOnlyList<FloorSlot> Hands);
