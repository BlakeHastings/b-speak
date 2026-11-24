using BSpeak.Application;
using BSpeak.Infrastructure.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace BSpeak.Infrastructure.Tests;

public sealed class TextToSpeechChainTests
{
    private sealed class FakeTts(string name, int failAfter = int.MaxValue) : ITextToSpeech
    {
        public List<string> Voices { get; } = [];
        private int _calls;
        public int SampleRate => 24_000;

        public Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
        {
            if (++_calls > failAfter) throw new InvalidOperationException($"{name} broke");
            Voices.Add(voice);
            return Task.FromResult(new float[10]);
        }
    }

    private static TtsCandidate Works(string name, ITextToSpeech engine, params string[] voices) => new(name, voices, _ => Task.FromResult(engine));
    private static TtsCandidate CannotStart(string name, string why) => new(name, ["x"], _ => throw new PlatformNotSupportedException(why));

    [Fact]
    public async Task Uses_the_first_provider_that_starts()
    {
        var phonon = new FakeTts("phonon");
        var chain = await TextToSpeechChain.StartAsync([Works("phonon", phonon, "Miles", "Harper"), Works("kokoro", new FakeTts("kokoro"), "am_michael", "af_heart")], NullLogger.Instance);
        await chain.SynthesizeAsync("Hi.", "Harper");
        Assert.Equal("phonon", chain.Active);
        Assert.Equal("Harper", phonon.Voices.Last());
    }

    [Fact]
    public async Task Steps_down_past_providers_that_cannot_run_here()
    {
        var kokoro = new FakeTts("kokoro");
        var chain = await TextToSpeechChain.StartAsync([CannotStart("phonon", "this CPU lacks AVX2"), Works("kokoro", kokoro, "am_michael")], NullLogger.Instance);
        Assert.Equal("kokoro", chain.Active);
        Assert.Contains("phonon: this CPU lacks AVX2", chain.Skipped);
    }

    [Fact]
    public async Task A_provider_that_fails_its_warm_up_is_skipped()
    {
        var chain = await TextToSpeechChain.StartAsync([Works("phonon", new FakeTts("phonon", failAfter: 0), "Miles"), Works("kokoro", new FakeTts("kokoro"), "am_michael")], NullLogger.Instance);
        Assert.Equal("kokoro", chain.Active);
    }

    [Fact]
    public async Task Fails_over_mid_conversation_and_keeps_each_voice_in_its_slot()
    {
        var kokoro = new FakeTts("kokoro");
        var chain = await TextToSpeechChain.StartAsync(
            [Works("phonon", new FakeTts("phonon", failAfter: 2), "Miles", "Harper"), Works("kokoro", kokoro, "am_michael", "af_heart")], NullLogger.Instance);
        await chain.SynthesizeAsync("One.", "Miles");      // warm-up was call 1, this is call 2
        await chain.SynthesizeAsync("Two.", "Harper");     // phonon breaks: kokoro takes over with Harper's slot
        Assert.Equal("kokoro", chain.Active);
        Assert.Equal("af_heart", kokoro.Voices.Last());
        Assert.Contains(chain.Skipped, s => s.StartsWith("phonon: failed while speaking"));
    }

    [Fact]
    public async Task Throws_when_nothing_can_start()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TextToSpeechChain.StartAsync([CannotStart("phonon", "no binary")], NullLogger.Instance));
        Assert.Contains("phonon: no binary", ex.Message);
    }
}
