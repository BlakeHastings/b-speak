using System.Text.Json;
using BSpeak.Cli.Tui;

namespace BSpeak.Cli.Tests;

/// <summary>The room view follows the host's event log: who is here, who is talking, who is waiting.</summary>
public sealed class RoomScreenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 14, 0, 0, TimeSpan.Zero);
    private readonly RoomScreen _screen = new();
    private long _seq;

    private void On(string type, object data, int atSeconds = 0) =>
        _screen.Apply(++_seq, T0.AddSeconds(atSeconds), type, JsonSerializer.SerializeToElement(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private string Row(string name) =>
        _screen.Render(T0.AddSeconds(30), 200, 40).Select(l => l.Plain).First(p => p.TrimStart('▶', ' ').StartsWith(name + " "));

    [Fact]
    public void An_agent_is_waiting_then_up_next_then_talking_then_idle()
    {
        On("ParticipantJoined", new { name = "Atlas", voice = "Miles" });
        On("SpeechQueued", new { speechId = "s1", participant = "Atlas", text = "Build is green." }, 10);
        Assert.Equal(AgentActivity.Waiting, _screen.Find("Atlas")!.Activity);
        Assert.Contains("waiting to speak 20s", Row("Atlas"));
        Assert.Contains("Build is green.", Row("Atlas"));

        On("FloorGranted", new { speechId = "s1", participant = "Atlas" }, 11);
        Assert.Equal(AgentActivity.UpNext, _screen.Find("Atlas")!.Activity);

        On("PlaybackStarted", new { speechId = "s1", participant = "Atlas", voice = "Miles" }, 12);
        Assert.Equal(AgentActivity.Talking, _screen.Find("Atlas")!.Activity);
        Assert.Contains("talking 18s", Row("Atlas"));
        Assert.StartsWith("▶", Row("Atlas"));

        On("PlaybackFinished", new { speechId = "s1", participant = "Atlas" }, 14);
        Assert.Equal(AgentActivity.Idle, _screen.Find("Atlas")!.Activity);
        Assert.Equal(("Atlas", "Build is green."), (_screen.Log[^1].Who, _screen.Log[^1].Text));
    }

    [Fact]
    public void Interruptions_and_withdrawals_show_what_was_heard_and_why()
    {
        On("ParticipantJoined", new { name = "Atlas", voice = "Miles" });
        On("ParticipantJoined", new { name = "Nova", voice = "Harper" });
        On("SpeechQueued", new { speechId = "s1", participant = "Atlas", text = "First point. Second point." });
        On("SpeechQueued", new { speechId = "s2", participant = "Nova", text = "Settings page is done." });
        On("FloorGranted", new { speechId = "s1", participant = "Atlas" });
        On("PlaybackStarted", new { speechId = "s1", participant = "Atlas", voice = "Miles" });
        On("UserStartedSpeaking", new { });
        Assert.True(_screen.UserSpeaking);
        On("SpeechInterrupted", new { speechId = "s1", participant = "Atlas", heardThrough = 0, heardText = "First point." });
        On("UserSpoke", new { utteranceId = 1, text = "Hold on.", addressedTo = (string?)null });
        On("SpeechWithdrawn", new { speechId = "s2", participant = "Nova", reason = "user-spoke" });

        Assert.False(_screen.UserSpeaking);
        Assert.All(_screen.Agents, a => Assert.Equal(AgentActivity.Idle, a.Activity));
        Assert.Equal(
            ["Atlas: First point. (cut off)", "you: Hold on.", "Nova: dropped it, you said something new: Settings page is done."],
            _screen.Log.Where(e => e.Text.Length > 0 && !e.Text.StartsWith("joined")).Select(e => $"{e.Who}: {e.Text}"));
    }

    [Fact]
    public void Status_snapshot_adds_missing_agents_drops_gone_ones_and_says_who_is_listening()
    {
        On("ParticipantJoined", new { name = "Ghost", voice = "Miles" });
        var host = JsonSerializer.SerializeToElement(new
        {
            version = "0.2.0",
            room = new
            {
                listening = false,
                mode = "always",
                userSpeaking = false,
                participants = new[] { new { name = "Atlas", voice = "Harper", waiting = true }, new { name = "Nova", voice = "Maeve", waiting = false } },
            },
        });
        _screen.Sync(host);

        Assert.Equal(["Atlas", "Nova"], _screen.Agents.Select(a => a.Name));
        Assert.False(_screen.Listening);
        Assert.Contains("listening", Row("Atlas"));
        Assert.Contains("busy", Row("Nova"));
        Assert.Contains("not listening", _screen.Render(T0, 200, 40)[0].Plain);
    }

    [Fact]
    public void Hands_show_in_order_with_their_latest_text_until_called_on()
    {
        On("ParticipantJoined", new { name = "Atlas", voice = "Miles" });
        On("ParticipantJoined", new { name = "Nova", voice = "Harper" });
        On("TurnModeChanged", new { mode = "hands-up", by = "voice" });
        // The room's HandRaised is logged before the speech's SpeechQueued; the view copes with either order.
        On("HandRaised", new { speechId = "s1", participant = "Nova" }, 5);
        On("SpeechQueued", new { speechId = "s1", participant = "Nova", text = "Schema question." }, 5);
        On("HandRaised", new { speechId = "s2", participant = "Atlas" }, 8);
        On("SpeechQueued", new { speechId = "s2", participant = "Atlas", text = "Parser update." }, 8);
        On("SpeechRevised", new { speechId = "s2", participant = "Atlas", text = "Parser is fixed." }, 20);

        Assert.Equal(["Nova", "Atlas"], _screen.HandOrder);
        Assert.Contains("[1] hand up 25s  Schema question.", Row("Nova"));
        Assert.Contains("[2] hand up 22s  Parser is fixed.", Row("Atlas"));
        Assert.Contains("hands up", _screen.Render(T0, 200, 40)[0].Plain);
        Assert.Contains("1-2 call on", _screen.Render(T0, 200, 40)[^1].Plain);

        On("HandCalledOn", new { speechId = "s2", participant = "Atlas", by = "voice" }, 25);
        Assert.Equal(["Nova"], _screen.HandOrder);
        Assert.Equal(AgentActivity.Waiting, _screen.Find("Atlas")!.Activity);
        Assert.Equal(("you", "called on Atlas"), (_screen.Log[^1].Who, _screen.Log[^1].Text));

        On("TurnModeChanged", new { mode = "open", by = "cli" }, 26);
        Assert.Empty(_screen.HandOrder);
        Assert.Equal(AgentActivity.Waiting, _screen.Find("Nova")!.Activity);
    }

    [Fact]
    public void Replaying_the_same_events_twice_changes_nothing()
    {
        var data = JsonSerializer.SerializeToElement(new { name = "Atlas", voice = "Miles" });
        _screen.Apply(1, T0, "ParticipantJoined", data);
        _screen.Apply(1, T0, "ParticipantJoined", data);
        Assert.Single(_screen.Log);
        Assert.Equal(1, _screen.Cursor);
    }

    [Fact]
    public void The_log_fills_the_space_left_and_keeps_the_newest()
    {
        On("ParticipantJoined", new { name = "Atlas", voice = "Miles" });
        for (var i = 1; i <= 50; i++) On("UserSpoke", new { utteranceId = i, text = $"line {i}" });
        var rows = _screen.Render(T0, 120, 20);
        Assert.Equal(20, rows.Count);
        Assert.Contains(rows, r => r.Plain.EndsWith("line 50"));
        Assert.DoesNotContain(rows, r => r.Plain.EndsWith("line 1"));
    }
}
