using BSpeak.Application;
using BSpeak.Domain;

namespace BSpeak.Application.Tests;

/// <summary>Hands up through the service: hands survive new utterances, stay revisable, and play when called on.</summary>
public sealed class HandsUpTests
{
    private static Harness HandsUp(params string[] names)
    {
        var h = new Harness();
        foreach (var n in names) h.Room.Join(n);
        h.Room.SetTurnMode(TurnMode.HandsUp, "test");
        h.Playback.Calls.Clear();
        return h;
    }

    [Fact]
    public void A_raised_hand_chimes_waits_and_plays_its_latest_text_when_called_on()
    {
        var h = HandsUp("Atlas");
        var hand = h.Room.PostSpeech("Atlas", "The parser has two failures.", null);
        Assert.True(hand.HandUp);
        Assert.Equal(["earcon HandRaised"], h.Playback.Calls);

        h.Clock.Advance(60);
        h.Room.Tick();
        Assert.DoesNotContain(h.Playback.Calls, c => c.StartsWith("play s"));

        var revised = h.Room.ReviseSpeech("Atlas", hand.Id, "Both parser failures are fixed now.");
        Assert.True(revised.HandUp);

        h.Room.CallOn(null, "cli");
        var played = h.Playback.Played.Single(p => p.SpeechId == hand.Id);
        Assert.Equal(["Both parser failures are fixed now."], played.Sentences);
    }

    [Fact]
    public void Saying_something_new_keeps_hands_up_and_tells_the_agent_its_hand_is_still_up()
    {
        var h = HandsUp("Atlas", "Nova");
        var hand = h.Room.PostSpeech("Nova", "I have a question about the schema.", null);

        h.Say("Atlas, what's the build status?");
        Assert.Equal(SpeechState.Queued, h.Room.GetSpeech(hand.Id).State);
        Assert.Equal(hand.Id, Assert.Single(h.Room.Turns().Hands).SpeechId);

        var inbox = h.Room.Inbox("Nova", 0);
        Assert.Contains(inbox.Items, i => i.Kind == "utterance" && i.ForYou == false);
        Assert.Equal((TurnMode.HandsUp, hand.Id), (inbox.Context!.Turns, inbox.Context.YourHand));
    }

    [Fact]
    public void A_direct_reply_plays_without_a_hand_while_other_hands_wait()
    {
        var h = HandsUp("Atlas", "Nova");
        h.Room.PostSpeech("Nova", "Can I raise something about the schema?", null);
        h.Say("Atlas, is the build green?");
        h.Clock.Advance(2);

        var reply = h.Room.PostSpeech("Atlas", "Yes, all green.", 1);
        Assert.False(reply.HandUp);
        h.Room.Tick();
        Assert.Contains($"play {reply.Id}", h.Playback.Calls);
        Assert.Single(h.Room.Turns().Hands);
    }

    [Fact]
    public void A_second_post_while_your_hand_is_up_is_refused_with_the_way_to_revise()
    {
        var h = HandsUp("Atlas");
        var hand = h.Room.PostSpeech("Atlas", "First thought.", null);
        var refused = Assert.Throws<DomainViolation>(() => h.Room.PostSpeech("Atlas", "Second thought.", null));
        Assert.Equal("hand-already-up", refused.Code);
        Assert.Contains($"speech patch {hand.Id}", refused.Message);
        Assert.Single(h.Room.ListSpeech("Atlas"));
    }

    [Fact]
    public void A_speech_about_to_play_can_no_longer_be_changed()
    {
        var h = HandsUp("Atlas");
        var hand = h.Room.PostSpeech("Atlas", "Ready when you are.", null);
        h.Room.CallOn("Atlas", "cli");
        Assert.Equal("speech-starting", Assert.Throws<DomainViolation>(() => h.Room.ReviseSpeech("Atlas", hand.Id, "Changed.")).Code);
    }

    [Fact]
    public void Asking_who_has_a_hand_up_is_answered_out_loud_in_a_voice_no_agent_uses()
    {
        var h = HandsUp("Atlas", "Nova");
        h.Room.PostSpeech("Nova", "One thing on the schema.", null);
        h.Room.PostSpeech("Atlas", "One thing on the parser.", null);
        h.Playback.Played.Clear();

        h.Say("Who has their hand up?");
        var answer = Assert.Single(h.Playback.Played);
        Assert.Equal("Nova and Atlas have hands up. Nova was first.", string.Join(" ", answer.Sentences));
        Assert.DoesNotContain(answer.Voice, h.Room.Status().Participants.Select(p => p.Voice));
        Assert.Equal(2, h.Room.Turns().Hands.Count);
    }

    [Fact]
    public void Talking_over_an_announcement_stops_it()
    {
        var h = HandsUp("Atlas");
        h.Say("Any hands?");
        var id = h.Playback.Played.Last().SpeechId;
        h.Room.SpeechOnset();
        Assert.Contains($"stop {id}", h.Playback.Calls);
    }

    [Fact]
    public void Going_back_to_an_open_floor_plays_the_raised_hands_in_order()
    {
        var h = HandsUp("Atlas", "Nova");
        var first = h.Room.PostSpeech("Nova", "Schema question.", null);
        var second = h.Room.PostSpeech("Atlas", "Parser update.", null);

        h.Room.SetTurnMode(TurnMode.Open, "cli");
        Assert.Contains($"play {first.Id}", h.Playback.Calls);
        h.PlayThrough(first.Id);
        h.Clock.Advance(2);
        h.Room.Tick();
        Assert.Contains($"play {second.Id}", h.Playback.Calls);
    }
}
