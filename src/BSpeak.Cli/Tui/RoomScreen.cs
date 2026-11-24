using System.Text.Json;

namespace BSpeak.Cli.Tui;

/// <summary>A run of text with an ANSI style (for example "1;32"), or none.</summary>
public readonly record struct Seg(string Text, string? Style = null);

/// <summary>One screen row, made of styled runs.</summary>
public sealed class Line(params Seg[] segs)
{
    public List<Seg> Segs { get; } = [.. segs];
    public string Plain => string.Concat(Segs.Select(s => s.Text));

    public Line Add(string text, string? style = null)
    {
        Segs.Add(new Seg(text, style));
        return this;
    }
}

/// <summary>What an agent is doing, as the room sees it.</summary>
public enum AgentActivity { Idle, Waiting, UpNext, Talking }

/// <summary>
/// The room as the TUI shows it, built from the host's event log plus a periodic status snapshot.
/// Pure: no terminal or network access, so it can be driven by tests.
/// </summary>
public sealed class RoomScreen
{
    public sealed class Agent(string name, string voice)
    {
        public string Name { get; } = name;
        public string Voice { get; set; } = voice;

        /// <summary>The agent has an inbox or utterance wait open, so it hears you right away.</summary>
        public bool Listening { get; set; }

        /// <summary>Speeches posted and not yet played, oldest first.</summary>
        public List<string> Pending { get; } = [];
        public string? UpNext { get; set; }
        public string? Talking { get; set; }
        public DateTimeOffset? TalkingSince { get; set; }

        public AgentActivity Activity =>
            Talking is not null ? AgentActivity.Talking
            : UpNext is not null ? AgentActivity.UpNext
            : Pending.Count > 0 ? AgentActivity.Waiting
            : AgentActivity.Idle;
    }

    private sealed record Speech(string Id, string Participant, string Text, DateTimeOffset QueuedAt);

    public sealed record LogEntry(DateTimeOffset At, string Who, string Text, string? Style = null);

    private const int MaxLog = 200;
    private readonly List<Agent> _agents = [];
    private readonly Dictionary<string, Speech> _speeches = new();
    private readonly List<LogEntry> _log = [];

    public long Cursor { get; private set; }
    public IReadOnlyList<Agent> Agents => _agents;
    public IReadOnlyList<LogEntry> Log => _log;
    public bool Listening { get; private set; } = true;
    public string Mode { get; private set; } = "always";
    public string? WakeWord { get; private set; }
    public bool UserSpeaking { get; private set; }
    public DateTimeOffset? UserSpeakingSince { get; private set; }
    public int? Volume { get; set; }
    public string? Version { get; private set; }

    /// <summary>Set while the host cannot be reached; shown instead of the room.</summary>
    public string? Disconnected { get; set; }

    /// <summary>Forgets everything, for a fresh replay after the host restarted.</summary>
    public void Clear()
    {
        _agents.Clear();
        _speeches.Clear();
        _log.Clear();
        Cursor = 0;
        UserSpeaking = false;
        UserSpeakingSince = null;
    }

    public Agent? Find(string name) => _agents.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies one event from <c>GET /api/events</c>.</summary>
    public void Apply(long seq, DateTimeOffset at, string type, JsonElement data)
    {
        if (seq <= Cursor) return;
        Cursor = seq;
        string? S(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (type)
        {
            case "ParticipantJoined":
            {
                var name = S("name")!;
                if (Find(name) is { } existing) existing.Voice = S("voice") ?? existing.Voice;
                else _agents.Add(new Agent(name, S("voice") ?? ""));
                Add(at, name, "joined the room", "2");
                break;
            }
            case "ParticipantLeft":
            {
                var name = S("name")!;
                _agents.RemoveAll(a => a.Name == name);
                Add(at, name, S("reason") == "timed-out" ? "dropped out (stopped checking in)" : "left the room", "2");
                break;
            }
            case "VoiceChanged":
                if (Find(S("name")!) is { } changed) changed.Voice = S("voice") ?? changed.Voice;
                Add(at, S("name")!, $"now speaks as {S("voice")}", "2");
                break;
            case "ListeningPaused":
                Listening = false;
                Add(at, "room", "stopped listening", "31");
                break;
            case "ListeningResumed":
                Listening = true;
                Add(at, "room", "listening again", "32");
                break;
            case "ListeningModeChanged":
                Mode = S("mode") ?? Mode;
                WakeWord = S("wakeWord");
                break;
            case "UserStartedSpeaking":
                UserSpeaking = true;
                UserSpeakingSince = at;
                break;
            case "UserSpoke":
            {
                UserSpeaking = false;
                UserSpeakingSince = null;
                var to = S("addressedTo");
                Add(at, to is null ? "you" : $"you > {to}", S("text") ?? "", "36");
                break;
            }
            case "UtteranceDiscarded":
                UserSpeaking = false;
                break;
            case "SpeechQueued":
            {
                var speech = new Speech(S("speechId")!, S("participant")!, S("text") ?? "", at);
                _speeches[speech.Id] = speech;
                Find(speech.Participant)?.Pending.Add(speech.Id);
                break;
            }
            case "FloorGranted":
                if (Find(S("participant")!) is { } next) next.UpNext = S("speechId");
                break;
            case "PlaybackStarted":
                if (Find(S("participant")!) is { } talker)
                {
                    var id = S("speechId")!;
                    talker.Talking = id;
                    talker.TalkingSince = at;
                    talker.Pending.Remove(id);
                    if (talker.UpNext == id) talker.UpNext = null;
                }
                break;
            case "PlaybackFinished":
            {
                var id = S("speechId")!;
                Add(at, S("participant")!, TextOf(id), null);
                Done(S("participant")!, id);
                break;
            }
            case "SpeechInterrupted":
            {
                var heard = S("heardText");
                Add(at, S("participant")!, string.IsNullOrWhiteSpace(heard) ? "(cut off before a full sentence)" : $"{heard} (cut off)", "33");
                Done(S("participant")!, S("speechId")!);
                break;
            }
            case "SpeechWithdrawn":
            {
                var id = S("speechId")!;
                var why = S("reason") switch
                {
                    "interrupted" => "dropped it, you started talking",
                    "user-spoke" => "dropped it, you said something new",
                    "playback-failed" => "could not be played",
                    "participant-left" => null,
                    _ => "took it back",
                };
                if (why is not null) Add(at, S("participant")!, $"{why}: {Preview(TextOf(id), 60)}", "2");
                Done(S("participant")!, id);
                break;
            }
            case "HandoffPosted":
                Add(at, S("from")!, $"handed off: {S("text")}", "35");
                break;
            case "HandoffUpdated":
            {
                var note = S("note");
                Add(at, S("by") ?? "worker", $"{S("handoffId")} {S("status")}{(note is null ? "" : $": {note}")}", "35");
                break;
            }
        }
    }

    /// <summary>Reconciles with <c>GET /api/host</c>: who is present and listening, and the room's switches.</summary>
    public void Sync(JsonElement host)
    {
        Version = host.TryGetProperty("version", out var v) ? v.GetString() : Version;
        if (!host.TryGetProperty("room", out var room)) return;
        if (room.TryGetProperty("listening", out var listening)) Listening = listening.GetBoolean();
        if (room.TryGetProperty("mode", out var mode)) Mode = mode.GetString() ?? Mode;
        if (room.TryGetProperty("wakeWord", out var wake)) WakeWord = wake.ValueKind == JsonValueKind.String ? wake.GetString() : null;
        if (room.TryGetProperty("userSpeaking", out var speaking))
        {
            var now = speaking.GetBoolean();
            if (!now) UserSpeakingSince = null;
            else if (!UserSpeaking) UserSpeakingSince ??= DateTimeOffset.Now;
            UserSpeaking = now;
        }
        if (!room.TryGetProperty("participants", out var participants)) return;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in participants.EnumerateArray())
        {
            var name = p.GetProperty("name").GetString()!;
            present.Add(name);
            var agent = Find(name);
            if (agent is null) _agents.Add(agent = new Agent(name, ""));
            agent.Voice = p.GetProperty("voice").GetString() ?? agent.Voice;
            agent.Listening = p.TryGetProperty("waiting", out var w) && w.GetBoolean();
        }
        _agents.RemoveAll(a => !present.Contains(a.Name));
    }

    private void Done(string participant, string speechId)
    {
        if (Find(participant) is { } agent)
        {
            agent.Pending.Remove(speechId);
            if (agent.UpNext == speechId) agent.UpNext = null;
            if (agent.Talking == speechId) { agent.Talking = null; agent.TalkingSince = null; }
        }
        _speeches.Remove(speechId);
    }

    private string TextOf(string speechId) => _speeches.TryGetValue(speechId, out var s) ? s.Text : "";

    private void Add(DateTimeOffset at, string who, string text, string? style)
    {
        _log.Add(new LogEntry(at, who, text, style));
        if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
    }

    private static string Preview(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";

    private static readonly string[] Meter = ["▂▅▇▅", "▃▇▅▂", "▅▃▇▄", "▇▂▅▃", "▄▆▃▇"];
    private static readonly string[] NameColors = ["32", "33", "35", "34", "36", "91", "92", "93"];

    private static string Seconds(DateTimeOffset since, DateTimeOffset now)
    {
        var s = Math.Max(0, (int)(now - since).TotalSeconds);
        return s < 60 ? $"{s}s" : $"{s / 60}m{s % 60:00}s";
    }

    private string ColorOf(string name)
    {
        var i = _agents.FindIndex(a => a.Name == name);
        return NameColors[(i < 0 ? 0 : i) % NameColors.Length];
    }

    /// <summary>Renders the screen as rows. <paramref name="height"/> bounds how much of the log is shown.</summary>
    public List<Line> Render(DateTimeOffset now, int width, int height)
    {
        var rows = new List<Line>();
        var listening = !Listening ? ("not listening, say \"start listening\"", "1;31")
            : Mode == "wake-word" ? ($"listening for \"{WakeWord}\"", "32")
            : ("listening", "32");
        var header = new Line(new Seg(" b-speak ", "1;7"), new Seg("  "), new Seg(listening.Item1, listening.Item2));
        if (Volume is { } volume) header.Add($"   volume {volume}%", "2");
        header.Add($"   {_agents.Count} {(_agents.Count == 1 ? "agent" : "agents")}", "2");
        header.Add($"   {now.ToLocalTime():HH:mm:ss}", "2");
        rows.Add(header);
        rows.Add(new Line());

        if (Disconnected is { } why)
        {
            rows.Add(new Line(new Seg($"  {why}", "33")));
            rows.Add(new Line());
            rows.Add(Footer());
            return rows;
        }

        var frame = Meter[(int)(now.ToUnixTimeMilliseconds() / 140 % Meter.Length)];
        var nameWidth = Math.Max(5, _agents.Count == 0 ? 0 : _agents.Max(a => a.Name.Length)) + 2;

        var you = new Line(new Seg("  "), new Seg("you".PadRight(nameWidth), "1;36"));
        if (UserSpeaking) you.Add(frame, "36").Add(" speaking", "1;36").Add(UserSpeakingSince is { } us ? $" {Seconds(us, now)}" : "", "2");
        else if (!Listening) you.Add("     muted", "2");
        rows.Add(you);

        if (_agents.Count == 0) rows.Add(new Line(new Seg("  no agents in the room yet", "2")));
        foreach (var agent in _agents)
        {
            var color = ColorOf(agent.Name);
            var talking = agent.Activity == AgentActivity.Talking;
            var row = new Line(new Seg(talking ? "▶ " : "  ", "1;" + color), new Seg(agent.Name.PadRight(nameWidth), (talking ? "1;" : "") + color));
            switch (agent.Activity)
            {
                case AgentActivity.Talking:
                    row.Add(frame, color).Add(" talking", "1;" + color).Add($" {Seconds(agent.TalkingSince!.Value, now)}  ", "2")
                       .Add(TextOf(agent.Talking!));
                    break;
                case AgentActivity.UpNext:
                    row.Add("     up next  ", "1;33").Add(Preview(TextOf(agent.UpNext!), 200), "2");
                    break;
                case AgentActivity.Waiting:
                {
                    var first = _speeches.GetValueOrDefault(agent.Pending[0]);
                    var more = agent.Pending.Count > 1 ? $" (+{agent.Pending.Count - 1})" : "";
                    row.Add("     waiting to speak", "33").Add(first is null ? more : $" {Seconds(first.QueuedAt, now)}{more}  ", "2")
                       .Add(first is null ? "" : first.Text, "2");
                    break;
                }
                default:
                    row.Add(agent.Listening ? "     listening" : "     busy", "2");
                    break;
            }
            rows.Add(row);
        }

        rows.Add(new Line());
        rows.Add(new Line(new Seg("  recent " + new string('─', Math.Max(0, Math.Min(width, 80) - 11)), "2")));
        var room = Math.Max(0, height - rows.Count - 2);
        var whoWidth = _log.Count == 0 ? 0 : Math.Min(24, _log.TakeLast(room).Max(e => e.Who.Length)) + 2;
        foreach (var entry in _log.TakeLast(room))
        {
            var whoStyle = entry.Who.StartsWith("you") ? "36" : entry.Who == "room" ? "2" : ColorOf(entry.Who);
            rows.Add(new Line(
                new Seg($"  {entry.At.ToLocalTime():HH:mm:ss}  ", "2"),
                new Seg(entry.Who.PadRight(whoWidth), whoStyle),
                new Seg(entry.Text, entry.Style)));
        }
        while (rows.Count < height - 1) rows.Add(new Line());
        rows.Add(Footer());
        return rows;
    }

    private static Line Footer() => new(new Seg("  q", "1"), new Seg(" quit   ", "2"), new Seg("space", "1"), new Seg(" pause or resume listening   ", "2"), new Seg("+ -", "1"), new Seg(" volume", "2"));
}
