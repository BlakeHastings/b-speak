using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;

namespace BSpeak.Infrastructure.Tests;

public sealed class EndpointerTests
{
    // Default options: onset after 160 ms (5 windows), min speech 300 ms (~9), end of turn 900 ms (~28), 32 ms windows.
    private static readonly AudioOptions Options = new();
    private static readonly float[] Window = new float[Endpointer.WindowSamples];

    private static List<EndpointResult> Feed(Endpointer e, float probability, int windows) =>
        Enumerable.Range(0, windows).Select(_ => e.Process(Window, probability)).Where(r => r.Kind != EndpointKind.None).ToList();

    [Fact]
    public void Speech_announces_onset_then_commits_after_the_end_of_turn_silence()
    {
        var e = new Endpointer(Options);
        Assert.Empty(Feed(e, 0.0f, 10));
        var speaking = Feed(e, 0.9f, 40);
        Assert.Equal([EndpointKind.Onset], speaking.Select(r => r.Kind));
        Assert.Empty(Feed(e, 0.0f, 20));                   // a pause shorter than 900 ms keeps the turn open
        var ended = Feed(e, 0.0f, 20);
        var committed = Assert.Single(ended);
        Assert.Equal(EndpointKind.Committed, committed.Kind);
        // pre-roll (~8 windows) + 40 speech + 28 trailing windows
        Assert.InRange(committed.Audio!.Length / Endpointer.WindowSamples, 70, 80);
    }

    [Fact]
    public void Pauses_inside_a_sentence_stay_in_one_utterance()
    {
        var e = new Endpointer(Options);
        Feed(e, 0.9f, 20);
        Assert.Empty(Feed(e, 0.1f, 15));
        Assert.Empty(Feed(e, 0.9f, 20));                    // resumed: no second onset
        Assert.Equal(EndpointKind.Committed, Assert.Single(Feed(e, 0.0f, 40)).Kind);
    }

    [Fact]
    public void A_blip_shorter_than_onset_is_ignored_entirely()
    {
        var e = new Endpointer(Options);
        Assert.Empty(Feed(e, 0.9f, 2));
        Assert.Empty(Feed(e, 0.0f, 40));
    }

    [Fact]
    public void Speech_that_announced_onset_but_is_too_short_is_abandoned()
    {
        var e = new Endpointer(Options);
        Assert.Equal([EndpointKind.Onset], Feed(e, 0.9f, 6).Select(r => r.Kind));
        Assert.Equal(EndpointKind.Abandoned, Assert.Single(Feed(e, 0.0f, 40)).Kind);
    }

    [Fact]
    public void Long_speech_is_cut_at_the_maximum_length()
    {
        var e = new Endpointer(new AudioOptions { MaxUtteranceSeconds = 2 });
        var results = Feed(e, 0.9f, 80);
        Assert.Contains(results, r => r.Kind == EndpointKind.Committed);
    }
}

public sealed class FirstAudioChunkTests
{
    [Fact]
    public void Short_sentences_stay_whole() =>
        Assert.Equal(["Tests pass. Want the PR?"], AudioPlayback.FirstAudioChunks("Tests pass. Want the PR?"));

    [Fact]
    public void Long_sentences_split_at_the_first_natural_pause() =>
        Assert.Equal(
            ["Okay, I looked into it,", "and the flaky test is racing the session refresh on login."],
            AudioPlayback.FirstAudioChunks("Okay, I looked into it, and the flaky test is racing the session refresh on login."));

    [Fact]
    public void Numbers_with_commas_do_not_split() =>
        Assert.Single(AudioPlayback.FirstAudioChunks("There are 1,250 rows in the table that need a migration before we can ship this."));
}

public sealed class OnsetFadeTests
{
    [Fact]
    public void Mutes_the_decoder_burst_then_ramps_in_across_chunks()
    {
        var fade = new OnsetFade(24_000);                 // 240 samples muted, 240 ramped
        var first = Enumerable.Repeat(0.5f, 300).ToArray();
        var second = Enumerable.Repeat(0.5f, 300).ToArray();
        fade.Apply(first);
        fade.Apply(second);
        Assert.All(first[..240], s => Assert.Equal(0f, s));
        Assert.InRange(first[299], 0.01f, 0.1f);          // early in the ramp
        Assert.InRange(second[100], 0.36f, 0.39f);        // two thirds through the ramp: gain 0.75
        Assert.Equal(0.5f, second[299]);                  // untouched after the ramp
        var steps = first.Concat(second).Zip(first.Concat(second).Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Assert.True(steps < 0.01f, $"largest step {steps}");
    }
}

public sealed class EarconTests
{
    [Fact]
    public void The_hand_chime_is_short_quiet_starts_and_ends_at_silence_and_differs_from_the_listening_tones()
    {
        var chime = Earcons.Render(BSpeak.Application.Earcon.HandRaised, 48_000);
        var paused = Earcons.Render(BSpeak.Application.Earcon.ListeningPaused, 48_000);
        Assert.InRange(chime.Length / 48_000.0, 0.2, 0.5);
        Assert.InRange(chime.Max(Math.Abs), 0.05f, 0.2f);
        Assert.True(Math.Abs(chime[0]) < 0.001 && Math.Abs(chime[^1]) < 0.001, "no click at either end");
        Assert.NotEqual(paused.Length, chime.Length);
    }
}

public sealed class TranscriptCleanerTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", 3.0, "")]
    [InlineData(" (keyboard clicking) ", 2.0, "")]
    [InlineData("Thank you.", 0.8, "")]
    [InlineData("Thank you.", 3.0, "Thank you.")]
    [InlineData("Atlas, run the tests. [music]", 3.0, "Atlas, run the tests.")]
    public void Cleans_annotations_and_noise(string raw, double seconds, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.Clean(raw, seconds));
}

public sealed class AudioFormatTests
{
    [Fact]
    public void Wav_round_trips_through_pcm16()
    {
        var samples = Enumerable.Range(0, 1000).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        var (decoded, rate) = WavAudio.Decode(WavAudio.EncodePcm16(samples, 16_000));
        Assert.Equal(16_000, rate);
        Assert.Equal(samples.Length, decoded.Length);
        Assert.All(samples.Zip(decoded), p => Assert.InRange(p.Second - p.First, -0.001f, 0.001f));
    }

    [Fact]
    public void Resampling_keeps_duration()
    {
        var oneSecond = new float[24_000];
        var up = Resampling.Resample(oneSecond, 24_000, 48_000);
        Assert.InRange(up.Length, 47_000, 49_000);
    }

    [Fact]
    public void Push_resampler_converts_48k_to_16k_in_frames()
    {
        var r = new PushResampler(48_000, 16_000);
        var total = 0;
        var buffer = new float[4096];
        for (var i = 0; i < 100; i++)
        {
            r.Write(new float[480]);
            int n;
            while ((n = r.Read(buffer)) > 0) total += n;
        }
        Assert.InRange(total, 15_500, 16_000);
    }

    [Fact]
    public void Earcons_are_short_and_quiet()
    {
        var tone = Earcons.Render(BSpeak.Application.Earcon.ListeningPaused, 48_000);
        Assert.InRange(tone.Length / 48_000.0, 0.15, 0.4);
        Assert.All(tone, s => Assert.InRange(s, -0.2f, 0.2f));
    }
}
