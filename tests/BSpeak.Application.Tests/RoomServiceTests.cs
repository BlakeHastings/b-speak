using BSpeak.Application;
using BSpeak.Domain;

namespace BSpeak.Application.Tests;

public sealed class RoomServiceTests
{
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    /// <summary>The worked example from docs/model.html: two agents, turn-taking, barge-in, a stale reply, pause and resume.</summary>
    [Fact]
    public void Two_agents_share_the_room()
    {
        var h = new Harness();
        var room = h.Room;

        var atlas = room.Join("Atlas");
        var nova = room.Join("Nova");
        Assert.NotEqual(atlas.Voice, nova.Voice);
        Assert.Equal(0, atlas.After);

        // You address Atlas. Both hear it; only Atlas is meant.
        h.Say("Atlas, run the full test suite and tell me if anything breaks.");
        var forAtlas = Assert.Single(room.ListUtterances("Atlas", atlas.After));
        var forNova = Assert.Single(room.ListUtterances("Nova", nova.After));
        Assert.True(forAtlas.ForYou);
        Assert.False(forNova.ForYou);

        // You address Nova.
        h.Clock.Advance(5);
        h.Say("And Nova, keep going on the settings page.");
        Assert.Equal("Nova", room.Status().Focus);

        // Nova speaks: the floor is free, so it plays.
        h.Clock.Advance(10);
        var s1 = room.PostSpeech("Nova", "Settings page is done. Want me to wire up the save button next?", replyTo: 2);
        Assert.Equal(["play s1"], h.Playback.Calls);
        room.PlaybackStarted("s1");

        // Atlas queues behind Nova. Utterance 2 was for Nova, so Atlas's reply to 1 is not stale.
        var s2 = room.PostSpeech("Atlas", "Tests finished. Two failures, both in the parser.", replyTo: 1);
        Assert.Equal(SpeechState.Queued, s2.State);
        Assert.Equal(new FloorSlot("s1", "Nova"), room.Status().Floor);

        // Nova finishes; Atlas waits out the gap so you can answer first, then gets the floor.
        h.PlayThrough("s1");
        Assert.Null(room.Status().Floor);
        h.Clock.Advance(1);
        room.Tick();
        Assert.Equal(new FloorSlot("s2", "Atlas"), room.Status().Floor);
        room.PlaybackStarted("s2");
        room.SentencePlayed("s2", 0);

        // You barge in. Playback stops at once and Atlas learns what you heard.
        var mark = h.Log.Head;
        h.Say("Wait, which parser?");
        Assert.Equal(["UserStartedSpeaking", "SpeechInterrupted", "UserSpoke"], h.EventTypesSince(mark));
        Assert.Contains("stop s2", h.Playback.Calls);
        var interrupted = room.GetSpeech("s2");
        Assert.Equal((SpeechState.Interrupted, "Tests finished."), (interrupted.State, interrupted.HeardText));
        var question = room.ListUtterances("Atlas", 2).Single();
        Assert.Equal((3, null, "Atlas", true), (question.Id, question.AddressedTo, question.Focus, question.ForYou));
        Assert.False(room.ListUtterances("Nova", 2).Single().ForYou);

        // Atlas tries to finish its old sentence and is refused; it answers the question instead.
        Assert.Equal("conversation-moved-on", Violation(() => room.PostSpeech("Atlas", "Two failures, both in the parser.", replyTo: 1)));
        h.Clock.Advance(1);
        var s3 = room.PostSpeech("Atlas", "The date parser. Both failures are timezone handling.", replyTo: 3);
        Assert.Equal("s3", s3.Id);
        Assert.Equal("play s3", h.Playback.Calls.Last());
        h.PlayThrough("s3");

        // You pause listening. The phrase never reaches agents; a phone call is discarded unheard.
        mark = h.Log.Head;
        h.Say("Stop listening.");
        h.Say("Hey, can I call you back in five?");
        h.Say("Start listening.");
        Assert.Equal(
            ["UserStartedSpeaking", "ListeningPaused", "UserStartedSpeaking", "UtteranceDiscarded", "UserStartedSpeaking", "ListeningResumed"],
            h.EventTypesSince(mark));
        Assert.Equal(["earcon ListeningPaused", "earcon ListeningResumed"], h.Playback.Calls.TakeLast(2));
        Assert.Empty(room.ListUtterances("Atlas", 3));
        Assert.Equal(3, room.Status().Utterances);
    }

    [Fact]
    public async Task Utterance_wait_returns_when_you_speak()
    {
        var h = new Harness();
        var atlas = h.Room.Join("Atlas");
        var wait = h.Room.WaitForUtterancesAsync("Atlas", atlas.After, forYouOnly: false, TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);
        Assert.True(h.Room.Status().Participants.Single().Waiting);

        h.Say("Atlas, status?");
        var batch = await wait;
        Assert.False(batch.TimedOut);
        Assert.Equal("Atlas, status?", Assert.Single(batch.Utterances).Text);
        Assert.False(h.Room.Status().Participants.Single().Waiting);
    }

    [Fact]
    public async Task Utterance_wait_times_out()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        var batch = await h.Room.WaitForUtterancesAsync("Atlas", 0, false, TimeSpan.FromMilliseconds(50));
        Assert.True(batch.TimedOut);
        Assert.Empty(batch.Utterances);
    }

    [Fact]
    public async Task For_you_only_skips_utterances_meant_for_others()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.Join("Nova");
        var wait = h.Room.WaitForUtterancesAsync("Nova", 0, forYouOnly: true, TimeSpan.FromSeconds(5));
        h.Say("Atlas, run the tests.");
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        h.Say("Everyone, quick break.");
        var batch = await wait;
        Assert.Equal(2, Assert.Single(batch.Utterances).Id);
    }

    [Fact]
    public async Task Speech_wait_reports_the_outcome()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        var s = h.Room.PostSpeech("Atlas", "First point. Second point.", null);
        var wait = h.Room.WaitForSpeechAsync(s.Id, TimeSpan.FromSeconds(5));
        h.Room.PlaybackStarted(s.Id);
        h.Room.SentencePlayed(s.Id, 0);
        h.Say("Hang on.");
        var done = await wait;
        Assert.Equal((SpeechState.Interrupted, "First point."), (done!.State, done.HeardText));
    }

    [Fact]
    public void Barge_in_before_playback_starts_returns_the_speech_to_the_queue()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.PostSpeech("Atlas", "Done.", null);
        h.Say("Hold on a second.");
        Assert.Equal(["play s1", "stop s1"], h.Playback.Calls);
        Assert.Equal(SpeechState.Queued, h.Room.GetSpeech("s1").State);
        Assert.Equal(["s1"], h.Room.Status().Queue.Select(q => q.SpeechId));

        h.Clock.Advance(1);
        h.Room.Tick();
        Assert.Equal("play s1", h.Playback.Calls.Last());
    }

    [Fact]
    public void Withdrawing_a_granted_speech_stops_it_and_passes_the_floor()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.Join("Nova");
        h.Room.PostSpeech("Atlas", "One.", null);
        h.Room.PostSpeech("Nova", "Two.", null);
        Assert.Equal("speech-not-yours", Violation(() => h.Room.WithdrawSpeech("Nova", "s1")));
        h.Room.WithdrawSpeech("Atlas", "s1");
        Assert.Contains("stop s1", h.Playback.Calls);
        h.Clock.Advance(1);
        h.Room.Tick();
        Assert.Equal("play s2", h.Playback.Calls.Last());
    }

    [Fact]
    public void Idle_participants_time_out_and_their_queue_is_withdrawn()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.Join("Nova");
        h.Room.PostSpeech("Nova", "Holding the floor.", null);
        h.Room.PlaybackStarted("s1");
        h.Room.PostSpeech("Atlas", "Waiting my turn.", null);

        h.Clock.Advance(RoomSettings.Default.IdleTimeout.TotalSeconds + 1);
        h.Room.Tick();

        var status = h.Room.Status();
        Assert.Empty(status.Participants);
        Assert.Equal(SpeechState.Withdrawn, h.Room.GetSpeech("s2").State);
        Assert.Equal(SpeechState.Playing, h.Room.GetSpeech("s1").State);
    }

    [Fact]
    public void Agents_can_toggle_listening_from_the_cli()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.SetListening(false, "Atlas");
        h.Room.SetListening(true, null);
        var events = h.Log.After(0, int.MaxValue).Select(e => e.Event).ToList();
        Assert.Equal("cli:Atlas", events.OfType<ListeningPaused>().Single().By);
        Assert.Equal("cli", events.OfType<ListeningResumed>().Single().By);
    }

    [Fact]
    public void Joining_without_a_name_takes_a_free_distinct_one()
    {
        var h = new Harness();
        h.Room.Join("Nora");
        var first = h.Room.Join(null);
        var second = h.Room.Join(null);
        Assert.Equal("Atlas", first.Name);
        Assert.Equal("Juno", second.Name);   // Nova sounds like Nora
    }

    [Fact]
    public void Failed_playback_frees_the_floor()
    {
        var h = new Harness();
        h.Room.Join("Atlas");
        h.Room.PostSpeech("Atlas", "One.", null);
        h.Room.PostSpeech("Atlas", "Two.", null);
        h.Room.PlaybackFailed("s1");
        Assert.Equal(SpeechState.Withdrawn, h.Room.GetSpeech("s1").State);
        h.Clock.Advance(1);
        h.Room.Tick();
        Assert.Equal("play s2", h.Playback.Calls.Last());
    }

    [Fact]
    public void Unknown_speech_is_not_found()
    {
        var h = new Harness();
        Assert.Equal("speech-unknown", Assert.Throws<NotFoundException>(() => h.Room.GetSpeech("s9")).Code);
    }
}
