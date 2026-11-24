using BSpeak.Domain;

namespace BSpeak.Domain.Tests;

public sealed class HandoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    private static Room RoomWithAtlas()
    {
        var room = new Room();
        room.Join("Atlas", T0);
        room.RecordUtterance("Atlas, fix the login bug.", T0);
        return room;
    }

    [Fact]
    public void Posts_open_with_the_canonical_name()
    {
        var h = Handoff.Post("h1", "atlas", "Fix the login bug the user described.", 1, RoomWithAtlas());
        Assert.Equal(("Atlas", HandoffStatus.Open), (h.From, h.Status));
        Assert.IsType<HandoffPosted>(Assert.Single(h.TakeEvents()));
    }

    [Fact]
    public void Rejects_empty_unknown_and_bad_references()
    {
        var room = RoomWithAtlas();
        Assert.Equal("handoff-empty", Violation(() => Handoff.Post("h1", "Atlas", " ", null, room)));
        Assert.Equal("participant-unknown", Violation(() => Handoff.Post("h1", "Nova", "x", null, room)));
        Assert.Equal("reply-to-unknown", Violation(() => Handoff.Post("h1", "Atlas", "x", 9, room)));
    }

    [Fact]
    public void Moves_from_open_to_working_to_done_with_a_spoken_result()
    {
        var h = Handoff.Post("h1", "Atlas", "Fix it.", null, RoomWithAtlas());
        h.Update("orchestrator", HandoffStatus.Working, null);
        h.Update("orchestrator", HandoffStatus.Working, "Found it; it's the session timeout.");
        Assert.Equal("handoff-result-required", Violation(() => h.Update("orchestrator", HandoffStatus.Done, " ")));
        h.Update("orchestrator", HandoffStatus.Done, "Fixed. Logins survive a refresh now.");
        Assert.True(h.IsClosed);
        Assert.Equal(3, h.Notes.Count);
        Assert.Equal("state-transition", Violation(() => h.Update("orchestrator", HandoffStatus.Working, null)));
    }

    [Fact]
    public void Never_reopens_and_ignores_repeated_status_without_a_note()
    {
        var h = Handoff.Post("h1", "Atlas", "Fix it.", null, RoomWithAtlas());
        h.Update("", HandoffStatus.Working, null);
        h.TakeEvents();
        h.Update("", HandoffStatus.Working, null);
        Assert.Empty(h.TakeEvents());
        Assert.Equal("orchestrator", h.Notes.Single().By);
        Assert.Equal("state-transition", Violation(() => h.Update("", HandoffStatus.Open, null)));
    }
}
