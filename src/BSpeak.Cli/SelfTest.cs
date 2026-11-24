using System.Diagnostics;
using BSpeak.Application;
using BSpeak.Domain;
using BSpeak.Host;
using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;
using Microsoft.Extensions.Logging;

namespace BSpeak.Cli;

/// <summary>
/// Checks the speech engines end to end without a microphone: text to speech produces a sentence, which
/// goes through the real voice detection, endpointer and speech to text. With --play it is also played
/// on the configured output device, so you can hear the voice.
/// </summary>
public static class SelfTest
{
    private const string Sentence = "Atlas, please run the tests and tell me what breaks.";

    /// <param name="provider">Test only this text to speech provider instead of the configured list.</param>
    public static async Task<int> RunAsync(bool play, string? provider = null)
    {
        var config = Profile.LoadConfig();
        if (provider is not null) config.Tts.Providers = [provider];
        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = factory.CreateLogger("bspeak");
        var store = new ModelStore(Profile.ModelsDir, config.Stt, config.Tts, logger);
        await store.EnsureCoreAsync();

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var clock = Stopwatch.StartNew();
        var tts = await SpeechEngines.StartTtsAsync(config.Tts, store, http, logger);
        var stt = SpeechEngines.CreateStt(config.Stt, store, http);
        var vad = new SileroVad(store.SileroPath);
        var loadMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var voice = config.Tts.RoomVoices[0];
        var speech = await tts.SynthesizeAsync(Sentence, voice);
        var firstTtsMs = clock.ElapsedMilliseconds;
        clock.Restart();
        speech = await tts.SynthesizeAsync(Sentence, voice); // warm: what each sentence costs in a conversation
        var ttsMs = clock.ElapsedMilliseconds;
        clock.Restart();
        await tts.SynthesizeAsync("Sure.", voice);
        var shortTtsMs = clock.ElapsedMilliseconds;

        var audio16k = Resampling.Resample(speech, tts.SampleRate, 16_000);
        var padded = new float[16_000].Concat(audio16k).Concat(new float[32_000]).ToArray();
        var endpointer = new Endpointer(config.Audio);
        var onset = false;
        float[]? committed = null;
        for (var i = 0; i + Endpointer.WindowSamples <= padded.Length; i += Endpointer.WindowSamples)
        {
            var window = padded[i..(i + Endpointer.WindowSamples)];
            var result = endpointer.Process(window, vad.Probability(window));
            if (result.Kind == EndpointKind.Onset) onset = true;
            if (result.Kind == EndpointKind.Committed) committed ??= result.Audio;
        }

        clock.Restart();
        var transcript = committed is null ? "" : await stt.TranscribeAsync(committed);
        var firstSttMs = clock.ElapsedMilliseconds;
        clock.Restart();
        if (committed is not null) transcript = await stt.TranscribeAsync(committed); // warm: what a real turn costs
        var sttMs = clock.ElapsedMilliseconds;
        var match = Words.Normalize(transcript) == Words.Normalize(Sentence);

        if (play)
        {
            PortAudioSystem.EnsureInitialized();
            using var render = new RenderDevice(config.Audio);
            render.Start();
            var done = new TaskCompletionSource();
            render.Write(Resampling.Resample(speech, tts.SampleRate, render.SampleRate));
            render.MarkAt(render.WritePosition, () => done.TrySetResult());
            render.EndOfData();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Console.WriteLine(Json.Of(new
        {
            ok = onset && committed is not null && match,
            said = Sentence,
            heard = transcript,
            voice,
            speechSeconds = Math.Round(speech.Length / (double)tts.SampleRate, 2),
            voiceDetected = onset,
            turnDetected = committed is not null,
            loadMs, firstTtsMs, ttsMs, shortTtsMs, firstSttMs, sttMs,
            whisperRuntime = stt is WhisperSpeechToText ? WhisperSpeechToText.Runtime : null,
            ttsProvider = tts.Active,
            ttsSkipped = tts.Skipped,
            played = play,
        }));
        return onset && committed is not null && match ? ExitCodes.Ok : ExitCodes.Rule;
    }
}
