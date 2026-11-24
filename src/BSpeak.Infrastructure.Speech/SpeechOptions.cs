namespace BSpeak.Infrastructure.Speech;

/// <summary>Speech to text settings (the <c>stt</c> section of config.json).</summary>
public sealed class SttOptions
{
    /// <summary>"whisper" (local, default) or "openai" (any OpenAI-compatible /v1/audio/transcriptions endpoint).</summary>
    public string Provider { get; set; } = "whisper";

    /// <summary>Whisper model name: tiny.en, base.en, small.en, medium.en, large-v3-turbo, large-v3.</summary>
    public string Model { get; set; } = "small.en";

    public string Language { get; set; } = "en";

    /// <summary>Base URL ending in /v1 for the "openai" provider, for example a LiteLLM gateway.</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string RemoteModel { get; set; } = "whisper-1";
}

/// <summary>
/// Text to speech settings (the <c>tts</c> section of config.json). <see cref="Providers"/> is tried in
/// order: the first one that can run on this machine is used, and if it fails later the next one takes over.
/// </summary>
public sealed class TtsOptions
{
    /// <summary>
    /// "phonon" (Gradium's ptts runner, CPU), "kokoro" (in-process, runs anywhere .NET does), and "openai"
    /// (any OpenAI-compatible /v1/audio/speech server, for example ptts-openai-server, which streams).
    /// </summary>
    public List<string> Providers { get; set; } = ["phonon-server", "phonon", "kokoro"];

    public PhononServerOptions PhononServer { get; set; } = new();
    public PhononOptions Phonon { get; set; } = new();
    public KokoroOptions Kokoro { get; set; } = new();
    public OpenAiTtsOptions OpenAi { get; set; } = new();

    /// <summary>The voices a provider hands to participants, in order.</summary>
    public IReadOnlyList<string> VoicesFor(string provider) => provider.ToLowerInvariant() switch
    {
        "phonon" or "phonon-server" => Phonon.Voices.Count > 0 ? Phonon.Voices : PhononOptions.DefaultVoices,
        "openai" => OpenAi.Voices.Count > 0 ? OpenAi.Voices : OpenAiTtsOptions.DefaultVoices,
        _ => Kokoro.Voices.Count > 0 ? Kokoro.Voices : KokoroOptions.DefaultVoices,
    };

    /// <summary>The first provider's voices: what the room hands out. Other providers map to theirs by position.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> RoomVoices => VoicesFor(Providers.FirstOrDefault() ?? "kokoro");
}

/// <summary>Phonon (Gradium, Apache 2.0) through Gradium's own <c>ptts</c> command-line runner.</summary>
public sealed class PhononOptions
{
    public static readonly IReadOnlyList<string> DefaultVoices = ["Miles", "Harper", "Easton", "Maeve", "Preston", "Hadley"];

    /// <summary>Names from <c>ptts --list-voices</c>.</summary>
    public List<string> Voices { get; set; } = [];

    /// <summary>Path to a ptts executable, for platforms without a prebuilt download (anything but Windows x64).</summary>
    public string? Path { get; set; }

    public float Temperature { get; set; } = 0.3f;

    /// <summary>CPU threads; 0 means one per logical core.</summary>
    public int Threads { get; set; }
}

public sealed class KokoroOptions
{
    public static readonly IReadOnlyList<string> DefaultVoices = ["am_michael", "af_heart", "bm_george", "af_nicole", "am_fenrir", "bf_emma"];

    public List<string> Voices { get; set; } = [];

    /// <summary>Model precision: "fp32" (best), "quant", or "int8" (smallest).</summary>
    public string Variant { get; set; } = "fp32";

    /// <summary>"cpu". ("auto"/"directml" only work with a DirectML build of ONNX Runtime; Kokoro v1.0 fails there anyway.)</summary>
    public string Device { get; set; } = "cpu";

    /// <summary>Speaking rate. 1.0 is natural; 1.1 to 1.2 suits listening while busy.</summary>
    public float Speed { get; set; } = 1.1f;
}

public sealed class OpenAiTtsOptions
{
    public static readonly IReadOnlyList<string> DefaultVoices = ["onyx", "nova", "echo", "shimmer", "fable", "alloy"];

    public List<string> Voices { get; set; } = [];

    /// <summary>Base URL ending in /v1, for example http://localhost:8880/v1 for ptts-openai-server.</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "tts-1";

    public float Speed { get; set; } = 1.0f;

    /// <summary>Ask for raw 16-bit PCM and play it as it arrives. Needs a server that supports <c>response_format: "pcm"</c>.</summary>
    public bool Streaming { get; set; }

    /// <summary>Sample rate of the server's PCM (OpenAI and ptts-openai-server use 24 kHz).</summary>
    public int SampleRate { get; set; } = 24_000;
}

/// <summary>
/// Gradium's ptts-openai-server, run by b-speak in Docker: started on first use, reused after, and
/// streamed from. Needs Docker running; otherwise the chain steps down.
/// </summary>
public sealed class PhononServerOptions
{
    public string Image { get; set; } = "ghcr.io/gradium-ai/ptts-openai-server";
    public string Container { get; set; } = "bspeak-phonon";

    /// <summary>Port on localhost the container listens on.</summary>
    public int Port { get; set; } = 8880;

    /// <summary>Weight format inside the container: q8 (default) or f32.</summary>
    public string Quant { get; set; } = "q8";

    /// <summary>How long to wait for the container to load the model (the first start downloads it).</summary>
    public int StartTimeoutSeconds { get; set; } = 240;
}
