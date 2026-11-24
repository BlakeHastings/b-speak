using System.Threading.Channels;
using BSpeak.Application;
using Microsoft.Extensions.Logging;

namespace BSpeak.Infrastructure.Audio;

/// <summary>
/// The capture side: mic frames at the device rate, resampled to 16 kHz, cut into 32 ms windows, scored
/// by voice detection and segmented by the endpointer. Onset tells the room you started speaking; a
/// committed turn goes to speech to text on its own worker so capture never stalls; an abandoned turn
/// closes the turn with an empty transcript.
/// </summary>
public sealed class CapturePipeline(
    CaptureDevice capture,
    RenderDevice render,
    IVoiceActivityDetector vad,
    ISpeechToText stt,
    RoomService room,
    AudioOptions options,
    ILogger logger)
{
    private readonly Channel<float[]> _utterances = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(
        Task.Run(() => CaptureLoopAsync(ct), ct),
        Task.Run(() => TranscribeLoopAsync(ct), ct));

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var resampler = new PushResampler(capture.SampleRate, 16_000);
        var endpointer = new Endpointer(options);
        var pending = new List<float>(4096);
        var read = new float[8192];
        try
        {
            await foreach (var frame in capture.Frames.ReadAllAsync(ct))
            {
                resampler.Write(frame);
                int n;
                while ((n = resampler.Read(read)) > 0)
                    for (var i = 0; i < n; i++) pending.Add(read[i]);

                while (pending.Count >= Endpointer.WindowSamples)
                {
                    var window = pending.GetRange(0, Endpointer.WindowSamples).ToArray();
                    pending.RemoveRange(0, Endpointer.WindowSamples);
                    // Suppress by zeroing, never by dropping: the endpointer counts silence to end a turn.
                    if (options.MuteMicWhilePlaying && render.IsActive) Array.Clear(window);
                    Handle(endpointer.Process(window, vad.Probability(window)));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "[capture] capture loop stopped"); }
        finally { _utterances.Writer.TryComplete(); }
    }

    private void Handle(EndpointResult result)
    {
        switch (result.Kind)
        {
            case EndpointKind.Onset:
                logger.LogDebug("[capture] speech onset");
                room.SpeechOnset();
                break;
            case EndpointKind.Committed:
                logger.LogDebug("[capture] utterance {Seconds:F1}s", result.Audio!.Length / 16_000.0);
                _utterances.Writer.TryWrite(result.Audio);
                break;
            case EndpointKind.Abandoned:
                room.Utterance("");
                break;
        }
    }

    private async Task TranscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var audio in _utterances.Reader.ReadAllAsync(ct))
            {
                var text = "";
                var started = DateTime.UtcNow;
                try { text = await stt.TranscribeAsync(audio, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "[stt] transcription failed"); }
                logger.LogInformation("[stt] \"{Text}\" ({Ms} ms)", text, (int)(DateTime.UtcNow - started).TotalMilliseconds);
                room.Utterance(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
