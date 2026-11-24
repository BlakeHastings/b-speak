namespace BSpeak.Domain;

public enum SpeechState { Queued, Playing, Finished, Interrupted, Withdrawn }

public sealed record SpeechRules(int MaxLength)
{
    public static SpeechRules Default { get; } = new(600);
}

/// <summary>One thing an agent says, from queued to played (or interrupted, or withdrawn).</summary>
public sealed class Speech : AggregateRoot
{
    public string Id { get; private init; } = "";
    public string Participant { get; private init; } = "";
    public string Text { get; private init; } = "";
    public IReadOnlyList<string> Sentences { get; private init; } = [];
    public int? ReplyTo { get; private init; }
    public SpeechState State { get; private set; }
    public string? Voice { get; private set; }

    /// <summary>Index of the last sentence you fully heard; -1 for none.</summary>
    public int HeardThrough { get; private set; } = -1;

    public string HeardText => string.Join(" ", Sentences.Take(HeardThrough + 1));

    public bool IsDone => State is SpeechState.Finished or SpeechState.Interrupted or SpeechState.Withdrawn;

    private Speech() { }

    public static Speech Queue(string id, string participant, string text, int? replyTo, IRoomView room, SpeechRules rules)
    {
        var name = room.CanonicalName(participant)
            ?? throw new DomainViolation("participant-unknown", $"'{participant}' is not in the room. Join with participant post --name.");
        text = (text ?? "").Trim();
        if (text.Length == 0)
            throw new DomainViolation("speech-empty", "There is nothing to say.");
        if (SpokenText.FindMarkdown(text) is { } found)
            throw new DomainViolation("speech-markdown", $"The speech contains {found}. Say it the way you would out loud.");
        if (text.Length > rules.MaxLength)
            throw new DomainViolation("speech-too-long", $"The speech is {text.Length} characters; the limit is {rules.MaxLength}. Say the headline and offer the detail.");
        if (replyTo is int r)
        {
            if (r < 1 || r > room.UtteranceCount)
                throw new DomainViolation("reply-to-unknown", $"There is no utterance {r}.");
            if (room.LatestRelevantUtterance(name) is int latest && latest > r)
                throw new DomainViolation("conversation-moved-on", $"Utterance {latest} is newer than {r}. Read it before answering.");
        }

        var speech = new Speech
        {
            Id = id, Participant = name, Text = text, Sentences = SpokenText.Sentences(text), ReplyTo = replyTo,
            State = SpeechState.Queued,
        };
        speech.Raise(new SpeechQueued(id, name, text, replyTo));
        return speech;
    }

    /// <summary>Withdraws a speech that has not started playing. Only its own participant may withdraw it.</summary>
    public void Withdraw(string by, WithdrawReason reason)
    {
        if (by != Participant)
            throw new DomainViolation("speech-not-yours", $"Speech {Id} belongs to {Participant}.");
        RequireState(SpeechState.Queued, "withdraw");
        State = SpeechState.Withdrawn;
        Raise(new SpeechWithdrawn(Id, Participant, reason));
    }

    public void Start(string voice)
    {
        RequireState(SpeechState.Queued, "start");
        State = SpeechState.Playing;
        Voice = voice;
        Raise(new PlaybackStarted(Id, Participant, voice));
    }

    /// <summary>The render side finished playing a sentence. Moves <see cref="HeardThrough"/> forward only.</summary>
    public void SentencePlayed(int index)
    {
        if (State != SpeechState.Playing || index < 0 || index >= Sentences.Count) return;
        if (index > HeardThrough) HeardThrough = index;
    }

    public void Finish()
    {
        RequireState(SpeechState.Playing, "finish");
        State = SpeechState.Finished;
        HeardThrough = Sentences.Count - 1;
        Raise(new PlaybackFinished(Id, Participant));
    }

    public void Interrupt()
    {
        RequireState(SpeechState.Playing, "interrupt");
        State = SpeechState.Interrupted;
        Raise(new SpeechInterrupted(Id, Participant, HeardThrough, HeardText));
    }

    private void RequireState(SpeechState expected, string action)
    {
        if (State != expected)
            throw new DomainViolation("state-transition", $"Cannot {action} speech {Id}: it is {State.ToString().ToLowerInvariant()}.");
    }
}
