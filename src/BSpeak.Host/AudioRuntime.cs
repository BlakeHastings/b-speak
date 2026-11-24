using BSpeak.Application;
using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;
using Microsoft.Extensions.Logging;

namespace BSpeak.Host;

/// <param name="Tts">The text to speech provider speaking now.</param>
/// <param name="TtsSkipped">Providers passed over on the way down the list, with why.</param>
public sealed record AudioStatus(string State, string? Error, string? Input, string? Output, string Stt, string Tts, IReadOnlyList<string> TtsSkipped);

/// <summary>
/// Brings the audio side up in the background: downloads missing models, loads the speech engines,
/// opens the devices and starts the capture and render pipelines. Until it is ready, speech waits in
/// the <see cref="PlaybackRouter"/>; if it fails, the host falls back to printing speech.
/// </summary>
public sealed class AudioRuntime(BSpeakConfig config, RoomService room, PlaybackRouter router, SimulatedPlayback fallback, ILogger logger)
{
    private volatile string _state = "starting";
    private string? _error;
    private CaptureDevice? _capture;
    private RenderDevice? _render;
    private TextToSpeechChain? _tts;

    public AudioStatus Status => new(_state, _error, _capture?.DeviceName, _render?.DeviceName, Describe(config.Stt),
        _tts?.Active ?? $"not started (will try {string.Join(", then ", config.Tts.Providers)})", _tts?.Skipped ?? []);

    private static string Describe(SttOptions o) => o.Provider.Equals("whisper", StringComparison.OrdinalIgnoreCase)
        ? $"whisper {o.Model}{(WhisperSpeechToText.Runtime == "unknown" ? "" : $" on {WhisperSpeechToText.Runtime}")}"
        : $"{o.Provider} {o.RemoteModel} at {o.Endpoint}";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!config.Audio.Enabled)
        {
            _state = "off";
            router.Use(fallback);
            logger.LogInformation("[audio] disabled: speech is printed here, and you type with: bspeak utterance post \"...\"");
            return;
        }

        try
        {
            _state = "downloading models";
            var store = new ModelStore(Profile.ModelsDir, config.Stt, config.Tts, logger);
            await store.EnsureCoreAsync(ct);

            _state = "loading models";
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var vad = new SileroVad(store.SileroPath);
            var stt = SpeechEngines.CreateStt(config.Stt, store, http);
            await stt.TranscribeAsync(new float[16_000], ct); // warm up: the first call compiles GPU shaders
            if (stt is WhisperSpeechToText) logger.LogInformation("[stt] whisper {Model} on {Runtime}", config.Stt.Model, WhisperSpeechToText.Runtime);

            _state = "starting text to speech";
            _tts = await SpeechEngines.StartTtsAsync(config.Tts, store, http, logger, ct);
            var tts = _tts;
            await WarmVoicesAsync(tts, ct);

            _state = "opening devices";
            PortAudioSystem.EnsureInitialized();
            _render = new RenderDevice(config.Audio);
            _render.Start();
            _capture = new CaptureDevice(config.Audio);
            _capture.Start();
            logger.LogInformation("[audio] mic '{Input}', speaker '{Output}' at {Rate} Hz", _capture.DeviceName, _render.DeviceName, config.Audio.DeviceSampleRate);

            var playback = new AudioPlayback(_render, tts, logger);
            playback.Attach(room);
            var capture = new CapturePipeline(_capture, _render, vad, stt, room, config.Audio, logger);
            router.Use(playback);
            _state = "ready";
            logger.LogInformation("[audio] ready. Talk whenever you like.");
            playback.PlayEarcon(Earcon.ListeningResumed);
            await Task.WhenAll(playback.RunAsync(ct), capture.RunAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state = "failed";
            _error = ex.Message;
            logger.LogError(ex, "[audio] could not start audio; speech will be printed instead");
            router.Use(fallback);
        }
        finally
        {
            _capture?.Dispose();
            _render?.Dispose();
        }
    }

    /// <summary>Speaks once in every room voice, so a provider that fetches voices on first use (Phonon) has them all before anyone joins.</summary>
    private async Task WarmVoicesAsync(ITextToSpeech tts, CancellationToken ct)
    {
        foreach (var voice in config.Tts.RoomVoices.Skip(1))
            await tts.SynthesizeAsync("Ready.", voice, ct);
    }
}
