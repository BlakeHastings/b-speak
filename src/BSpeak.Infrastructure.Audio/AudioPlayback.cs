using System.Collections.Concurrent;
using System.Threading.Channels;
using BSpeak.Application;
using Microsoft.Extensions.Logging;

namespace BSpeak.Infrastructure.Audio;

/// <summary>
/// The render side of <see cref="IPlayback"/>: synthesizes a granted speech sentence by sentence, resamples
/// it to the device rate and queues it on the render ring, with markers that report back to the room when
/// playback really started, when each sentence was heard, and when the last sample played.
/// </summary>
public sealed class AudioPlayback(RenderDevice render, ITextToSpeech tts, ILogger logger) : IPlayback
{
    private abstract record Work;
    private sealed record PlayWork(PlaybackRequest Request, CancellationToken Token) : Work;
    private sealed record EarconWork(Earcon Earcon) : Work;

    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private RoomService? _room;

    /// <summary>Connects the room to report progress to. Required before the first play.</summary>
    public void Attach(RoomService room) => _room = room;

    public void Play(PlaybackRequest request)
    {
        var cts = new CancellationTokenSource();
        _active[request.SpeechId] = cts;
        _work.Writer.TryWrite(new PlayWork(request, cts.Token));
    }

    public void Stop(string speechId)
    {
        if (_active.TryRemove(speechId, out var cts)) cts.Cancel();
        render.Flush();
    }

    public void PlayEarcon(Earcon earcon) => _work.Writer.TryWrite(new EarconWork(earcon));

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(ct))
            {
                switch (work)
                {
                    case EarconWork e:
                        render.Write(Earcons.Render(e.Earcon, render.SampleRate));
                        render.EndOfData();
                        break;
                    case PlayWork p:
                        await SpeakAsync(p.Request, p.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Splits a long first sentence at its first natural pause (comma, semicolon, colon or dash) so a short
    /// opening clause is synthesized and playing while the rest is generated. Short sentences stay whole.
    /// </summary>
    public static IReadOnlyList<string> FirstAudioChunks(string sentence, int minHead = 12, int splitAbove = 60)
    {
        if (sentence.Length <= splitAbove) return [sentence];
        for (var i = minHead; i < sentence.Length - minHead; i++)
        {
            var c = sentence[i];
            var pause = c is ',' or ';' or ':' || (c == '-' && i > 0 && sentence[i - 1] == ' ');
            if (pause && i + 1 < sentence.Length && sentence[i + 1] == ' ')
                return [sentence[..(i + 1)].Trim(), sentence[(i + 1)..].Trim()];
        }
        return [sentence];
    }

    private IAsyncEnumerable<float[]> Generate(string text, string voice, CancellationToken ct) =>
        tts is IStreamingTextToSpeech streaming ? streaming.StreamAsync(text, voice, ct) : Once(tts.SynthesizeAsync(text, voice, ct));

    private static async IAsyncEnumerable<float[]> Once(Task<float[]> audio)
    {
        yield return await audio;
    }

    private async Task SpeakAsync(PlaybackRequest request, CancellationToken ct)
    {
        var room = _room ?? throw new InvalidOperationException("AudioPlayback is not attached to a room.");
        var id = request.SpeechId;
        var started = false;
        var generation = render.Generation; // a barge-in flush advances it, so late chunks from this speech are dropped
        try
        {
            for (var i = 0; i < request.Sentences.Count; i++)
            {
                // Only the first sentence is split: later ones are synthesized while earlier ones play.
                foreach (var chunk in i == 0 ? FirstAudioChunks(request.Sentences[i]) : [request.Sentences[i]])
                {
                    // Audio arrives in pieces from a streaming engine (one piece otherwise) and is played as it comes.
                    PushResampler? resampler = null;
                    OnsetFade? fade = null;
                    var buffer = new float[16_384];
                    await foreach (var pcm in Generate(chunk, request.Voice, ct))
                    {
                        if (ct.IsCancellationRequested) return;
                        resampler ??= new PushResampler(tts.SampleRate, render.SampleRate);
                        fade ??= new OnsetFade(tts.SampleRate);
                        fade.Apply(pcm);
                        resampler.Write(pcm);
                        int n;
                        while ((n = resampler.Read(buffer)) > 0) Play(buffer.AsSpan(0, n));
                    }
                    if (ct.IsCancellationRequested) return;
                    if (resampler is not null)
                    {
                        int n;
                        while ((n = resampler.Drain(buffer)) > 0) Play(buffer.AsSpan(0, n));
                    }
                }
                var index = i;
                render.MarkAt(render.WritePosition, () => room.SentencePlayed(id, index));
            }
            void Play(ReadOnlySpan<float> samples)
            {
                var start = render.WritePosition;
                render.Write(samples, generation);
                if (render.Generation != generation) return;
                if (started) return;
                started = true;
                render.MarkAt(start + 1, () => room.PlaybackStarted(id));
            }

            if (ct.IsCancellationRequested) return;
            if (!started) { room.PlaybackStarted(id); room.PlaybackDrained(id); return; }
            render.MarkAt(render.WritePosition, () => room.PlaybackDrained(id));
            render.EndOfData();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "[tts] could not play speech {Id}", id);
            room.PlaybackFailed(id);
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Silences the first 10 ms of each synthesized piece and ramps in over the next 10 ms. Neural codec
/// decoders (Phonon's among them) emit a small noise burst as they start, heard as a click against
/// silence; speech itself does not begin that early, so nothing real is lost.
/// </summary>
public sealed class OnsetFade(int sampleRate, int muteMs = 10, int rampMs = 10)
{
    private readonly int _mute = sampleRate * muteMs / 1000;
    private readonly int _ramp = Math.Max(1, sampleRate * rampMs / 1000);
    private int _position;

    /// <summary>Applies the fade in place. Call with consecutive chunks of one piece.</summary>
    public void Apply(float[] chunk)
    {
        for (var i = 0; i < chunk.Length && _position < _mute + _ramp; i++, _position++)
            chunk[i] *= _position < _mute ? 0f : (float)(0.5 - 0.5 * Math.Cos(Math.PI * (_position - _mute) / _ramp));
    }
}

/// <summary>
/// Short tones: falling for "stopped listening", rising for "listening again", and a soft single
/// note with a gentle decay when an agent raises a hand.
/// </summary>
public static class Earcons
{
    public static float[] Render(Earcon earcon, int sampleRate) => earcon switch
    {
        Earcon.ListeningPaused => [.. Tone(880, 0.09, sampleRate), .. new float[sampleRate / 40], .. Tone(587, 0.12, sampleRate)],
        Earcon.HandRaised => Chime(1046.5, 0.35, sampleRate),
        _ => [.. Tone(587, 0.09, sampleRate), .. new float[sampleRate / 40], .. Tone(880, 0.12, sampleRate)],
    };

    private static float[] Tone(double hz, double seconds, int rate)
    {
        var n = (int)(seconds * rate);
        var samples = new float[n];
        var fade = Math.Max(1, rate / 200);
        for (var i = 0; i < n; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
            samples[i] = (float)(0.18 * envelope * Math.Sin(2 * Math.PI * hz * i / rate));
        }
        return samples;
    }

    /// <summary>A quiet bell-like note: quick attack, exponential decay.</summary>
    private static float[] Chime(double hz, double seconds, int rate)
    {
        var n = (int)(seconds * rate);
        var samples = new float[n];
        var attack = Math.Max(1, rate / 200);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var envelope = Math.Min(1.0, i / (double)attack) * Math.Exp(-t * 9) * Math.Min(1.0, (n - 1 - i) / (double)attack);
            samples[i] = (float)(0.12 * envelope * (Math.Sin(2 * Math.PI * hz * t) + 0.3 * Math.Sin(4 * Math.PI * hz * t)));
        }
        return samples;
    }
}
