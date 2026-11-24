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
    /// <summary>Runs a check and turns any exception into a JSON result with "ok": false and the reason, instead of a crash.</summary>
    public static async Task<int> ReportFailuresAsync(Func<Task<int>> check)
    {
        try { return await check(); }
        catch (Exception ex)
        {
            Console.WriteLine(Json.Of(new
            {
                ok = false,
                error = ex.Message,
                hint = "See the warnings above for which engine could not start. On Linux, b-speak needs libgomp1 (speech to text) and libasound2 (audio).",
            }));
            return ExitCodes.Rule;
        }
    }

    /// <summary>
    /// Writes a reply's raw synthesized audio and what playback would send to the device, piece by piece,
    /// and reports where speech starts in each, for diagnosing clipped or noisy starts.
    /// </summary>
    public static async Task<int> DumpAsync(string dir, string? provider)
    {
        var config = Profile.LoadConfig();
        if (provider is not null) config.Tts.Providers = [provider];
        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = factory.CreateLogger("bspeak");
        var store = new ModelStore(Profile.ModelsDir, config.Stt, config.Tts, logger);
        var tts = await SpeechEngines.StartTtsAsync(config.Tts, store, new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, logger);
        var voice = config.Tts.RoomVoices[0];
        Directory.CreateDirectory(dir);

        var sentences = Domain.SpokenText.Sentences("Okay, I looked into it, and the flaky test was racing the session refresh. Want me to commit it?");
        var pieces = AudioPlayback.FirstAudioChunks(sentences[0]).Concat(sentences.Skip(1)).ToList();
        var report = new List<object>();
        for (var p = 0; p < pieces.Count; p++)
        {
            var raw = new List<float>();
            var rendered = new List<float>();
            var resampler = new PushResampler(24_000, config.Audio.DeviceSampleRate);
            var fade = new OnsetFade(24_000);
            var buffer = new float[16_384];
            await foreach (var chunk in tts.StreamAsync(pieces[p], voice))
            {
                raw.AddRange(chunk);
                var copy = (float[])chunk.Clone();
                fade.Apply(copy);
                resampler.Write(copy);
                int n;
                while ((n = resampler.Read(buffer)) > 0) rendered.AddRange(buffer[..n]);
            }
            int m;
            while ((m = resampler.Drain(buffer)) > 0) rendered.AddRange(buffer[..m]);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"piece{p}-raw.wav"), WavAudio.EncodePcm16(raw.ToArray(), 24_000));
            await File.WriteAllBytesAsync(Path.Combine(dir, $"piece{p}-rendered.wav"), WavAudio.EncodePcm16(rendered.ToArray(), config.Audio.DeviceSampleRate));
            report.Add(new
            {
                text = pieces[p],
                rawSpeechStartsMs = SpeechStartMs(raw, 24_000),
                renderedSpeechStartsMs = SpeechStartMs(rendered, config.Audio.DeviceSampleRate),
                rawSeconds = Math.Round(raw.Count / 24_000.0, 2),
                renderedSeconds = Math.Round(rendered.Count / (double)config.Audio.DeviceSampleRate, 2),
            });
        }
        Console.WriteLine(Json.Of(new { provider = tts.Active, dir, pieces = report }));
        return ExitCodes.Ok;
    }

    /// <summary>First 10 ms window whose RMS reaches -30 dBFS: roughly where audible speech begins.</summary>
    private static int SpeechStartMs(List<float> samples, int rate)
    {
        var window = rate / 100;
        for (var i = 0; i + window <= samples.Count; i += window)
        {
            double sum = 0;
            for (var j = i; j < i + window; j++) sum += samples[j] * samples[j];
            if (Math.Sqrt(sum / window) > 0.0316) return i * 1000 / rate;
        }
        return -1;
    }

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
        clock.Restart();
        long firstAudioMs = -1;
        await foreach (var _ in tts.StreamAsync(Sentence, voice)) if (firstAudioMs < 0) firstAudioMs = clock.ElapsedMilliseconds; // time until playback could start

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
            var heard = (float[])speech.Clone();
            new OnsetFade(tts.SampleRate).Apply(heard); // as AudioPlayback does
            render.Write(Resampling.Resample(heard, tts.SampleRate, render.SampleRate));
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
            loadMs, firstTtsMs, ttsMs, shortTtsMs, firstAudioMs, firstSttMs, sttMs,
            whisperRuntime = stt is WhisperSpeechToText ? WhisperSpeechToText.Runtime : null,
            ttsProvider = tts.Active,
            ttsSkipped = tts.Skipped,
            played = play,
        }));
        return onset && committed is not null && match ? ExitCodes.Ok : ExitCodes.Rule;
    }
}
