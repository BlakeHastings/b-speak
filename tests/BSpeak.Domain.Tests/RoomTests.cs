using BSpeak.Domain;

namespace BSpeak.Domain.Tests;

public sealed class RoomTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    private static Room RoomWith(params string[] names)
    {
        var room = new Room();
        foreach (var n in names) room.Join(n, T0);
        room.TakeEvents();
        return room;
    }

    private static T Single<T>(Room room) where T : DomainEvent => Assert.IsType<T>(Assert.Single(room.TakeEvents()));

    private static UserSpoke Say(Room room, string text, DateTimeOffset at)
    {
        room.RecordUtterance(text, at);
        return Single<UserSpoke>(room);
    }

    [Fact]
    public void Joining_hands_out_distinct_voices()
    {
        var room = new Room();
        var a = room.Join("Atlas", T0);
        var n = room.Join("Nova", T0);
        Assert.NotEqual(a.Voice, n.Voice);
        Assert.Equal(["Atlas", "Nova"], room.TakeEvents().Cast<ParticipantJoined>().Select(e => e.Name));
    }

    [Fact]
    public void Voices_change_to_one_the_engine_has_and_nobody_else_uses()
    {
        var room = RoomWith("Atlas", "Nova");
        string[] available = ["Miles", "Harper", "Maeve"];
        var novasVoice = room.Find("Nova")!.Voice;

        room.ChangeVoice("atlas", "maeve", available);
        Assert.Equal("Maeve", room.Find("Atlas")!.Voice);                   // canonical spelling from the catalog
        Assert.Equal(new VoiceChanged("Atlas", "Maeve"), Assert.Single(room.TakeEvents()));

        Assert.Equal("voice-unknown", Violation(() => room.ChangeVoice("Atlas", "Zelda", available)));
        room.ChangeVoice("Nova", "Harper", available);
        Assert.Equal("voice-taken", Violation(() => room.ChangeVoice("Atlas", "harper", available)));
        room.ChangeVoice("Atlas", "Maeve", available);                       // already hers: no event
        Assert.Single(room.TakeEvents());
        Assert.NotEqual(novasVoice, room.Find("Nova")!.Voice);
    }

    [Fact]
    public void Joining_takes_a_remembered_voice_unless_someone_has_it()
    {
        var room = new Room();
        room.Join("Atlas", T0, preferredVoice: "Harper");
        var nova = room.Join("Nova", T0, preferredVoice: "Harper");
        Assert.Equal("Harper", room.Find("Atlas")!.Voice);
        Assert.NotEqual("Harper", nova.Voice);
    }

    [Fact]
    public void Names_must_be_unique_and_easy_to_tell_apart()
    {
        var room = RoomWith("Nova");
        Assert.Equal("name-duplicate", Violation(() => room.Join("nova", T0)));
        Assert.Equal("name-similar", Violation(() => room.Join("Nora", T0)));
        room.Join("Atlas", T0);
    }

    [Fact]
    public void Addressing_a_participant_moves_focus_and_unaddressed_speech_keeps_it()
    {
        var room = RoomWith("Atlas", "Nova");

        var first = Say(room, "Atlas, run the tests.", T0);
        Assert.Equal((1, "Atlas", "Atlas"), (first.UtteranceId, first.AddressedTo, first.Focus));

        var second = Say(room, "Wait, all of them?", T0);
        Assert.Equal((2, null, "Atlas"), (second.UtteranceId, second.AddressedTo, second.Focus));

        var third = Say(room, "Everyone, take a break.", T0);
        Assert.Equal((AddressResolver.Everyone, "Atlas"), (third.AddressedTo, third.Focus));
    }

    [Fact]
    public void Relevant_utterances_track_addressee_focus_and_everyone()
    {
        var room = RoomWith("Atlas", "Nova");
        Say(room, "Atlas, run the tests.", T0);           // 1 -> Atlas
        Say(room, "Nova, keep going.", T0);               // 2 -> Nova, focus Nova
        Assert.Equal(1, room.LatestRelevantUtterance("Atlas"));
        Assert.Equal(2, room.LatestRelevantUtterance("Nova"));

        Say(room, "Sounds good.", T0);                    // 3 -> unaddressed, focus Nova
        Assert.Equal(1, room.LatestRelevantUtterance("Atlas"));
        Assert.Equal(3, room.LatestRelevantUtterance("Nova"));

        Say(room, "Everyone, pause.", T0);                // 4 -> both
        Assert.Equal(4, room.LatestRelevantUtterance("Atlas"));
        Assert.Equal(4, room.LatestRelevantUtterance("Nova"));
    }

    [Fact]
    public void Control_phrases_pause_and_resume_and_paused_speech_is_discarded()
    {
        var room = RoomWith("Atlas");

        room.RecordUtterance("Stop listening.", T0);
        Assert.Equal("voice", Single<ListeningPaused>(room).By);
        Assert.False(room.Listening);

        room.RecordUtterance("Atlas, are you there?", T0);
        Assert.Equal(DiscardReason.Paused, Single<UtteranceDiscarded>(room).Reason);
        Assert.Equal(0, room.UtteranceCount);

        room.RecordUtterance("Start listening.", T0);
        Single<ListeningResumed>(room);

        room.RecordUtterance("Start listening.", T0);
        Assert.Equal(DiscardReason.Control, Single<UtteranceDiscarded>(room).Reason);
    }

    [Fact]
    public void Pause_and_resume_are_idempotent()
    {
        var room = RoomWith();
        room.PauseListening("cli");
        room.PauseListening("cli");
        room.ResumeListening("cli");
        room.ResumeListening("cli");
        Assert.Equal([typeof(ListeningPaused), typeof(ListeningResumed)], room.TakeEvents().Select(e => e.GetType()));
    }

    [Fact]
    public void Empty_transcripts_are_discarded_and_end_the_turn()
    {
        var room = RoomWith();
        room.BeginUserTurn(T0);
        room.BeginUserTurn(T0);
        Single<UserStartedSpeaking>(room);
        room.RecordUtterance("  ...  ", T0);
        Assert.Equal(DiscardReason.Empty, Single<UtteranceDiscarded>(room).Reason);
        Assert.False(room.UserSpeaking);
    }

    [Fact]
    public void Wake_word_mode_delivers_only_utterances_that_start_with_it()
    {
        var room = RoomWith("Atlas");
        Assert.Equal("wake-word-required", Violation(() => room.SetMode(ListeningMode.WakeWord, " ")));
        room.SetMode(ListeningMode.WakeWord, "hey room");
        Single<ListeningModeChanged>(room);

        room.RecordUtterance("Atlas, run the tests.", T0);
        Assert.Equal(DiscardReason.NoWakeWord, Single<UtteranceDiscarded>(room).Reason);

        var spoke = Say(room, "Hey room, Atlas, run the tests.", T0);
        Assert.Equal(("Atlas, run the tests.", "Atlas"), (spoke.Text, spoke.AddressedTo));

        room.RecordUtterance("Stop listening.", T0);
        Single<ListeningPaused>(room);
    }

    [Fact]
    public void Floor_goes_in_order_and_moves_focus_to_the_speaker()
    {
        var room = RoomWith("Atlas", "Nova");
        room.RequestFloor("s1", "Nova");
        room.RequestFloor("s2", "atlas");

        Assert.Equal(new FloorSlot("s1", "Nova"), room.TryGrantFloor(T0));
        Assert.Null(room.TryGrantFloor(T0));
        Assert.Equal("Nova", room.Focus);

        room.ReleaseFloor("s1", T0);
        Assert.Null(room.TryGrantFloor(T0.AddMilliseconds(100)));
        Assert.Equal(new FloorSlot("s2", "Atlas"), room.TryGrantFloor(T0.AddSeconds(1)));
        Assert.Equal("Atlas", room.Focus);
        Assert.Equal(["s1", "s2"], room.TakeEvents().Cast<FloorGranted>().Select(e => e.SpeechId));
    }

    [Fact]
    public void Floor_is_never_granted_while_you_speak_or_inside_the_gap_after()
    {
        var room = RoomWith("Atlas");
        room.RequestFloor("s1", "Atlas");

        room.BeginUserTurn(T0);
        Assert.Null(room.TryGrantFloor(T0.AddSeconds(5)));

        room.RecordUtterance("Hold on.", T0.AddSeconds(5));
        Assert.Null(room.TryGrantFloor(T0.AddSeconds(5.5)));
        Assert.NotNull(room.TryGrantFloor(T0.AddSeconds(5.7)));
    }

    [Fact]
    public void Returned_floor_goes_back_to_the_front_of_the_queue()
    {
        var room = RoomWith("Atlas", "Nova");
        room.RequestFloor("s1", "Atlas");
        room.RequestFloor("s2", "Nova");
        room.TryGrantFloor(T0);
        room.ReturnFloor("s1", T0);
        Assert.Equal(["s1", "s2"], room.FloorQueue.Select(s => s.SpeechId));
    }

    [Fact]
    public void Leaving_clears_focus_and_unknown_participants_are_rejected()
    {
        var room = RoomWith("Atlas");
        Say(room, "Atlas, hi.", T0);
        room.Leave("Atlas");
        Assert.Null(room.Focus);
        Assert.Equal(LeaveReason.Left, Single<ParticipantLeft>(room).Reason);
        Assert.Equal("participant-unknown", Violation(() => room.Touch("Atlas", T0)));
    }

    [Fact]
    public void Idle_participants_expire_unless_active()
    {
        var room = RoomWith("Atlas", "Nova");
        var later = T0 + RoomSettings.Default.IdleTimeout + TimeSpan.FromSeconds(1);
        var expired = room.ExpireIdle(later, name => name == "Nova");
        Assert.Equal(["Atlas"], expired);
        Assert.Equal(LeaveReason.TimedOut, Single<ParticipantLeft>(room).Reason);
    }
}
