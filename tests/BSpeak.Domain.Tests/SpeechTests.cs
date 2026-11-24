using BSpeak.Domain;

namespace BSpeak.Domain.Tests;

public sealed class SpeechTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    private static Room RoomWith(params string[] names)
    {
        var room = new Room();
        foreach (var n in names) room.Join(n, T0);
        return room;
    }

    private static Speech Queue(Room room, string who = "Atlas", string text = "Tests pass. Want the PR?", int? replyTo = null) =>
        Speech.Queue("s1", who, text, replyTo, room, SpeechRules.Default);

    [Fact]
    public void Queues_plain_speech_split_into_sentences()
    {
        var speech = Queue(RoomWith("Atlas"), who: "atlas");
        Assert.Equal("Atlas", speech.Participant);
        Assert.Equal(["Tests pass.", "Want the PR?"], speech.Sentences);
        var e = Assert.IsType<SpeechQueued>(Assert.Single(speech.TakeEvents()));
        Assert.Equal(("s1", "Atlas", null), (e.SpeechId, e.Participant, e.ReplyTo));
    }

    [Fact]
    public void Rejects_what_cannot_be_said()
    {
        var room = RoomWith("Atlas");
        Assert.Equal("participant-unknown", Violation(() => Queue(room, who: "Nova")));
        Assert.Equal("speech-empty", Violation(() => Queue(room, text: "   ")));
        Assert.Equal("speech-markdown", Violation(() => Queue(room, text: "Changed:\n- parser")));
        Assert.Equal("speech-too-long", Violation(() => Queue(room, text: new string('a', 601))));
        Assert.Equal("reply-to-unknown", Violation(() => Queue(room, replyTo: 1)));
    }

    [Fact]
    public void Reply_is_refused_once_the_conversation_with_you_moved_on()
    {
        var room = RoomWith("Atlas", "Nova");
        room.RecordUtterance("Atlas, run the tests.", T0);   // 1 -> Atlas
        room.RecordUtterance("Nova, keep going.", T0);       // 2 -> Nova
        Queue(room, replyTo: 1);                             // 2 was for Nova: still fine

        room.RecordUtterance("Atlas, which parser?", T0);    // 3 -> Atlas
        Assert.Equal("conversation-moved-on", Violation(() => Queue(room, replyTo: 1)));
        Queue(room, replyTo: 3);

        room.RecordUtterance("Everyone, stop.", T0);         // 4 -> both
        Assert.Equal("conversation-moved-on", Violation(() => Queue(room, who: "Nova", replyTo: 2)));
    }

    [Fact]
    public void Unaddressed_utterances_count_for_whoever_holds_focus()
    {
        var room = RoomWith("Atlas", "Nova");
        room.RecordUtterance("Atlas, run the tests.", T0);   // 1, focus Atlas
        room.RecordUtterance("And the linter.", T0);         // 2, unaddressed, focus Atlas
        Assert.Equal("conversation-moved-on", Violation(() => Queue(room, replyTo: 1)));
    }

    [Fact]
    public void Plays_through_to_finished()
    {
        var speech = Queue(RoomWith("Atlas"));
        speech.Start("am_michael");
        speech.SentencePlayed(0);
        speech.Finish();
        Assert.Equal(SpeechState.Finished, speech.State);
        Assert.Equal("Tests pass. Want the PR?", speech.HeardText);
        Assert.Equal([typeof(SpeechQueued), typeof(PlaybackStarted), typeof(PlaybackFinished)], speech.TakeEvents().Select(e => e.GetType()));
    }

    [Fact]
    public void Interrupt_records_what_you_actually_heard()
    {
        var speech = Queue(RoomWith("Atlas"), text: "Tests finished. Two failures. Both in the parser.");
        speech.Start("am_michael");
        speech.SentencePlayed(0);
        speech.SentencePlayed(-1);
        speech.Interrupt();
        var e = Assert.IsType<SpeechInterrupted>(speech.TakeEvents().Last());
        Assert.Equal((0, "Tests finished."), (e.HeardThrough, e.HeardText));
    }

    [Fact]
    public void Interrupted_before_the_first_sentence_heard_nothing()
    {
        var speech = Queue(RoomWith("Atlas"));
        speech.Start("v");
        speech.Interrupt();
        Assert.Equal((-1, ""), (speech.HeardThrough, speech.HeardText));
    }

    [Fact]
    public void Only_your_own_queued_speech_can_be_withdrawn()
    {
        var speech = Queue(RoomWith("Atlas", "Nova"));
        Assert.Equal("speech-not-yours", Violation(() => speech.Withdraw("Nova", WithdrawReason.Withdrawn)));
        speech.Withdraw("Atlas", WithdrawReason.Withdrawn);
        Assert.Equal(SpeechState.Withdrawn, speech.State);
        Assert.Equal("state-transition", Violation(() => speech.Withdraw("Atlas", WithdrawReason.Withdrawn)));
    }

    [Fact]
    public void State_only_moves_forward()
    {
        var speech = Queue(RoomWith("Atlas"));
        Assert.Equal("state-transition", Violation(speech.Finish));
        Assert.Equal("state-transition", Violation(speech.Interrupt));
        speech.Start("v");
        Assert.Equal("state-transition", Violation(() => speech.Start("v")));
        Assert.Equal("state-transition", Violation(() => speech.Withdraw("Atlas", WithdrawReason.Withdrawn)));
        speech.Finish();
        Assert.Equal("state-transition", Violation(speech.Interrupt));
    }
}
