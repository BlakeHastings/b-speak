using BSpeak.Domain;

namespace BSpeak.Domain.Tests;

/// <summary>Hands up: agents raise a hand and wait to be called on, like a large video call.</summary>
public sealed class HandsUpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    private static Room HandsUpRoom(params string[] names)
    {
        var room = new Room();
        foreach (var n in names) room.Join(n, T0);
        room.SetTurnMode(TurnMode.HandsUp, "test");
        room.TakeEvents();
        return room;
    }

    [Theory]
    [InlineData("Hands up.", TurnAction.HandsUp)]
    [InlineData("OK, hands up mode please.", TurnAction.HandsUp)]
    [InlineData("Open floor.", TurnAction.OpenFloor)]
    [InlineData("Hands down.", TurnAction.OpenFloor)]
    [InlineData("Who has their hand up?", TurnAction.AskHands)]
    [InlineData("Any hands?", TurnAction.AskHands)]
    [InlineData("Next.", TurnAction.Next)]
    [InlineData("Go ahead.", TurnAction.Next)]
    [InlineData("Next, let's look at the parser.", null)]
    [InlineData("Put your hands up if you are done.", null)]
    public void Turn_phrases_match_only_the_whole_utterance(string said, TurnAction? expected) =>
        Assert.Equal(expected, TurnPhrases.Match(said));

    [Theory]
    [InlineData("Atlas, go ahead.", true)]
    [InlineData("Atlas?", true)]
    [InlineData("OK Atlas, what's up?", true)]
    [InlineData("Atlas, your turn.", true)]
    [InlineData("Atlas, go ahead and fix the parser.", false)]
    [InlineData("Atlas, run the tests.", false)]
    public void A_name_with_a_go_ahead_calls_on_that_agent(string said, bool expected) =>
        Assert.Equal(expected, TurnPhrases.IsGoAhead(said, "Atlas"));

    [Fact]
    public void In_hands_up_mode_a_new_speech_raises_a_hand_and_is_never_granted_by_itself()
    {
        var room = HandsUpRoom("Atlas");
        Assert.True(room.RaisesHand("Atlas", null));
        room.RequestFloor("s1", "Atlas", asHand: true);

        Assert.Equal("s1", Assert.IsType<HandRaised>(Assert.Single(room.TakeEvents())).SpeechId);
        Assert.Null(room.TryGrantFloor(T0.AddMinutes(5)));
        Assert.Equal("s1", Assert.Single(room.Hands).SpeechId);
    }

    [Fact]
    public void One_hand_per_agent_so_it_revises_instead_of_stacking_messages()
    {
        var room = HandsUpRoom("Atlas");
        room.RequestFloor("s1", "Atlas", asHand: true);
        Assert.Equal("hand-already-up", Violation(() => room.RaisesHand("Atlas", null)));
    }

    [Fact]
    public void A_direct_reply_skips_the_line()
    {
        var room = HandsUpRoom("Atlas", "Nova");
        room.RequestFloor("s1", "Nova", asHand: true);
        room.RecordUtterance("Atlas, is the build green?", T0);
        room.TakeEvents();

        Assert.True(room.IsDirectReply("Atlas", 1));
        Assert.False(room.RaisesHand("Atlas", 1));
        Assert.True(room.RaisesHand("Atlas", null));   // speaking up unprompted still needs a hand
        Assert.False(room.IsDirectReply("Nova", 1));   // Nova was not asked

        room.RequestFloor("s2", "Atlas", asHand: false);
        Assert.Equal("s2", room.TryGrantFloor(T0.AddSeconds(5))!.SpeechId);
        Assert.Equal("s1", Assert.Single(room.Hands).SpeechId);
    }

    [Fact]
    public void Calling_on_moves_a_hand_to_the_front_of_the_line()
    {
        var room = HandsUpRoom("Atlas", "Nova");
        room.RequestFloor("s1", "Atlas", asHand: true);
        room.RequestFloor("s2", "Nova", asHand: true);
        room.TakeEvents();

        Assert.Equal("s2", room.CallOn("Nova", "cli").SpeechId);
        Assert.IsType<HandCalledOn>(Assert.Single(room.TakeEvents()));
        Assert.Equal("s2", room.TryGrantFloor(T0)!.SpeechId);
        Assert.Equal("s1", Assert.Single(room.Hands).SpeechId);

        Assert.Equal("hand-not-up", Violation(() => room.CallOn("Nova", "cli")));
        Assert.Equal("s1", room.CallOn(null, "cli").SpeechId);  // no name: the first one up
        Assert.Equal("no-hands", Violation(() => room.CallOn(null, "cli")));
    }

    [Fact]
    public void Saying_a_name_with_a_hand_up_calls_on_it_and_is_not_an_utterance()
    {
        var room = HandsUpRoom("Atlas", "Nova");
        room.RequestFloor("s1", "Atlas", asHand: true);
        room.TakeEvents();

        room.RecordUtterance("Atlas, go ahead.", T0);
        var called = Assert.IsType<HandCalledOn>(Assert.Single(room.TakeEvents()));
        Assert.Equal(("Atlas", "voice"), (called.Participant, called.By));
        Assert.Equal(0, room.UtteranceCount);

        room.RecordUtterance("Nova, go ahead.", T0);   // no hand up: an ordinary utterance
        Assert.IsType<UserSpoke>(Assert.Single(room.TakeEvents()));
    }

    [Fact]
    public void Next_calls_on_the_first_hand_and_is_ordinary_speech_when_no_hands_are_up()
    {
        var room = HandsUpRoom("Atlas", "Nova");
        room.RecordUtterance("Next.", T0);
        Assert.IsType<UserSpoke>(Assert.Single(room.TakeEvents()));

        room.RequestFloor("s1", "Nova", asHand: true);
        room.RequestFloor("s2", "Atlas", asHand: true);
        room.TakeEvents();
        room.RecordUtterance("Next.", T0);
        Assert.Equal("Nova", Assert.IsType<HandCalledOn>(Assert.Single(room.TakeEvents())).Participant);
    }

    [Fact]
    public void Voice_switches_the_mode_and_back_to_open_floor_lowers_hands_into_the_line_in_order()
    {
        var room = new Room();
        room.Join("Atlas", T0);
        room.Join("Nova", T0);
        room.TakeEvents();

        room.RecordUtterance("Hands up.", T0);
        Assert.Equal(TurnMode.HandsUp, Assert.IsType<TurnModeChanged>(Assert.Single(room.TakeEvents())).Mode);
        room.RequestFloor("s1", "Nova", asHand: true);
        room.RequestFloor("s2", "Atlas", asHand: true);
        room.TakeEvents();

        room.RecordUtterance("Open floor.", T0);
        Assert.IsType<TurnModeChanged>(Assert.Single(room.TakeEvents()));
        Assert.Empty(room.Hands);
        Assert.Equal(["s1", "s2"], room.FloorQueue.Select(s => s.SpeechId));

        room.RecordUtterance("Open floor.", T0);
        Assert.Equal(DiscardReason.Control, Assert.IsType<UtteranceDiscarded>(Assert.Single(room.TakeEvents())).Reason);
    }

    [Fact]
    public void Open_floor_never_raises_hands()
    {
        var room = new Room();
        room.Join("Atlas", T0);
        Assert.False(room.RaisesHand("Atlas", null));
    }

    [Fact]
    public void A_lowered_hand_leaves_the_list()
    {
        var room = HandsUpRoom("Atlas");
        room.RequestFloor("s1", "Atlas", asHand: true);
        room.ReleaseFloor("s1", T0);
        Assert.Empty(room.Hands);
    }
}
