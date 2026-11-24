using BSpeak.Domain;

namespace BSpeak.Application;

/// <param name="After">Pass this to the first utterance wait so the new participant does not receive history.</param>
public sealed record JoinResult(string Name, string Voice, int After);

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
