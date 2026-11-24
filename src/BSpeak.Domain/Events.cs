namespace BSpeak.Domain;

public enum LeaveReason { Left, TimedOut }

public enum DiscardReason
{
    /// <summary>No words in the transcript (noise, a cough).</summary>
    Empty,

    /// <summary>Listening is paused; only the resume phrase is accepted.</summary>
    Paused,

    /// <summary>Wake-word mode, and the utterance did not start with the wake word.</summary>
    NoWakeWord,

    /// <summary>A control phrase that changed nothing, for example "start listening" while already listening.</summary>
    Control,
}

public enum WithdrawReason
{
    Withdrawn,
    ParticipantLeft,

    /// <summary>Text to speech or the audio device failed before any of it played.</summary>
    PlaybackFailed,
}

// Room
public sealed record ParticipantJoined(string Name, string Voice) : DomainEvent { public override string Stream => "room"; }
public sealed record ParticipantLeft(string Name, LeaveReason Reason) : DomainEvent { public override string Stream => "room"; }
public sealed record ListeningPaused(string By) : DomainEvent { public override string Stream => "room"; }
public sealed record ListeningResumed(string By) : DomainEvent { public override string Stream => "room"; }
public sealed record ListeningModeChanged(ListeningMode Mode, string? WakeWord) : DomainEvent { public override string Stream => "room"; }
public sealed record UserStartedSpeaking : DomainEvent { public override string Stream => "room"; }
public sealed record UserSpoke(int UtteranceId, string Text, string? AddressedTo, string? Focus) : DomainEvent { public override string Stream => "room"; }
public sealed record UtteranceDiscarded(DiscardReason Reason) : DomainEvent { public override string Stream => "room"; }
public sealed record FloorGranted(string SpeechId, string Participant) : DomainEvent { public override string Stream => "room"; }

// Speech
public sealed record SpeechQueued(string SpeechId, string Participant, string Text, int? ReplyTo) : DomainEvent { public override string Stream => $"speech:{SpeechId}"; }
public sealed record PlaybackStarted(string SpeechId, string Participant, string Voice) : DomainEvent { public override string Stream => $"speech:{SpeechId}"; }
public sealed record PlaybackFinished(string SpeechId, string Participant) : DomainEvent { public override string Stream => $"speech:{SpeechId}"; }
public sealed record SpeechInterrupted(string SpeechId, string Participant, int HeardThrough, string HeardText) : DomainEvent { public override string Stream => $"speech:{SpeechId}"; }
public sealed record SpeechWithdrawn(string SpeechId, string Participant, WithdrawReason Reason) : DomainEvent { public override string Stream => $"speech:{SpeechId}"; }
