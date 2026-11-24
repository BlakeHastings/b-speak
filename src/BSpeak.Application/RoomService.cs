using BSpeak.Domain;

namespace BSpeak.Application;

/// <summary>
/// The host's application core. Every adapter (CLI over HTTP, MCP later) and every infrastructure
/// callback (voice detection, speech to text, playback) goes through here.
/// </summary>
/// <remarks>
/// Commands run one at a time under a lock. After each command the events it produced are appended
/// to the log and the deterministic policies run, which may issue further commands. Side effects on
/// the audio devices (play, stop, earcon) are collected and run after the lock is released.
/// </remarks>
public sealed class RoomService
{
    private readonly object _gate = new();
    private readonly Room _room;
    private readonly SpeechRules _rules;
    private readonly IClock _clock;
    private readonly IEventLog _log;
    private readonly IPlayback _playback;
    private readonly Dictionary<string, Speech> _speeches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _openWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Handoff> _handoffs = new(StringComparer.Ordinal);
    private List<Action> _effects = [];
    private int _speechCounter;
    private int _handoffCounter;

    public RoomService(RoomSettings settings, SpeechRules rules, IClock clock, IEventLog log, IPlayback playback)
    {
        _room = new Room(settings);
        _rules = rules;
        _clock = clock;
        _log = log;
        _playback = playback;
    }

    public IEventLog Log => _log;

    // ================= agent commands

    /// <summary>Names handed out when an agent joins without choosing one. Chosen to sound distinct from each other.</summary>
    public static readonly IReadOnlyList<string> NamePool = ["Atlas", "Nova", "Juno", "Felix", "Iris", "Orion", "Hazel", "Milo", "Sage", "Quinn"];

    /// <param name="name">The name to join as, or null to take the first free name from <see cref="NamePool"/>.</param>
    /// <param name="preferredVoiceFor">The voice a name used before, if remembered.</param>
    public JoinResult Join(string? name, Func<string, string?>? preferredVoiceFor = null) => Run(() =>
    {
        Participant p;
        if (!string.IsNullOrWhiteSpace(name)) p = _room.Join(name, _clock.Now, preferredVoiceFor?.Invoke(name.Trim()));
        else
        {
            var free = NamePool.FirstOrDefault(n => _room.Participants.All(x => !Words.SoundsAlike(x.Name, n)))
                ?? throw new DomainViolation("name-pool-empty", "Every pooled name is taken. Join with --name.");
            p = _room.Join(free, _clock.Now, preferredVoiceFor?.Invoke(free));
        }
        return new JoinResult(p.Name, p.Voice, _room.UtteranceCount, _log.Head);
    });

    // ================= handoffs (voice agent to orchestrator)

    public HandoffView PostHandoff(string @as, string text, int? utteranceId) => Run(() =>
    {
        var p = _room.Touch(@as, _clock.Now);
        var handoff = Handoff.Post($"h{_handoffCounter + 1}", p.Name, text, utteranceId, _room);
        _handoffCounter++;
        _handoffs[handoff.Id] = handoff;
        return View(handoff);
    });

    /// <param name="by">Who is updating, for example "orchestrator". Need not be in the room.</param>
    public HandoffView UpdateHandoff(string id, string? by, HandoffStatus status, string? note) => Run(() =>
    {
        var handoff = RequireHandoff(id);
        handoff.Update(by ?? "orchestrator", status, note);
        return View(handoff);
    });

    public HandoffView GetHandoff(string id)
    {
        lock (_gate) return View(RequireHandoff(id));
    }

    public IReadOnlyList<HandoffView> ListHandoffs(HandoffStatus? status = null)
    {
        lock (_gate) return _handoffs.Values.Where(h => status is null || h.Status == status).Select(View).ToList();
    }

    /// <summary>Blocks until at least one handoff is open (unclaimed). Returns them, or an empty list on timeout.</summary>
    public async Task<IReadOnlyList<HandoffView>> WaitForOpenHandoffsAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await WaitAsync(() => ListHandoffs(HandoffStatus.Open) is { Count: > 0 } open ? open : null, timeout, ct) ?? [];

    // ================= inbox (everything the voice agent needs to react to)

    /// <summary>
    /// What happened for this participant after event <paramref name="after"/>: utterances, updates on
    /// handoffs it posted, and its own speeches being interrupted or failing. Pass <see cref="InboxBatch.Next"/> back as <paramref name="after"/>.
    /// </summary>
    public InboxBatch Inbox(string @as, long after)
    {
        var name = Run(() => _room.Touch(@as, _clock.Now).Name);
        return InboxFor(name, after, timedOut: false);
    }

    public async Task<InboxBatch> WaitForInboxAsync(string @as, long after, TimeSpan timeout, CancellationToken ct = default)
    {
        var name = Run(() => _room.Touch(@as, _clock.Now).Name);
        lock (_gate) _openWaits[name] = _openWaits.GetValueOrDefault(name) + 1;
        try
        {
            var found = await WaitAsync(() => InboxFor(name, after, false) is { Items.Count: > 0 } b ? b : null, timeout, ct);
            return found ?? InboxFor(name, after, timedOut: true);
        }
        finally
        {
            lock (_gate)
            {
                if (--_openWaits[name] == 0) _openWaits.Remove(name);
                if (_room.Find(name) is not null) _room.Touch(name, _clock.Now);
            }
        }
    }

    /// <summary>
    /// Who was heard last and how long ago, from the whole retained log, so an agent can tell whether to open
    /// with a micro introduction ("Rovo, a follow-up on ...") or just carry on.
    /// </summary>
    private RoomContext ContextFor(string name)
    {
        var now = _clock.Now;
        string? lastHeard = null;
        DateTimeOffset? lastHeardAt = null, youLastSpoke = null, lastToYou = null;
        foreach (var e in _log.After(0, int.MaxValue))
        {
            switch (e.Event)
            {
                case UserSpoke u:
                    (lastHeard, lastHeardAt) = ("user", e.At);
                    if (IsForYou(u, name)) lastToYou = e.At;
                    break;
                case PlaybackStarted s:
                    (lastHeard, lastHeardAt) = (s.Participant, e.At);
                    break;
                case PlaybackFinished f:
                    (lastHeard, lastHeardAt) = (f.Participant, e.At);
                    if (f.Participant == name) youLastSpoke = e.At;
                    break;
                case SpeechInterrupted i:
                    (lastHeard, lastHeardAt) = (i.Participant, e.At);
                    if (i.Participant == name) youLastSpoke = e.At;
                    break;
            }
        }
        static int? Ago(DateTimeOffset? at, DateTimeOffset now) => at is { } t ? (int)Math.Max(0, (now - t).TotalSeconds) : null;
        return new RoomContext(lastHeard, Ago(lastHeardAt, now), Ago(youLastSpoke, now), Ago(lastToYou, now));
    }

    /// <summary>The sentences of an interrupted speech the user never heard.</summary>
    private string? Unheard(SpeechInterrupted s) =>
        _speeches.GetValueOrDefault(s.SpeechId) is { } speech ? string.Join(" ", speech.Sentences.Skip(s.HeardThrough + 1)) : null;

    private InboxBatch InboxFor(string name, long after, bool timedOut)
    {
        var events = _log.After(after, int.MaxValue);
        var items = new List<InboxItem>();
        foreach (var e in events)
        {
            switch (e.Event)
            {
                case UserSpoke u:
                    items.Add(new InboxItem(e.Seq, "utterance", e.At) { UtteranceId = u.UtteranceId, Text = u.Text, AddressedTo = u.AddressedTo, Focus = u.Focus, ForYou = IsForYou(u, name) });
                    break;
                case HandoffUpdated h when h.From == name:
                    items.Add(new InboxItem(e.Seq, "handoff", e.At) { HandoffId = h.HandoffId, Status = h.Status, Text = h.Note, By = h.By });
                    break;
                case SpeechInterrupted s when s.Participant == name:
                    items.Add(new InboxItem(e.Seq, "speech-interrupted", e.At) { SpeechId = s.SpeechId, Text = s.HeardText, Unheard = Unheard(s) });
                    break;
                case SpeechWithdrawn w when w.Participant == name && w.Reason == WithdrawReason.PlaybackFailed:
                    items.Add(new InboxItem(e.Seq, "speech-failed", e.At) { SpeechId = w.SpeechId, Text = _speeches.GetValueOrDefault(w.SpeechId)?.Text });
                    break;
                case SpeechWithdrawn w when w.Participant == name && w.Reason is WithdrawReason.Interrupted or WithdrawReason.UserSpoke:
                    items.Add(new InboxItem(e.Seq, "speech-withdrawn", e.At) { SpeechId = w.SpeechId, Text = _speeches.GetValueOrDefault(w.SpeechId)?.Text, Reason = w.Reason == WithdrawReason.Interrupted ? "interrupted" : "user-spoke" });
                    break;
            }
        }
        var next = events.Count > 0 ? events[^1].Seq : after;
        return new InboxBatch(items, next, timedOut, ContextFor(name));
    }

    public void Leave(string name) => Run(() => _room.Leave(name));

    /// <summary>Changes a participant's voice. Speech already playing keeps the old one.</summary>
    /// <param name="available">The voices the speaking engine has, or null when not known yet.</param>
    public ParticipantView ChangeVoice(string name, string voice, IReadOnlyCollection<string>? available) => Run(() =>
    {
        _room.Touch(name, _clock.Now);
        _room.ChangeVoice(name, voice, available);
        var p = _room.Find(name)!;
        return new ParticipantView(p.Name, p.Voice, p.LastSeen, _openWaits.ContainsKey(p.Name));
    });

    /// <param name="by">Who asked: a participant name, or null for the user at the CLI.</param>
    public void SetListening(bool on, string? by) => Run(() =>
    {
        var who = by is null ? "cli" : $"cli:{_room.Touch(by, _clock.Now).Name}";
        if (on) _room.ResumeListening(who); else _room.PauseListening(who);
    });

    public void SetMode(ListeningMode mode, string? wakeWord) => Run(() => _room.SetMode(mode, wakeWord));

    public SpeechView PostSpeech(string @as, string text, int? replyTo) => Run(() =>
    {
        var p = _room.Touch(@as, _clock.Now);
        var speech = Speech.Queue($"s{_speechCounter + 1}", p.Name, text, replyTo, _room, _rules);
        _speechCounter++;
        _speeches[speech.Id] = speech;
        _room.RequestFloor(speech.Id, p.Name);
        return View(speech);
    });

    public void WithdrawSpeech(string @as, string speechId) => Run(() =>
    {
        var p = _room.Touch(@as, _clock.Now);
        var speech = RequireSpeech(speechId);
        var wasGranted = _room.FloorHolder?.SpeechId == speechId;
        speech.Withdraw(p.Name, WithdrawReason.Withdrawn);
        _room.ReleaseFloor(speechId, _clock.Now);
        if (wasGranted) _effects.Add(() => _playback.Stop(speechId));
    });

    // ================= queries

    public SpeechView GetSpeech(string speechId)
    {
        lock (_gate) return View(RequireSpeech(speechId));
    }

    public IReadOnlyList<SpeechView> ListSpeech(string? participant = null)
    {
        lock (_gate)
            return _speeches.Values
                .Where(s => participant is null || s.Participant == _room.CanonicalName(participant))
                .Select(View).ToList();
    }

    public RoomStatus Status()
    {
        lock (_gate)
            return new RoomStatus(
                _room.Participants.Select(p => new ParticipantView(p.Name, p.Voice, p.LastSeen, _openWaits.ContainsKey(p.Name))).ToList(),
                _room.Listening, _room.Mode, _room.WakeWord, _room.Focus, _room.UserSpeaking,
                _room.FloorHolder, _room.FloorQueue.ToList(), _room.UtteranceCount);
    }

    /// <summary>Utterances after <paramref name="after"/> (an utterance id), without waiting.</summary>
    public IReadOnlyList<UtteranceView> ListUtterances(string @as, int after, bool forYouOnly = false)
    {
        var name = Run(() => _room.Touch(@as, _clock.Now).Name);
        return Utterances(name, after, forYouOnly);
    }

    /// <summary>
    /// Blocks until at least one utterance after <paramref name="after"/> arrives, or the timeout passes.
    /// While the wait is open the participant counts as present and never times out.
    /// </summary>
    public async Task<UtteranceBatch> WaitForUtterancesAsync(string @as, int after, bool forYouOnly, TimeSpan timeout, CancellationToken ct = default)
    {
        var name = Run(() => _room.Touch(@as, _clock.Now).Name);
        lock (_gate) _openWaits[name] = _openWaits.GetValueOrDefault(name) + 1;
        try
        {
            var found = await WaitAsync(() => Utterances(name, after, forYouOnly) is { Count: > 0 } u ? u : null, timeout, ct);
            return new UtteranceBatch(found ?? [], TimedOut: found is null);
        }
        finally
        {
            lock (_gate)
            {
                if (--_openWaits[name] == 0) _openWaits.Remove(name);
                if (_room.Find(name) is not null) _room.Touch(name, _clock.Now);
            }
        }
    }

    /// <summary>Blocks until the speech is finished, interrupted or withdrawn. Returns null on timeout.</summary>
    public Task<SpeechView?> WaitForSpeechAsync(string speechId, TimeSpan timeout, CancellationToken ct = default)
    {
        GetSpeech(speechId);
        return WaitAsync(() => { lock (_gate) { var s = _speeches[speechId]; return s.IsDone ? View(s) : null; } }, timeout, ct);
    }

    // ================= audio pipeline (capture side)

    public void SpeechOnset() => Run(() => _room.BeginUserTurn(_clock.Now));

    public void Utterance(string transcript) => Run(() => _room.RecordUtterance(transcript, _clock.Now));

    // ================= audio pipeline (render side)

    public void PlaybackStarted(string speechId) => Run(() =>
    {
        if (!_speeches.TryGetValue(speechId, out var speech)) return;
        if (speech.State != SpeechState.Queued || _room.FloorHolder?.SpeechId != speechId) return;
        speech.Start(VoiceOf(speech.Participant));
    });

    public void SentencePlayed(string speechId, int index) => Run(() =>
    {
        if (_speeches.TryGetValue(speechId, out var speech)) speech.SentencePlayed(index);
    });

    /// <summary>The render side played the last sample of the speech.</summary>
    public void PlaybackDrained(string speechId) => Run(() =>
    {
        if (!_speeches.TryGetValue(speechId, out var speech) || speech.State != SpeechState.Playing) return;
        speech.Finish();
        _room.ReleaseFloor(speechId, _clock.Now);
    });

    /// <summary>
    /// Text to speech or the device failed. A speech that never started is withdrawn; one that was playing
    /// counts as interrupted, so the agent learns how much was heard. Either way the floor is released.
    /// </summary>
    public void PlaybackFailed(string speechId) => Run(() =>
    {
        if (!_speeches.TryGetValue(speechId, out var speech)) return;
        if (speech.State == SpeechState.Queued) speech.Withdraw(speech.Participant, WithdrawReason.PlaybackFailed);
        else if (speech.State == SpeechState.Playing) speech.Interrupt();
        else return;
        _room.ReleaseFloor(speechId, _clock.Now);
    });

    /// <summary>Clock-driven policies: idle timeout, and granting the floor once the gap after you has passed.</summary>
    public void Tick() => Run(() =>
    {
        _room.ExpireIdle(_clock.Now, name => _openWaits.ContainsKey(name));
        _room.TryGrantFloor(_clock.Now);
    });

    // ================= policies

    private void React(DomainEvent e)
    {
        switch (e)
        {
            case UserStartedSpeaking:
                BargeIn();
                break;
            case UserSpoke:
                WithdrawStaleSpeech();
                break;
            case ParticipantLeft left:
                foreach (var s in _speeches.Values.Where(s => s.Participant == left.Name && s.State == SpeechState.Queued).ToList())
                {
                    var wasGranted = _room.FloorHolder?.SpeechId == s.Id;
                    s.Withdraw(left.Name, WithdrawReason.ParticipantLeft);
                    _room.ReleaseFloor(s.Id, _clock.Now);
                    if (wasGranted) _effects.Add(() => _playback.Stop(s.Id));
                }
                break;
            case ListeningPaused:
                _effects.Add(() => _playback.PlayEarcon(Earcon.ListeningPaused));
                break;
            case ListeningResumed:
                _effects.Add(() => _playback.PlayEarcon(Earcon.ListeningResumed));
                break;
            case FloorGranted granted:
                var speech = _speeches[granted.SpeechId];
                var request = new PlaybackRequest(speech.Id, VoiceOf(speech.Participant), speech.Sentences);
                _effects.Add(() => _playback.Play(request));
                break;
            case SpeechQueued or PlaybackFinished or SpeechInterrupted or SpeechWithdrawn:
                _room.TryGrantFloor(_clock.Now);
                break;
        }
    }

    /// <summary>
    /// You started speaking: stop whoever holds the floor at once. A speech that had not started yet is
    /// withdrawn rather than put back, so the agent decides whether it still wants to say it.
    /// </summary>
    private void BargeIn()
    {
        if (_room.FloorHolder is not { } holder) return;
        var speech = _speeches[holder.SpeechId];
        if (speech.State == SpeechState.Playing) speech.Interrupt();
        else speech.Withdraw(speech.Participant, WithdrawReason.Interrupted);
        _room.ReleaseFloor(holder.SpeechId, _clock.Now);
        _effects.Add(() => _playback.Stop(holder.SpeechId));
    }

    /// <summary>
    /// You said something new: every speech still waiting was written before it, so withdraw them all and let
    /// each agent decide whether to say it again. Noise and coughs (no utterance) leave the queue alone.
    /// </summary>
    private void WithdrawStaleSpeech()
    {
        foreach (var s in _speeches.Values.Where(s => s.State == SpeechState.Queued).ToList())
        {
            s.Withdraw(s.Participant, WithdrawReason.UserSpoke);
            _room.ReleaseFloor(s.Id, _clock.Now);
        }
    }

    // ================= plumbing

    private void Run(Action command) => Run(() => { command(); return 0; });

    private T Run<T>(Func<T> command)
    {
        List<Action> effects;
        T result;
        lock (_gate)
        {
            _effects = [];
            try { result = command(); }
            finally { Commit(); }
            effects = _effects;
        }
        foreach (var effect in effects) effect();
        return result;
    }

    /// <summary>Appends pending events in order and runs policies until nothing new happens.</summary>
    private void Commit()
    {
        while (true)
        {
            var pending = _room.TakeEvents()
                .Concat(_speeches.Values.Where(s => s.PendingEvents.Count > 0).SelectMany(s => s.TakeEvents()))
                .Concat(_handoffs.Values.Where(h => h.PendingEvents.Count > 0).SelectMany(h => h.TakeEvents()))
                .ToList();
            if (pending.Count == 0) return;
            foreach (var e in pending)
            {
                _log.Append(e, _clock.Now);
                React(e);
            }
        }
    }

    private IReadOnlyList<UtteranceView> Utterances(string name, int after, bool forYouOnly) =>
        _log.After(0, int.MaxValue)
            .Where(s => s.Event is UserSpoke u && u.UtteranceId > after)
            .Select(s => (Stored: s, Spoke: (UserSpoke)s.Event))
            .Select(x => new UtteranceView(x.Spoke.UtteranceId, x.Spoke.Text, x.Spoke.AddressedTo, x.Spoke.Focus, IsForYou(x.Spoke, name), x.Stored.At))
            .Where(u => !forYouOnly || u.ForYou)
            .ToList();

    private static bool IsForYou(UserSpoke u, string name) =>
        u.AddressedTo == name || u.AddressedTo == AddressResolver.Everyone || (u.AddressedTo is null && u.Focus == name);

    private async Task<T?> WaitAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var changed = _log.Changed;
            if (probe() is { } found) return found;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.WhenAny(changed, Task.Delay(remaining, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private Speech RequireSpeech(string id) =>
        _speeches.TryGetValue(id, out var s) ? s : throw new NotFoundException("speech-unknown", $"There is no speech {id}.");

    private string VoiceOf(string participant) => _room.Find(participant)?.Voice ?? _room.Settings.Voices.FirstOrDefault() ?? "default";

    private Handoff RequireHandoff(string id) =>
        _handoffs.TryGetValue(id, out var h) ? h : throw new NotFoundException("handoff-unknown", $"There is no handoff {id}.");

    private static HandoffView View(Handoff h) => new(h.Id, h.From, h.Text, h.UtteranceId, h.Status, h.Notes.ToList());

    private static SpeechView View(Speech s) => new(s.Id, s.Participant, s.Text, s.State, s.ReplyTo, s.HeardThrough, s.HeardText);
}
