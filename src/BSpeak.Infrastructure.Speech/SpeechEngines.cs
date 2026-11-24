using BSpeak.Application;
using Microsoft.Extensions.Logging;

namespace BSpeak.Infrastructure.Speech;

/// <summary>Builds the configured speech engines.</summary>
public static class SpeechEngines
{
    /// <summary>The step-down list for text to speech, in the order config.json gives.</summary>
    public static IReadOnlyList<TtsCandidate> TtsCandidates(TtsOptions options, ModelStore store, HttpClient http, ILogger logger) =>
        options.Providers.Select(name => name.ToLowerInvariant() switch
        {
            "phonon-server" => new TtsCandidate("phonon-server", options.VoicesFor("phonon-server"), async ct =>
            {
                var endpoint = await PhononServer.EnsureRunningAsync(options.PhononServer, http, logger, ct);
                return new OpenAiTextToSpeech(new OpenAiTtsOptions { Endpoint = endpoint, Model = "phonon", Streaming = true }, http);
            }),
            "phonon" => new TtsCandidate("phonon", options.VoicesFor("phonon"), async ct =>
            {
                if (PhononTextToSpeech.CheckSupported(options.Phonon) is { } why) throw new PlatformNotSupportedException(why);
                var exe = await store.EnsurePhononAsync(ct);
                return new PhononTextToSpeech(exe, store.PhononModelDir, options.Phonon);
            }),
            "kokoro" => new TtsCandidate("kokoro", options.VoicesFor("kokoro"), async ct =>
            {
                await store.EnsureKokoroAsync(ct);
                return new KokoroTextToSpeech(store.KokoroPath, options.Kokoro.Speed, options.Kokoro.Device);
            }),
            "openai" => new TtsCandidate("openai", options.VoicesFor("openai"), _ =>
                string.IsNullOrWhiteSpace(options.OpenAi.Endpoint)
                    ? throw new InvalidOperationException("tts.openAi.endpoint is not set.")
                    : Task.FromResult<ITextToSpeech>(new OpenAiTextToSpeech(options.OpenAi, http))),
            _ => new TtsCandidate(name, [], _ => throw new ArgumentException($"Unknown text to speech provider '{name}'. Use phonon-server, phonon, kokoro or openai.")),
        }).ToList();

    public static Task<TextToSpeechChain> StartTtsAsync(TtsOptions options, ModelStore store, HttpClient http, ILogger logger, CancellationToken ct = default) =>
        TextToSpeechChain.StartAsync(TtsCandidates(options, store, http, logger), logger, ct);

    public static ISpeechToText CreateStt(SttOptions options, ModelStore store, HttpClient http) =>
        options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiSpeechToText(options, http)
            : new WhisperSpeechToText(store.WhisperPath, options.Language);
}
