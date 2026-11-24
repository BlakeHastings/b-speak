namespace BSpeak.Domain;

public enum ListeningMode { Always, WakeWord }

/// <summary>Open: agents speak in turn as soon as the floor is free. HandsUp: they raise a hand and wait to be called on.</summary>
public enum TurnMode { Open, HandsUp }

/// <param name="UserGap">How long after you stop speaking, or after a speech ends, before the floor goes to the next agent.</param>
/// <param name="IdleTimeout">How long a participant may go without calling in before it is removed.</param>
/// <param name="Voices">TTS voices handed out to participants in order.</param>
public sealed record RoomSettings(TimeSpan UserGap, TimeSpan IdleTimeout, IReadOnlyList<string> Voices, ControlPhrases ControlPhrases)
{
    public static RoomSettings Default { get; } = new(
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMinutes(30),
        ["am_michael", "af_heart", "bf_emma", "am_fenrir", "af_nicole", "bm_george"],
        ControlPhrases.Default);
}

public sealed class Participant(string name, string voice, DateTimeOffset lastSeen)
{
    public string Name { get; } = name;
    public string Voice { get; internal set; } = voice;
    public DateTimeOffset LastSeen { get; internal set; } = lastSeen;
}

/// <summary>A speech waiting for, or holding, the floor.</summary>
public sealed record FloorSlot(string SpeechId, string Participant);

/// <summary>What <see cref="Speech"/> needs to know about the room to accept a new speech.</summary>
public interface IRoomView
{
    /// <summary>The participant's name as it joined, or null when nobody by that name is in the room.</summary>
    string? CanonicalName(string name);

    int UtteranceCount { get; }

    /// <summary>The newest utterance meant for this participant: addressed to it, to everyone, or unaddressed while it held focus.</summary>
    int? LatestRelevantUtterance(string participant);
}

/// <summary>
/// The room shared by you and every agent on this machine: who is in it, who holds the floor,
/// whether listening is on, and who the conversation is with (focus). One per host.
/// </summary>
public sealed class Room(RoomSettings? settings = null) : AggregateRoot, IRoomView
{
    private readonly List<Participant> _participants = [];
    private readonly List<FloorSlot> _queue = [];
    private readonly List<FloorSlot> _hands = [];
    private readonly Dictionary<string, int> _lastRelevant = new(StringComparer.Ordinal);
    private int _voiceCursor;
    private DateTimeOffset? _floorFreedAt;

    public RoomSettings Settings { get; } = settings ?? RoomSettings.Default;
    public IReadOnlyList<Participant> Participants => _participants;

    /// <summary>The participant the conversation is with when an utterance names nobody.</summary>
    public string? Focus { get; private set; }

    public bool Listening { get; private set; } = true;
    public ListeningMode Mode { get; private set; } = ListeningMode.Always;
    public string? WakeWord { get; private set; }
    public bool UserSpeaking { get; private set; }
    public DateTimeOffset? UserStoppedAt { get; private set; }
    public int UtteranceCount { get; private set; }
    public FloorSlot? FloorHolder { get; private set; }
    public IReadOnlyList<FloorSlot> FloorQueue => _queue;

    public TurnMode TurnMode { get; private set; } = TurnMode.Open;

    /// <summary>Raised hands in the order they went up. Each waits to be called on; none is granted the floor by itself.</summary>
    public IReadOnlyList<FloorSlot> Hands => _hands;

    // ---------- participants

    /// <param name="preferredVoice">A voice this name used before; taken when no one else has it.</param>
    public Participant Join(string name, DateTimeOffset now, string? preferredVoice = null)
    {
        name = ParticipantNames.Require(name, Settings.ControlPhrases);
        foreach (var p in _participants)
        {
            if (Words.Normalize(p.Name) == Words.Normalize(name))
                throw new DomainViolation("name-duplicate", $"{p.Name} is already in the room.");
            if (Words.SoundsAlike(p.Name, name))
                throw new DomainViolation("name-similar", $"{name} sounds too much like {p.Name}. Pick a name that is easy to tell apart by ear.");
        }
        var voice = !string.IsNullOrWhiteSpace(preferredVoice) && _participants.All(p => !p.Voice.Equals(preferredVoice, StringComparison.OrdinalIgnoreCase))
            ? preferredVoice.Trim()
            : NextVoice();
        var participant = new Participant(name, voice, now);
        _participants.Add(participant);
        Raise(new ParticipantJoined(participant.Name, participant.Voice));
        return participant;
    }

    private string NextVoice()
    {
        var voices = Settings.Voices;
        if (voices.Count == 0) return "default";
        var free = voices.FirstOrDefault(v => _participants.All(p => p.Voice != v));
        return free ?? voices[_voiceCursor++ % voices.Count];
    }

    public void Leave(string name, LeaveReason reason = LeaveReason.Left)
    {
        var p = Require(name);
        _participants.Remove(p);
        _lastRelevant.Remove(p.Name);
        if (Focus == p.Name) Focus = null;
        Raise(new ParticipantLeft(p.Name, reason));
    }

    /// <summary>Records that a participant called in. Throws participant-unknown when it is not in the room.</summary>
    public Participant Touch(string name, DateTimeOffset now)
    {
        var p = Require(name);
        p.LastSeen = now;
        return p;
    }

    /// <summary>Removes participants that have been silent past the idle timeout, unless <paramref name="isActive"/> says otherwise.</summary>
    public IReadOnlyList<string> ExpireIdle(DateTimeOffset now, Func<string, bool> isActive)
    {
        var idle = _participants
            .Where(p => !isActive(p.Name) && now - p.LastSeen > Settings.IdleTimeout)
            .Select(p => p.Name).ToList();
        foreach (var name in idle) Leave(name, LeaveReason.TimedOut);
        return idle;
    }

    /// <summary>
    /// Gives a participant a different voice. It must be one the speaking engine has (when <paramref name="available"/>
    /// is known) and no one else in the room may be using it, so agents stay easy to tell apart.
    /// </summary>
    public void ChangeVoice(string name, string voice, IReadOnlyCollection<string>? available)
    {
        var p = Require(name);
        voice = (voice ?? "").Trim();
        if (voice.Length == 0) throw new DomainViolation("voice-unknown", "Say which voice. List them with: bspeak voice list");
        if (available is not null)
        {
            var match = available.FirstOrDefault(v => v.Equals(voice, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainViolation("voice-unknown", $"There is no voice '{voice}'. List them with: bspeak voice list");
            voice = match;
        }
        var holder = _participants.FirstOrDefault(x => x != p && x.Voice.Equals(voice, StringComparison.OrdinalIgnoreCase));
        if (holder is not null) throw new DomainViolation("voice-taken", $"{holder.Name} is using {voice}. Pick another so you are easy to tell apart.");
        if (p.Voice.Equals(voice, StringComparison.Ordinal)) return;
        p.Voice = voice;
        Raise(new VoiceChanged(p.Name, voice));
    }

    // ---------- listening

    public void PauseListening(string by)
    {
        if (!Listening) return;
        Listening = false;
        Raise(new ListeningPaused(by));
    }

    public void ResumeListening(string by)
    {
        if (Listening) return;
        Listening = true;
        Raise(new ListeningResumed(by));
    }

    public void SetMode(ListeningMode mode, string? wakeWord)
    {
        if (!Enum.IsDefined(mode)) throw new DomainViolation("mode-invalid", "Mode must be always or wake-word.");
        wakeWord = string.IsNullOrWhiteSpace(wakeWord) ? null : wakeWord.Trim();
        if (mode == ListeningMode.WakeWord && (wakeWord is null || Words.Split(wakeWord).Length == 0))
            throw new DomainViolation("wake-word-required", "wake-word mode needs a wake word, for example \"hey room\".");
        if (mode == ListeningMode.Always) wakeWord = null;
        if (mode == Mode && wakeWord == WakeWord) return;
        Mode = mode;
        WakeWord = wakeWord;
        Raise(new ListeningModeChanged(mode, wakeWord));
    }

    // ---------- what you say

    /// <summary>Voice detection heard speech start. Raised before any transcript, so barge-in is instant.</summary>
    public void BeginUserTurn(DateTimeOffset now)
    {
        if (UserSpeaking) return;
        UserSpeaking = true;
        Raise(new UserStartedSpeaking());
    }

    /// <summary>
    /// A finished utterance from speech to text. It becomes exactly one of: a control action,
    /// <see cref="UserSpoke"/> addressed by the resolver, or <see cref="UtteranceDiscarded"/>.
    /// </summary>
    public void RecordUtterance(string text, DateTimeOffset now)
    {
        UserSpeaking = false;
        UserStoppedAt = now;
        text = (text ?? "").Trim();

        if (Words.Normalize(text).Length == 0) { Raise(new UtteranceDiscarded(DiscardReason.Empty)); return; }

        var control = Settings.ControlPhrases.Match(text);
        if (!Listening)
        {
            if (control == ControlAction.Resume) ResumeListening("voice");
            else Raise(new UtteranceDiscarded(DiscardReason.Paused));
            return;
        }
        if (control == ControlAction.Pause) { PauseListening("voice"); return; }
        if (control == ControlAction.Resume) { Raise(new UtteranceDiscarded(DiscardReason.Control)); return; }

        if (Mode == ListeningMode.WakeWord)
        {
            if (!Words.StartsWith(text, WakeWord!, out var rest) || Words.Normalize(rest).Length == 0)
            {
                Raise(new UtteranceDiscarded(DiscardReason.NoWakeWord));
                return;
            }
            text = rest;
        }

        switch (TurnPhrases.Match(text))
        {
            case TurnAction.HandsUp:
                if (TurnMode == TurnMode.HandsUp) Raise(new UtteranceDiscarded(DiscardReason.Control));
                else SetTurnMode(TurnMode.HandsUp, "voice");
                return;
            case TurnAction.OpenFloor:
                if (TurnMode == TurnMode.Open) Raise(new UtteranceDiscarded(DiscardReason.Control));
                else SetTurnMode(TurnMode.Open, "voice");
                return;
            case TurnAction.AskHands:
                Raise(new HandsAsked());
                return;
            case TurnAction.Next when _hands.Count > 0:
                CallOn(null, "voice");
                return;
        }

        var addressed = AddressResolver.Resolve(text, _participants.Select(p => p.Name));
        if (addressed is not null && _hands.Any(h => h.Participant == addressed) && TurnPhrases.IsGoAhead(text, addressed))
        {
            CallOn(addressed, "voice");
            return;
        }
        if (addressed is not null && addressed != AddressResolver.Everyone) Focus = addressed;

        var id = ++UtteranceCount;
        if (addressed == AddressResolver.Everyone)
            foreach (var p in _participants) _lastRelevant[p.Name] = id;
        else if (addressed is not null) _lastRelevant[addressed] = id;
        else if (Focus is not null) _lastRelevant[Focus] = id;

        Raise(new UserSpoke(id, text, addressed, Focus));
    }

    // ---------- the floor

    /// <summary>
    /// A reply to the newest thing said to this participant (addressed to it, to everyone, or while it held focus).
    /// Direct replies skip the line in hands-up mode: you asked, so the answer needs no hand.
    /// </summary>
    public bool IsDirectReply(string participant, int? replyTo) =>
        replyTo is int r && Find(participant) is { } p && _lastRelevant.TryGetValue(p.Name, out var latest) && latest == r;

    /// <summary>
    /// Whether a new speech from this participant raises a hand rather than joining the line: true in hands-up
    /// mode unless it is a direct reply. Throws hand-already-up when it would raise a second hand.
    /// </summary>
    public bool RaisesHand(string participant, int? replyTo)
    {
        var p = Require(participant);
        if (TurnMode != TurnMode.HandsUp || IsDirectReply(p.Name, replyTo)) return false;
        if (_hands.FirstOrDefault(h => h.Participant == p.Name) is { } hand)
            throw new DomainViolation("hand-already-up",
                $"Your hand is already up with {hand.SpeechId}. Change what you will say with speech patch {hand.SpeechId}, or lower your hand with speech delete {hand.SpeechId}.");
        return true;
    }

    /// <param name="asHand">Raise a hand and wait to be called on (see <see cref="RaisesHand"/>) instead of joining the line.</param>
    public void RequestFloor(string speechId, string participant, bool asHand = false)
    {
        var p = Require(participant);
        if (FloorHolder?.SpeechId == speechId || _queue.Any(s => s.SpeechId == speechId) || _hands.Any(s => s.SpeechId == speechId)) return;
        if (asHand)
        {
            _hands.Add(new FloorSlot(speechId, p.Name));
            Raise(new HandRaised(speechId, p.Name));
        }
        else _queue.Add(new FloorSlot(speechId, p.Name));
    }

    public bool IsHand(string speechId) => _hands.Any(h => h.SpeechId == speechId);

    public bool IsGranted(string speechId) => FloorHolder?.SpeechId == speechId;

    /// <summary>
    /// Calls on a raised hand: the named participant's, or the one that went up first. It moves to the front of
    /// the line and plays as soon as the floor is free.
    /// </summary>
    public FloorSlot CallOn(string? participant, string by)
    {
        if (_hands.Count == 0) throw new DomainViolation("no-hands", "Nobody has a hand up.");
        var slot = participant is null
            ? _hands[0]
            : _hands.FirstOrDefault(h => h.Participant == Require(participant).Name)
              ?? throw new DomainViolation("hand-not-up", $"{Require(participant).Name} does not have a hand up.");
        _hands.Remove(slot);
        _queue.Insert(0, slot);
        Raise(new HandCalledOn(slot.SpeechId, slot.Participant, by));
        return slot;
    }

    /// <summary>
    /// Switches between an open floor and hands up. Going back to an open floor lowers every hand into the line,
    /// in the order they went up, so nothing anyone was waiting to say is lost.
    /// </summary>
    public void SetTurnMode(TurnMode mode, string by)
    {
        if (!Enum.IsDefined(mode)) throw new DomainViolation("turn-mode-invalid", "Turn mode must be open or hands-up.");
        if (mode == TurnMode) return;
        if (mode == TurnMode.Open)
        {
            _queue.AddRange(_hands);
            _hands.Clear();
        }
        TurnMode = mode;
        Raise(new TurnModeChanged(mode, by));
    }

    /// <summary>
    /// Grants the floor to the oldest queued speech when nobody holds it, you are not speaking,
    /// and the gap since you stopped (or since the last speech ended) has passed. Otherwise does nothing.
    /// </summary>
    public FloorSlot? TryGrantFloor(DateTimeOffset now)
    {
        if (FloorHolder is not null || UserSpeaking || _queue.Count == 0) return null;
        var quietSince = Max(UserStoppedAt, _floorFreedAt);
        if (quietSince is { } since && now - since < Settings.UserGap) return null;

        var slot = _queue[0];
        _queue.RemoveAt(0);
        FloorHolder = slot;
        Focus = slot.Participant;
        Raise(new FloorGranted(slot.SpeechId, slot.Participant));
        return slot;
    }

    /// <summary>The speech is done with the floor (finished, interrupted or withdrawn), or leaves the queue.</summary>
    public void ReleaseFloor(string speechId, DateTimeOffset now)
    {
        if (FloorHolder?.SpeechId == speechId)
        {
            FloorHolder = null;
            _floorFreedAt = now;
        }
        else
        {
            _queue.RemoveAll(s => s.SpeechId == speechId);
            _hands.RemoveAll(s => s.SpeechId == speechId);
        }
    }

    /// <summary>Puts a granted speech that never started playing back at the front of the queue.</summary>
    public void ReturnFloor(string speechId, DateTimeOffset now)
    {
        if (FloorHolder is not { } holder || holder.SpeechId != speechId) return;
        _queue.Insert(0, holder);
        FloorHolder = null;
        _floorFreedAt = now;
    }

    // ---------- lookups

    public string? CanonicalName(string name) => Find(name)?.Name;

    public int? LatestRelevantUtterance(string participant) =>
        _lastRelevant.TryGetValue(participant, out var id) ? id : null;

    public Participant? Find(string name)
    {
        var n = Words.Normalize(name ?? "");
        return _participants.FirstOrDefault(p => Words.Normalize(p.Name) == n);
    }

    private Participant Require(string name) =>
        Find(name) ?? throw new DomainViolation("participant-unknown", $"'{name}' is not in the room. Join with participant post --name.");

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
